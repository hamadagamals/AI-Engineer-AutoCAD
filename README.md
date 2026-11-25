# AI-Engineer

> 📘 **وثيقة الهدف الشامل ودليل التشغيل (Master Product Charter):**  
> راجع [`docs/MASTER_PRODUCT_GOAL_AR.md`](docs/MASTER_PRODUCT_GOAL_AR.md) و [`GEMINI.md`](GEMINI.md) للحصول على الميثاق الكامل لتحويل AI-Engineer إلى برنامج تثبيت تجاري متكامل داخل AutoCAD و Revit مع واجهة شات ذكية ومراجعة لونية ودعم متعدد لمزودي الذكاء الاصطناعي (Gemini / Claude / Codex / Custom BYOK).

## Current Suite Overview (Phases 1–7)

- **42 AutoCAD Direct Engineering Tools (`AIEngineer.AutoCAD.Context.dll`, commands `AIENGPALETTE` / `AIENGINVENTORY` / `AIENGDIALUX`):** Commercial dark-theme card UI with status badge (`_headerBadge`), `"اوامر جاهزة / Presets"` (12 editable drafts), contextual inventory bar with instant chat shortcuts (`اوكي`/`ok`/`اضف`/`شيل`/`تحديث`/`تخطي`/`انهاء`), voice I/O, vector visual capture (`capture_cad_view`), **interactive circuit-by-circuit guided inventory (`guided_circuit_inventory`: `start`, `add_selection` broken-branch auto-trace, `remove_selection` permanent exclusion, `inspect_selection` ad-hoc lookup, `recall` instant saved-circuit reload/editing, `approve_next` numeric stacked tables, `audit_remaining` coverage + gap audit, `export_merged_panels`)**, **persistent `AIENG_CIRCUIT_TAG` XData recovery across `COPY`/`MOVE`**, **cross-plan / cross-DWG Lighting + Power numeric merge** (`project_database.json` & `.ai-engineer-project-db.json` with newest-`mtime` reload + cross-PC `.dwg` filename fallback), **full MV/LV Project Distribution Network & Master SLD (`build_distribution_network_and_sld`: `RMU -> TX -> MDB -> GEN/ATS -> SMDB -> DB / Feeder Pillar / Landscape`, CAD cable routing discovery, strict missing-load gate, IEC 60364 / IEC 60909 / ECP sizing, `_AI_MASTER_SLD` + `_AI_FEEDER_SCHEDULE`, and 4-sheet `.xlsx` export)**, **interactive DIALux pre-export review wizard (`AIENGDIALUX` / `DialuxExportReviewForm` / `export_dialux_stf` `preview_missing_only`, multi-luminaire `[LUMINAIRE.L1..Ln]`, per-room heights + `dialux_rooms` persistence)**, **strict zero-hallucination Legend spec gate (`strict_legend_specs`)**, balanced 3-phase panel schedules (`generate_panel_schedule_and_sld`), electrical QA (`audit_electrical_drawing`), IEC LV calcs (`calculate_lv_electrical`), bidirectional Excel `.xlsx` sync, and multi-step per-document Undo.
- **2D/3D Switchgear Panel Manufacturing Studio (`AIEngineer.AutoCAD.PanelManufacturing.dll`, command `AIENGPANEL` / `PANELAI`):** Dedicated dark copper-themed studio separating industrial cabinet manufacturing (`design_panel_manufacturing_layout`) from floor-plan drafting — supporting IEC 61439 thermal/fan sizing, standard DIN 43671 single & multi-run Cu busbars (`15x3 Cu` .. `2x(100x10) Cu`), magnetic DIN-rail snap (`AIENGPANELSNAP`), interactive netlist wire/device tracing (`AIENGWIRETRACE`), 8 ordered Z-depth 3D layers (`AIENGPANELVIEW`), CNC plate drilling, side cross-section, and bidirectional `SLD <-> 2D/3D Panel` sync (`AIENGPANELSLD`).
- **Multi-Version Revit Direct Copilot (`AIEngineer.Revit.Direct.dll`, Revit 2015–2026+):** Shared DPAPI-encrypted provider config (`%APPDATA%\AI-Engineer\direct_ai_config.json`), deterministic IEC/ECP LV calculator, lighting & DIALux `STFF 1.0.5` export, and red-preview safe review (`OverrideGraphicSettings`).
- **Quick Start & CI:** Run `python launcher.py check` (automatically bootstraps clean `data/*.json` templates on a fresh clone) and `python -m pytest -q`. See [`AUTOCAD_AI_TOOLS_REFERENCE_AR.md`](AUTOCAD_AI_TOOLS_REFERENCE_AR.md) and [`docs/CHANGELOG.md`](docs/CHANGELOG.md) for details.

AI-Engineer is being built as a local-first electrical-engineering copilot and
integration platform. Its primary product experience will be an Arabic/English
conversational palette inside AutoCAD: the engineer selects drawing content, asks for
inspection, drafting, quantities, calculations, or compliance review, previews the
plan and changes, approves execution, and receives traceable verified results.

A shared electrical project model will connect AutoCAD drawings to deterministic
engineering calculations, panel schedules, single-line diagrams, current cited code
evidence, manufacturer data, and later ETAP and SOLIDWORKS Electrical. AI interprets,
plans, searches, and explains; versioned tested code owns calculations and validation,
and the engineer remains the final authority. See
[Product Vision](docs/PRODUCT_VISION.md) for the full agreed scope and delivery order.

AI-Engineer is a Windows-local foundation for a safety-focused engineering and
software-development agent. Milestone 0 provides configuration, version metadata,
structured local logging, atomic JSON state persistence, and a validated application
lifecycle. Milestone 1 adds sandboxed files, bounded project scanning, fail-closed
command execution, read-only system/process inspection, confirmation-gated graceful
process termination, and read-only Git inspection.

Milestone 2 adds typed commands, exact capability permissions, durable one-use human
approvals, persistent task history, validated lifecycle transitions, cooperative
cancellation, explicit bounded retries, and a Safe Core command catalog.

Milestone 3 exposes those tasks through an authenticated, localhost-only FastAPI
service with strict models, streamed request limits, early authentication, disabled
schema/docs, and explicit approval routes.

Milestone 4 adds an injected provider adapter and a persistent, bounded developer
loop. Every provider plan and exact command payload requires human review; high-risk
commands retain the independent durable approval gate from Milestone 2. Provider
output is untrusted strict JSON and can use only registered Safe Core commands.

Milestone 5 adds a supported VS Code CLI adapter. It detects non-secret version
metadata and can open only existing configured-workspace paths after durable human
approval. On Windows the official `code.cmd` wrapper is validated and resolved to
`Code.exe` and `cli.js`, so no command shell or GUI automation is used.

Milestone 6 adds AutoCAD 2021 detection and approval-gated read-only inspection of
workspace drawing copies. The installed product, exact versioned COM ProgID, Core
Console, and secure AutoLISP capability are detected without exposing license data or
installation paths.

Milestone 7 adds a fixed-command operator launcher, reproducible portable ZIP
packages, hash-verified installation into a new directory, runtime-data recovery
snapshots, and confirmed schema-v1 layout migration with pre-change recovery.

Milestone 8 adds the first bounded AutoCAD engineering pilot: an approval-gated,
aggregate layer/entity inventory performed only on a read-only scratch copy, with
strict count limits and SHA-256 proof that both source and scratch remained unchanged.

Milestone 9 adds the first bounded writer. It creates exactly one `LINE` with finite,
bounded coordinates, saves only the scratch copy, and requires entity-count,
entity-type, clean-save, and before/after hash evidence.

Milestone 10 adds a bounded electrical wire-route primitive. It creates 1–15 verified
`LINE` segments on the fixed `E-WIRE` layer in a scratch copy, using an NCS/AIA-
inspired layer name and project-controlled color/linetype semantics.

Milestone 11 adds a versioned, user-editable electrical symbol catalog and the first
hash-approved block placement. Catalog edits fail closed until their SHA-256 is
reviewed, and placement approval is bound to that exact definition hash.

Milestone 12 adds review-only intake for user-created block sources. One named block
is inspected inside an unchanged read-only scratch copy; compatible line/circle
geometry produces a hash-approved catalog proposal without registering or importing
anything automatically.

Milestone 13 adds separately approved catalog registration. It consumes only persisted
eligible-review evidence and exact proposal/source hashes, rejects changed sources and
duplicates, creates a timestamped backup, atomically replaces the catalog, reloads the
complete document, and rolls back on post-write validation failure.

Milestone 14 validates an existing block's flags, base point, ordered geometry, layer,
and exact end before reuse. One approval can create a bounded scratch circuit fragment
with 2–8 references to one hash-approved symbol and a connected 1–15-segment `E-WIRE`
route, followed by exact count, unit, save-state, source-hash, and output-hash checks.

Milestone 15 adds verified drawing handoff. Every writable producer records immutable
run evidence for its exact output path, size, suffix, source hash, and output hash. A
separately approved handoff can create a new workspace drawing without overwrite, or
replace an existing drawing only with its approved current hash, byte-identical backup,
atomic replacement, post-write verification, and automatic rollback.

Milestone 16 adds immutable schema-v1 electrical project records and a strict,
bounded AutoCAD drawing/explicit-selection context. A read-only AutoCAD 2021 .NET
bridge exports allowlisted structured entities to a workspace inbox; hash-bound local
import maps reviewed blocks and `E-WIRE` geometry while preserving provenance and
reporting unknown meaning. Drawing context never leaves the device in this milestone.

Milestone 17 is complete. It defines immutable
Arabic/English sessions and messages bound to one exact drawing context, plus
hash-bound privacy/data-egress previews that cannot include a whole drawing or claim
transmission. Authenticated loopback routes now provide context-verified session
creation, owner-key-protected messages/results/cancellation, and exact egress review
decisions under expiry, concurrency, rate, and payload limits. Sessions are currently
process-local. A buildable dockable AutoCAD 2021 palette shell now displays the last
explicit context and bounded local conversation in Arabic or English, with progress
and cancellation. Optional provider answers require a displayed exact egress preview
and a separate approval, accept no commands, and retain citations to approved fields.
No real provider is configured by default. Live AutoCAD 2021 acceptance verified the
explicit three-object selection, palette command, `DBMOD=0`, restored security
settings, disabled provider egress, and an unchanged SHA-256 on an OS-read-only
controlled DWG.

## Requirements

- Windows 11
- Python 3.13

Install the runtime and test dependencies:

```powershell
python -m pip install -r requirements.txt
```

Run the foundation entry point:

```powershell
python main.py
```

Or use the fixed-command launcher:

```powershell
python launcher.py check
python launcher.py run
python launcher.py autocad-benchmark --runs 5 --output performance\autocad-core.json
```

A successful invocation starts and cleanly stops the lifecycle, updates
`data/agent_state.json`, and appends JSON records to `logs/ai-engineer.jsonl`.

Run validation:

```powershell
python -m compileall agent core api integrations main.py launcher.py
python -m pytest -q
```

The current unfinished work is tracked in `docs/WORK_QUEUE.md`. It contains only
remaining steps; verified completed work is removed from that queue and retained in
`docs/ROADMAP.md` and `docs/CHANGELOG.md`.

Milestone 16 establishes the product-facing architecture with the typed shared
electrical model and bounded read-only AutoCAD drawing/selection context. The embedded
palette, previewed drafting, quantities, calculations, code intelligence, and external
tool connectors build on that boundary in the documented roadmap order.

## Operator Bridge: AI inside VS Code and AutoCAD

The unreleased Operator Bridge turns the existing localhost control plane into a practical
IDE/CAD sidecar without exposing a public command server. It is designed for the workflow:

`AI side panel -> reviewed developer run -> typed command -> local permission/approval -> verification`

The VS Code client lives in `clients/vscode/`. It contacts only `127.0.0.1`, reads the
local API token from the VS Code process environment, and never receives an OpenAI API key.
It can auto-continue LOW/MODERATE local steps only when the operator checks that option;
HIGH and DESTRUCTIVE actions remain visible approval stops.

Start the local bridge without an AI reasoning provider:

```powershell
$bytes = New-Object byte[] 32
[Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
$env:AI_ENGINEER_API_TOKEN = [Convert]::ToBase64String($bytes)
python launcher.py operator --allow-project-root
```

For normal Windows use, choose one reviewed provider launcher instead of setting variables by
hand:

```text
START-AI-ENGINEER-LOCAL.bat   -> loopback Ollama, default qwen2.5-coder:7b
START-AI-ENGINEER-CODEX.bat   -> signed-in Codex CLI, reasoning-only
START-AI-ENGINEER-HYBRID.bat  -> Ollama first, Codex only on provider/contract failure
START-AI-ENGINEER-WITH-OPENAI.bat -> optional API key mode
START-AI-ENGINEER-WITH-GEMINI.bat -> optional Gemini API key mode
```

Codex is never launched in the project repository by this adapter. Each call uses an empty
temporary directory, a read-only sandbox, an output schema, and a minimal environment that
omits bridge/API/cloud credentials. The local command engine remains the only execution path.
Hybrid mode therefore changes reasoning availability/cost, not permissions.

The optional OpenAI provider still requires a separate API key/billing account and explicit
data-egress opt-in. `start-operator.ps1 -EnableOpenAI` provides the lower-level bootstrap while
keeping the entered key only in that process environment.

The Gemini path is independently opt-in. `START-AI-ENGINEER-WITH-GEMINI.bat` requests one
session-only `GEMINI_API_KEY`, requires explicit data-egress confirmation, uses only Google's fixed
Gemini Interactions endpoint, and starts the existing disposable read-only AutoCAD preview with the
explicit configured-provider switch on default port `8766`. Copilot summaries distinguish raw
top-level selected `INSERT` references from recognized electrical devices; they do not recurse into
nested blocks or Xrefs. Choose one provider launcher per bridge session; every provider
uses the same local AutoCAD approval, preview, Scratch, recovery, and verification gates.
The launcher also installs the reviewed bundled Operator extension into an isolated runtime profile
and opens a separate VS Code window connected to the same session token and port. Existing VS Code
windows can remain open and never receive the Gemini key. From that Operator panel, Gemini may plan
only registered typed commands; the live AutoCAD subset is exact document status, palette display,
and approval-gated full-drawing index, never arbitrary command text or drawing mutation.

In the AutoCAD copilot, capture an explicit selection with `AIENGCONTEXT` before starting a
session. The palette disables actions until their prerequisites exist: Start before a session,
local Send after a session, AI egress Preview after a locally stored message, and Approve/Reject
only while an exact preview is pending. A successful session is labeled `Session active`.

Official manufacturer discovery has an additional independent runtime gate:
`AI_ENGINEER_MANUFACTURER_DISCOVERY_ENABLE=1`. It also requires the existing
`AI_ENGINEER_OPENAI_ENABLE=1`, `AI_ENGINEER_OPENAI_EGRESS_CONFIRMED=1`, and environment-only
`OPENAI_API_KEY`. Search is restricted to the official domains in the reviewed project
request; AI-Engineer independently re-fetches and hashes every accepted product page. Do
not store the key in a project file, JSON request, drawing, or launcher argument.

For an exact reviewed part number, the standalone Manufacturer Asset Collector can search
approved official domains for a data sheet, 2D CAD, 3D CAD, and product images, then download
only the exact files selected by the engineer and preserve their requested order:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\START-AI-ENGINEER-MANUFACTURER-ASSETS.ps1
```

Search and download are two separate confirmations. Files remain in the quarantined
`workspace/manufacturer-assets/<manufacturer>/<part-number>/` library; collection never imports
CAD, registers a catalog item, or changes an AutoCAD drawing. See
[Manufacturer Asset Collector](docs/MANUFACTURER_ASSET_COLLECTOR.md).

See [Hybrid AI providers](docs/HYBRID_AI_PROVIDERS.md) for the complete boundary.

Install the current VS Code side panel after reviewing the destination:

```powershell
powershell -ExecutionPolicy Bypass -File .\clients\vscode\install.ps1 -Confirm
```

Restart VS Code from an environment that contains the same `AI_ENGINEER_API_TOKEN`, then open
the AI-Engineer icon in the Activity Bar. The AutoCAD `AIENGPALETTE` continues to use its
separate exact egress-preview approval before any provider question. Previewed drawing writes
remain governed by Milestone 18 and are not bypassed by Operator mode.

## Foundation layout

- `agent/config.py`: immutable, project-root-relative runtime configuration.
- `agent/exceptions.py`: application-specific error hierarchy.
- `agent/logger.py`: UTF-8 JSON-lines logging.
- `agent/state.py`: atomic JSON object persistence.
- `agent/copilot.py`: immutable local session/message and exact data-egress review
  contracts with no drawing-write or transmission behavior.
- `agent/copilot_service.py`: process-local hashed ownership, expiry, concurrency,
  operation-rate, cancellation, and metadata-only audit handling for copilot sessions.
- `agent/copilot_provider.py`: injected same-language read-only answer adapter with
  strict approved-field citations, timeout/concurrency/output bounds, and no commands.
- `agent/app.py`: explicit start/stop lifecycle.
- `data/`: versioned JSON settings, state, history, and mission data.
- `tests/test_foundation.py`: baseline behavior and failure-mode tests.
- `agent/provider.py`: bounded, callback-based provider adapter and typed requests.
- `agent/developer_agent.py`: durable reviewed plan → act → observe → verify state
  machine.
- `tests/test_developer_agent.py`: provider-boundary, review, limit, persistence,
  failure, verification, and approval tests.
- `integrations/vscode.py`: supported CLI discovery and approved workspace opening.
- `tests/test_vscode.py`: wrapper, containment, confirmation, timeout, metadata, and
  argument tests.
- `integrations/autocad.py`: bounded AutoCAD 2021 detection, inspection, inventory,
  and scratch-only writers.
- `core/electrical_model.py`: immutable schema-v1 electrical identities, objects,
  units, provenance, confidence, privacy, and explicit unknowns.
- `integrations/autocad_context.py`: strict local context validation, evidence binding,
  conservative mapping, and no-provider-egress enforcement.
- `integrations/autocad_context_plugin/`: buildable read-only AutoCAD 2021 context
  exporter for the active drawing or engineer's explicit selection.
- `integrations/drawing_handoff.py`: evidence-bound new-file and backed-up replacement
  delivery of verified AutoCAD outputs.
- `tests/test_autocad.py`: installation, containment, copy, command, marker, failure,
  and approval tests.
- `core/package_manager.py`: bounded package, installation, recovery, and migration
  services.
- `launcher.py` / `ai-engineer.cmd`: fixed operator commands without a general shell.
- `tests/test_package_manager.py`: archive integrity, containment, confirmation,
  recovery, migration, and launcher tests.
- `core/file_manager.py`: workspace-confined text reads and atomic writes with
  confirmation and backups for destructive operations.
- `core/project_scanner.py`: bounded project metadata scans that never read file
  contents or follow links and junctions.
- `tests/test_file_manager.py`: containment, junction escape, backup, confirmation,
  size-limit, encoding, audit-log, and mutation tests.
- `tests/test_project_scanner.py`: discovery, resource-limit, ignored-directory,
  junction, permission, classification, and audit tests.
- `core/terminal_manager.py`: explicit-prefix subprocess policy, workspace cwd,
  filtered environment, timeouts, process-tree termination, and bounded output.
- `core/system_info.py`: non-secret OS, Python, CPU, memory, boot, and workspace-disk
  information.
- `core/process_manager.py`: bounded non-secret process listing and confirmed graceful
  termination guarded against PID reuse.
- `core/git_manager.py`: read-only repository validation, status, diff, and log.

The remaining empty integration module scaffolds reserve future supported-tool
adapters. They are not operational implementations.

## Safe FileManager

`FileManager` accepts one or more explicit workspace roots and a separate backup
root. Relative paths are allowed only with one workspace root. Canonical path checks
reject traversal and Windows junction or symlink escapes. Reads accept UTF-8 regular
files up to a configurable byte limit. Writes use same-directory temporary files,
`fsync`, and atomic replacement.

Creating a new file needs no confirmation. Overwriting or deleting an existing file
requires an explicit confirmation flag and first creates a timestamped, metadata-
preserving backup. Directory deletion and recursive deletion are not supported.

## ProjectScanner

`ProjectScanner` uses the same workspace policy as `FileManager`. It records regular-
file metadata only and does not open file contents, so binary files are never decoded
as text. Directory links and Windows junctions are skipped, as are common dependency,
cache, Git, log, virtual-environment, and backup directories.

Independent limits bound files, directories, entries per directory, traversal depth,
and cumulative discovered bytes. Results are deterministic and JSON-serializable,
identify common project markers, classify known text/binary extensions, explain
skipped paths, and state honestly when a limit truncated the scan.

## Terminal and local inspection

`TerminalManager` starts with no implicit command permissions. Callers must provide
explicit argument prefixes. Shell executables, inline Python, dangerous Git actions,
parent traversal, out-of-workspace absolute arguments, excessive timeouts, and
unapproved environment keys are rejected. Commands run with `shell=False`, no stdin,
a filtered environment, bounded captured output, and process-tree termination on
timeout. Output is returned but never copied into audit logs.

`SystemInfo` deliberately omits hostname, username, environment, and secrets.
`ProcessManager` omits command lines, usernames, executable paths, and environments.
It cannot terminate system PIDs or itself, requires explicit confirmation and an
expected process creation time, and performs graceful termination only.

`GitManager` is read-only. It validates both the working tree and Git metadata through
the workspace policy, disables external diff and text conversion, suppresses optional
Git locks, bounds output/time, and exposes status, patch, and limited commit metadata.
It has no commit, reset, clean, checkout, push, rebase, or history-rewrite methods.

## Command and task engine

`CommandRegistry` rejects duplicate or malformed dotted command names. Every
definition includes a payload validator, permission, ordered risk level, cooperative
deadline, and handler. `CommandEngine` bounds JSON payloads and results, checks exact
or explicit namespace permissions, and never logs payload or result values.

High-risk and destructive commands create durable approval records in
`data/approvals.json`. Each approval is time-limited, tied to one task and command,
decided explicitly, and consumed exactly once. A caller-provided boolean is never
accepted as human approval.

`TaskEngine` persists tasks and transition events atomically in `data/history.json`.
Retries occur only for `RetryableCommandError`, are limited to three attempts, and
ordinary failures are not retried. Running Python handlers use cooperative
cancellation and deadlines; Safe Core subprocess commands retain hard timeouts.

The application registers twenty-four commands for system snapshots, process
listing/termination, project scanning, file read/write/delete, Git status/diff/log,
VS Code detection/opening, and bounded AutoCAD operations. Mutations and application
launches require approval. No general terminal command is registered.

## Protected local API

Generate an ephemeral token in PowerShell and keep it out of repository files:

```powershell
$bytes = New-Object byte[] 32
[Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
$env:AI_ENGINEER_API_TOKEN = [Convert]::ToBase64String($bytes)
python -m api.server --check
python -m api.server
```

The server binds exactly to `127.0.0.1:8765`, uses one worker, ignores proxy headers,
and disables access logs, docs, Redoc, and OpenAPI. Only `/health` is unauthenticated.
Call protected routes with `Authorization: Bearer <token>`; never place the token in a
URL, source file, committed settings, or shared log.

All `/v1` requests are authenticated by ASGI middleware before body parsing and again
by route dependency. Request bodies are buffered only up to the configured byte limit
and oversized declared or streamed bodies return 413. Strict Pydantic models reject
coercion and extra fields without echoing submitted values in validation responses.

See [API reference](docs/API.md) for routes and the approval workflow.

## Reviewed developer agent

`ProviderAdapter` wraps a trusted callback supplied by an embedding application. The
default CLI and `python -m api.server` still load no provider or secret. Operator mode
can inject the loopback Ollama adapter, isolated Codex CLI adapter, Ollama-first hybrid, or
optional OpenAI Responses API adapter only after their explicit runtime opt-ins. Provider
credentials/authentication are never written to repository/runtime JSON, and provider choice
never bypasses the local command/approval state machine.

An injected provider receives the goal, reviewed plan, sanitized command catalog,
and the most recent bounded task observation. It must return one strict JSON object
for the current phase. The local state machine—not the provider—validates commands,
permissions, payloads, task results, approvals, and completion evidence.

Each run follows these gates:

1. Generate a bounded plan and pause for explicit human plan review.
2. Generate one exact command proposal and pause with its payload visible.
3. After human approval, submit through `TaskEngine`; high-risk operations pause
   again for a one-use durable command approval.
4. Observe the durable task outcome and request a strict verification decision.
5. Repeat within configured limits, or accept completion only after a successful,
   accepted verification.

Runs allow 1–10 actions and derive a hard provider-call bound. Goals, plans,
proposals, run states, and audit metadata persist atomically in
`data/developer_runs.json`. Audit events and logs omit goal, payload, result, and
provider-response values.

## Visual Studio Code CLI

`VSCodeManager` looks up the supported `code` CLI from the process `PATH`. Detection
returns only installation state, semantic version, build commit, architecture, and
CLI kind; installation paths and CLI stderr are not exposed.

On Windows, `.cmd`/`.bat` wrappers are never executed. The adapter accepts only the
official wrapper shape, resolves both launch files within the same installation root,
then invokes the executable with `shell=False`, a filtered environment, captured
output, and a 15-second timeout. Opening uses `--reuse-window` and optional bounded
`--goto` coordinates. Targets must already exist within configured workspace roots.

The registered `vscode.detect` command is read-only. `vscode.open` is high risk and
therefore pauses in `awaiting_approval`; the same commands are available through the
existing authenticated `/v1/tasks` API. No extension installation, settings rewrite,
keystroke injection, terminal control, or screen automation is implemented.

## AutoCAD 2021 foundation

`AutoCADManager` discovers only the R24.0/2021 registry product and validates
`acad.exe`, `accoreconsole.exe`, and `AutoCAD.Application.24`. Detection exposes no
installation path, serial number, license value, or user registry data.

`autocad.inspect_scratch` requires durable approval. It atomically copies one bounded
workspace `.dwg`/`.dwt` into `workspace/.ai-engineer/autocad/runs/<uuid>`, then runs a
fixed metadata-only script through Core Console with `/readonly`, `shell=False`, no
stdin, a filtered environment, bounded output, timeout, and process-tree cleanup.
Original drawings are never opened or changed.

`autocad.inventory_scratch` uses the same approval and scratch boundary. Its fixed
AutoLISP expression counts only layer-table records and all database entities; it
does not return layer names, entity types, geometry, text, attributes, or paths from
inside the drawing. Count limits, `DBMOD=0`, and source/scratch SHA-256 equality are
required before success is reported.

`autocad.create_line_scratch` accepts one workspace drawing plus two three-number
points normalized to nine decimal places. It copies the source, invokes only `LINE`
and `QSAVE` against the named scratch
file, and never includes `/readonly` because the output copy is intentionally saved.
Success requires exactly one new entity, type `LINE`, `DBMOD=0` after save, unchanged
source bytes, and changed scratch bytes. The returned `output_drawing` remains under
the isolated run directory; the input is never overwritten.

`autocad.create_wire_route_scratch` accepts 2–16 validated 3D points and creates one
`LINE` for every adjacent pair. The fixed layer is `E-WIRE`, with ACI color 3 and
`CONTINUOUS` linetype. `E-WIRE` follows the NCS/AIA discipline/major-group naming
shape; the color is a documented local project choice, so this is not a claim of
complete NCS compliance. Success requires exact segment growth, every new entity on
the fixed layer, a clean save, unchanged source bytes, and changed scratch bytes.

`autocad.list_electrical_symbols` validates and lists the packaged, user-editable
`plugins/electrical_symbols/catalog.json`. The starter `socket_single` definition is
three bounded line/circle primitives in millimeters, block
`AIENG_E_SOCKET_SINGLE_V1`, inserted on `E-POWR-DEVC`. Every definition includes a
canonical SHA-256; changing geometry, units, names, or layer policy without reviewing
and updating the hash makes the catalog unusable rather than silently trusted.

`autocad.place_electrical_symbol_scratch` requires the exact catalog symbol ID and
definition hash plus a bounded insertion point, rotation, and uniform scale. Durable
approval is therefore tied to the reviewed definition. It creates a new block only
when the same name is absent, verifies its geometry count, creates exactly one
`INSERT`, verifies block/layer/transform and zero attributes, saves once, and requires
source/output hash evidence. Existing same-named blocks fail closed to prevent silent
redefinition.

Symbol placement now also requires drawing `INSUNITS=4` because the current catalog
contract is millimeters. `autocad.review_electrical_symbol_source` accepts a workspace
DWG/DWT, one safe block name, and proposed catalog metadata. After durable approval it
uses `/readonly`, traverses at most 32 definition entities, and reports drawing units,
block flags/base point, line/circle/unsupported/layer/attribute/nested-block counts,
truncation, source SHA-256, and explicit eligibility reasons. Eligible definitions
produce a complete proposal and canonical hash; the catalog itself is not changed.

`autocad.register_electrical_symbol_review` accepts only a 32-character review run ID
and the exact proposal/source SHA-256 values shown by the review. It re-reads the
persisted `symbol-review.json`, re-hashes the reviewed source, requires a separate
durable approval, rejects duplicate symbol IDs or block names, backs up the current
catalog under `backup/symbol-catalog`, writes atomically, and reloads the complete
catalog. A failed post-write check restores the original bytes.

`autocad.create_electrical_circuit_fragment_scratch` accepts one catalog symbol/hash,
2–8 bounded point/rotation/scale transforms, and 2–16 connected wire points. Existing
block definitions are reused only after their header and every ordered `LINE`/`CIRCLE`
match the approved catalog geometry with no extra entity. The fixed script creates all
references and 1–15 `E-WIRE` segments, saves once only after exact verification, and
returns both source and output SHA-256 values. The original drawing is never saved.

Every successful writable AutoCAD run also atomically creates `drawing-output.json`
inside its isolated run directory. `autocad.handoff_verified_output_new` accepts only
that run ID, its exact output SHA-256, and a workspace destination with the same
`.dwg`/`.dwt` suffix. It refuses existing files, internal `.ai-engineer` paths,
producer sources, arbitrary caller source paths, traversal, and link escapes.

`autocad.replace_with_verified_output` is a distinct capability and approval. It also
requires the destination's current SHA-256, creates a timestamped byte-identical
backup under `backup/autocad-handoff`, detects changes immediately before replacement,
uses same-directory staging and atomic replacement, verifies the installed bytes, and
restores the backup automatically when post-write verification fails.

The real read-only path is verified against a copied Autodesk `acad.dwt` under the
interactive Windows user: AutoCAD 2021 returned version `24.0`, the expected drawing
metadata, and `DBMOD=0`. A sandbox service account cannot reuse another Windows
user's AutoCAD profile, so production Core Console execution must run as the
initialized interactive user. See [AutoCAD integration](docs/AUTOCAD.md).

The live inventory pilot also succeeded on that template: one layer, zero entities,
`DBMOD=0`, unchanged source and scratch hashes, and no truncated output.

The live writer pilot created a line from `(0,0,0)` to `(1000,0,0)` in a scratch DWT:
the entity count changed from zero to one, the source hash stayed unchanged, the
output hash changed, and the saved drawing returned `DBMOD=0`.

The live electrical pilot created a two-segment route through `(0,0,0)`,
`(1000,0,0)`, and `(1000,500,0)` on `E-WIRE`. Entity count changed from zero to two,
both segments and the layer schema were verified, `DBMOD=0`, the source stayed
unchanged, and only the scratch output changed.

The live symbol pilot created `AIENG_E_SOCKET_SINGLE_V1` and inserted one reference
at `(2000,1000,0)` on `E-POWR-DEVC`. Entity count changed from zero to one,
`DBMOD=0`, the source remained unchanged, and only the scratch output changed.

The live intake pilot used the Autodesk millimeter `acadiso.dwt`, placed the starter
block, then reviewed its resulting source read-only. It recovered one circle and two
lines on layer 0, found zero attributes/nested/unsupported entities, returned
`eligible=true`, and produced a proposed catalog hash while both review files remained
byte-identical.

The live handoff pilot produced a two-reference, one-wire circuit from the Autodesk
millimeter template, created `workspace/autocad-live/ai-engineer-beta-0.16.0.dwt`
without overwrite, and independently inspected and inventoried it as three entities
on three layers with `DBMOD=0`. Its 32,725 bytes matched SHA-256
`11fddeb136d129b2f6c38312c9780e2080519bb393edcf03e9528bf9fb1c4682`; the template
and producer output remained byte-identical through verification.

## Portable package and recovery

Build a clean package that excludes local logs, backups, work files, task history,
approvals, developer runs, and API tokens:

```powershell
python launcher.py package --output dist/ai-engineer-0.18.0.zip
python dist/ai-engineer-0.18.0.zip install dist/ai-engineer-0.18.0.zip D:\AI-Engineer --confirm
python D:\AI-Engineer\launcher.py check
```

Run compileall and the full pytest suite in the reviewed source tree before packaging. Portable
archives intentionally omit tests; the installed `launcher.py check` verifies runtime schemas and
the required AutoCAD product entrypoints/assets.
On an AutoCAD workstation, run `python launcher.py autocad-readiness` for a machine-readable,
read-only prerequisite report before using an AutoCAD launcher. The `autocad-benchmark` command
measures local deterministic cores only and does not replace live AutoCAD performance acceptance.

Installation requires a new destination and validates every archived file against
the SHA-256 manifest before writing. The manifest detects corruption or modification;
it is not a publisher signature and does not prove who created the ZIP.

Recovery snapshots contain allowlisted runtime JSON and require explicit confirmation
to restore. A pre-restore backup is created first. Although secret-like keys are
rejected, task and mission text may still be sensitive, so snapshot archives must
remain private. Migration creates its own pre-change snapshot and is idempotent.

See [Packaging, migration, and recovery](docs/PACKAGING.md) for transfer and
multi-computer instructions.

## Safety boundary

The control plane remains authenticated localhost-only and exposes no unrestricted shell or
public remote-control surface. Optional reasoning providers are runtime opt-in: Ollama is
restricted to loopback, Codex runs as an isolated read-only reasoning process outside the
project, and the OpenAI API adapter uses its fixed reviewed endpoint. None of these providers
receives direct execution authority; plans and exact actions still pass through local command
validation, review, approval, backup, and verification gates.

Engineering calculations and compliance are being built as deterministic/evidence-backed
layers rather than model-memory claims. See [Hybrid AI providers](docs/HYBRID_AI_PROVIDERS.md)
and [Engineering assurance plan](docs/ENGINEERING_ASSURANCE_PLAN.md).

See [Architecture](docs/ARCHITECTURE.md), [Roadmap](docs/ROADMAP.md), and
[Changelog](docs/CHANGELOG.md).
The current honest AutoCAD beta boundary is maintained in
[AutoCAD supported-feature matrix](docs/AUTOCAD_SUPPORTED_FEATURES.md).
