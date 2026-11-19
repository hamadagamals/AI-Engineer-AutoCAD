# Architecture

## Current foundation

The executable boundary is `main.py`. It constructs `agent.app.Application`, starts
the lifecycle, reports successful initialization, and stops cleanly. Expected
application failures return exit code 1 with an actionable message; successful runs
return 0.

`Application` owns four foundation concerns:

1. `AppConfig` resolves every path relative to the supplied or discovered project
   root and validates `data/settings.json`.
2. `configure_logging` writes UTF-8 JSON Lines records to
   `logs/ai-engineer.jsonl` without attaching duplicate handlers.
3. `StateStore` validates JSON-object state and writes changes through a temporary
   file in the destination directory, flushes and synchronizes it, then atomically
   replaces the destination with `os.replace`.
4. Lifecycle transitions permit `created -> running -> stopped`; invalid transitions
   raise `LifecycleError`. Start and stop timestamps are timezone-aware UTC values.

The configuration and persistent files have explicit `schema_version` fields. State
records the application version, status, invocation count, and latest lifecycle
timestamps. Mission and history are valid JSON objects rather than executable state.

## Target product architecture

`docs/PRODUCT_VISION.md` defines the target product. The existing Python application
remains the local control plane for lifecycle, permissions, approvals, tasks, provider
review, evidence, persistence, and authenticated localhost APIs. Product-facing
engineering features must be layered over it rather than bypassing its controls.

The central domain boundary is a typed electrical project model. Stable project object
identities link sources, transformers, switchboards, panels, circuits, protection,
cables, conductors, terminals, loads, earthing, routes, drawings, calculations,
requirements, and manufacturer components. Every value carries units, provenance,
validation state, and source-application references. Unknown or contradictory data is
represented explicitly and is never silently completed by a language model.

AutoCAD's future in-process .NET palette is a narrow client and adapter, not a second
authority. It captures only the active document or engineer's explicit selection,
serializes allowlisted bounded entity/layer/block/attribute/geometry context, and uses
the authenticated loopback API. Read access is delivered before write access. Later
writes require a visible preview and execute through supported undoable transactions,
scratch operations, or the verified handoff boundary.

Provider adapters receive only explicitly approved context. Local policy classifies
and previews data egress, bounds content, records metadata without drawing contents,
and permits a project to disable cloud transmission. Provider output remains untrusted
and cannot directly operate AutoCAD, run calculations, select code editions, or invoke
external engineering applications.

Deterministic calculation services own formulas, units, rule versions, test vectors,
and numerical results. The AI maps intent, identifies missing inputs, proposes work,
retrieves evidence, and explains output. Quantity, voltage-drop, cable-sizing,
protection, panel, and short-circuit results must be reproducible without a provider.

Milestone 21 starts with `core.panel_sld`, an immutable contract boundary between the
shared model, a future panel schedule, and a supported single-line representation. The
contract retains the exact project/document/context evidence and model hashes, makes each
outgoing way own one circuit and one protective device, distinguishes reviewed load ways
from explicit spares, and requires the SLD to preserve the same circuit identities through
panel-to-protection-to-load edges. Contract creation is read-only and rejects incomplete
or conflicting relationships before schedule generation, extraction, preview, or drawing
execution is available.

`core.panel_schedule` consumes only a contract that revalidates against the exact current
typed model. It deterministically orders one row per outgoing way and copies circuit,
protection, load, phase, pole, source, and spare/load identities into a self-hashed
read-only result. The generator has explicit false extraction and mutation flags. The
existing AutoCAD mapper still leaves panel semantics empty rather than guessing from
generic geometry; a bounded supported extraction adapter is the required next boundary
before this flow is exposed through the live authenticated API.

`core.panel_sld_extraction` now provides that initial supported adapter through fixed
`AIENG_M21_*_V1` marker blocks and exact attribute schemas. It scans at most 512 explicit
markers from one hash-bound context, rejects truncated evidence as incomplete, and never
classifies arbitrary blocks by proximity or model inference. Complete observations create
a new immutable semantic model, contract, and schedule; any missing or conflicting fact
returns bounded `UNKNOWN`/`CONFLICT` findings with no partial semantic payload. The
authenticated `/v1/panels/extract-schedule` route re-imports the local context server-side,
performs no provider egress, and has no drawing-write authority.

Extraction profile v5 resolves each load's atomic `LOAD_BINDINGS` AutoCAD
handle against recognized devices in the exact model context. This removes dependence on
selection enumeration and fails closed for invalid, absent, or unrecognized handles. The
separate high-risk fixed-fixture command is a hash-bound scratch producer with no caller-
supplied marker content; its output remains subject to independent handoff approval.

The in-process `Panels / SLD` palette remains a narrow client of that endpoint. It keeps
the current context/evidence identity, reviewer, and assumption visible; validates the
returned extraction and schedule hashes/flags before display; and never receives an
AutoCAD write method. Incomplete extraction is a reviewable domain result, not false
success: the palette requires null semantic model/contract/schedule values and renders the
bounded UNKNOWN/CONFLICT findings.

A code-evidence service uses a project-pinned jurisdiction and edition profile. It may
retrieve only official, lawfully licensed, subscribed, or user-provided licensed
sources; it stores citations, clause identity, edition, retrieval date, content hash,
and extracted rule evidence. New editions trigger review and never change active rules
silently. Manufacturer evidence follows the same provenance pattern and quarantines
downloaded documents or CAD assets before review.

ETAP, SOLIDWORKS Electrical, Excel, and later connectors translate only between their
reviewed capability surface and the shared model. Each connector begins read-only,
uses supported APIs/SDKs or validated exchange formats, preserves external identity,
detects conflicts, and requires explicit approval for writes. Mouse/keyboard automation
is not a normal connector architecture.

## Safe file boundary

`core.file_manager.FileManager` is the first Milestone 1 service. It canonicalizes
configured workspace roots and every requested path before access. Relative paths
are unambiguous only when one root is configured. Paths outside a root—including
those redirected through a Windows directory junction or symbolic link—are rejected.
The backup tree must not contain, or be contained by, a workspace tree.

The service supports bounded UTF-8 text reads, atomic creates and overwrites, and
single-file deletion. Existing content cannot be overwritten or deleted without an
explicit confirmation flag. Each confirmed destructive operation first copies the
file metadata and content to a timestamped backup through an atomic temporary-file
replacement. It never performs recursive deletion. Successful actions, denials, and
operating-system failures emit structured audit events when application logging is
configured.

Path validation substantially reduces accidental and policy-violating access. It
does not claim to eliminate every time-of-check/time-of-use race against a separate
malicious process that can replace filesystem objects concurrently; stronger Windows
handle-based enforcement can be added if hostile local processes enter the threat
model.

## Project scanning boundary

`core.project_scanner.ProjectScanner` depends on `FileManager` for workspace-root
validation. It walks metadata iteratively without following symbolic links or Windows
junctions and never opens file contents. This prevents accidental binary decoding and
keeps scanning read-only.

`ScanLimits` independently bounds file count, directory count, entries retained from
one directory, depth, and cumulative file size. The per-directory entry limit is
applied before sorting so a single enormous directory cannot force unbounded memory
use. Results contain sorted relative paths, sizes, suffix classifications, extension
counts, common project markers, skipped-path reasons, and explicit truncation state.
Permission and operating-system errors skip only the affected path and are recorded
in the audit log; an invalid or out-of-workspace scan root fails the operation.

## Command execution boundary

`core.terminal_manager.TerminalManager` accepts argument vectors only and always uses
`shell=False`. `CommandPolicy` is fail-closed and matches explicit command prefixes;
shells, inline Python, and dangerous Git actions are permanently denied. The working
directory and absolute path arguments pass through `FileManager`, while parent
traversal is rejected.

Child processes receive no stdin and only a small environment allowlist. Separate
reader threads continuously drain stdout and stderr to avoid pipe deadlock while
retaining only configured byte limits. Timeout handling terminates the spawned
process tree through `psutil`. Audit records contain the executable and operational
metadata, never command argument values or captured output.

This boundary is a policy-controlled runner, not an operating-system security
sandbox. Allowed programs may still have capabilities beyond the workspace, so only
narrow, reviewed prefixes should be registered by the future command engine.

## System and process boundary

`SystemInfo` exposes read-only OS, Python, CPU, memory, boot-time, and configured-
workspace disk usage. It omits machine hostname, user identity, environment values,
and network information.

`ProcessManager` lists a bounded set of PID, name, state, creation time, and RSS
metadata. Command lines, executables, users, and environments are omitted. Graceful
termination protects system PIDs and the current process, requires explicit
confirmation, verifies process creation time to mitigate PID reuse, and never
escalates to force-kill.

## Git boundary

`GitManager` exposes read-only branch, porcelain status, bounded diff, and limited log
metadata. Both the work tree and absolute Git metadata directory must remain within a
configured workspace. Diff disables external tools and text conversion, optional Git
locks are disabled, output and time are bounded, and author email/body data is not
returned. No mutating Git method exists in this milestone.

## Command, permission, and approval boundary

`CommandRegistry` stores immutable typed definitions with lowercase dotted names.
Each definition declares its validator, permission, risk, cooperative timeout,
approval lifetime, and handler. `PermissionPolicy` grants exact capabilities or
explicit namespace wildcards.

Risk at or above the configured threshold requires `ApprovalManager`. Approval
records are atomic JSON, expire, bind to one task and command, and transition from
pending to approved/denied and then one-use consumed. Approval identifiers are audit
references, not remote authentication tokens.

`CommandEngine` bounds input and output JSON, omits their values from logs, and gives
handlers a cancellation/deadline context. The built-in catalog exposes ten Safe Core
operations. Mutating file/process commands are high or destructive risk; a general
terminal command is intentionally absent.

## Task lifecycle boundary

`TaskEngine` writes tasks and append-only transition events atomically. Allowed states
are queued, awaiting approval, running, succeeded, failed, and cancelled, with
explicit transition validation. It prevents concurrent duplicate runs, caps history,
bounds errors, and hides unexpected exception detail from durable state and logs.

Cancellation is immediate before execution and cooperative during in-process
handlers. Retries are limited to three and only occur for
`RetryableCommandError`. Python threads are not force-killed; subprocess handlers
retain `TerminalManager` hard timeouts and process-tree cleanup.

## Local API boundary

`api.server.create_app` owns a fresh `Application` through FastAPI lifespan. The
production runner binds exactly to `127.0.0.1`, runs one worker, ignores proxy
headers, disables access logs and schema/documentation routes, and validates the Host
header. The authentication token comes only from process environment and is redacted
from configuration and middleware representations.

Only `/health` is public. An outer ASGI Bearer middleware authenticates every other
request before buffering or parsing its body; protected routers repeat authentication
as defense in depth. Token comparison is constant-time. No CORS policy is enabled.

The body-limit middleware checks declared length and pre-buffers streamed request
messages only through a configured maximum before replaying them to FastAPI. Strict
Pydantic models reject coercion and unknown fields. Validation handlers omit input
values, unexpected errors return generic text, task responses omit submitted payload,
and security headers prevent caching, MIME sniffing, embedding, and referrer leakage.

All operations route through the existing command, permission, approval, and task
engines. Convenience file and scan routes are task adapters, not direct filesystem
access. High-risk routes return awaiting-approval tasks and cannot decide their own
approval.

## Developer-agent boundary

`agent.provider.ProviderAdapter` wraps one explicitly injected synchronous callback.
The adapter validates a non-secret provider identifier, redacts callback internals
from its representation, bounds UTF-8 response bytes, and converts provider failures
to project exceptions without retaining their messages. The core adapter contains no
credential store or default fake provider. A separate optional Operator Bridge can inject
a fixed-endpoint OpenAI Responses API transport after explicit runtime enablement and
data-egress confirmation.

`DeveloperAgent` owns a durable state machine backed by atomic
`data/developer_runs.json` storage. A run first requests a strict JSON plan and always
enters `awaiting_plan_review`. Approval permits one strict action proposal; that exact
command, rationale, risk, and payload then enter `awaiting_action_review`. Denial at
either human gate cancels without execution.

Approved actions are revalidated and submitted only through `TaskEngine`. Registered
command permissions remain authoritative, unknown commands fail closed, and
high/destructive risk still creates a separate one-use `ApprovalManager` record. The
provider cannot decide or consume any approval. A decided command approval can be
resumed through the developer state machine and existing task engine.

The observation contains only the durable task identifier, command name, state,
bounded result, and bounded error. Verification is a strict boolean decision plus a
bounded reason. Completion is rejected until at least one task succeeded and its
latest observation was accepted. Actions, provider calls, goals, response bytes,
plans, text fields, persistent runs, and events all have independent limits. Logs and
developer audit events contain identifiers, state, and command names but omit goal,
payload, task-result, and provider-response values.

The protected API exposes developer provider status and run creation, retrieval,
listing, plan review, action review, advance, and cancellation. Routes exist when no
provider is configured but creation returns 503. `python -m api.server` intentionally
does not load a provider or secret. `launcher.py operator` is the explicit embedding
application: when enabled it may inject the reviewed OpenAI transport, fixed to the
official Responses endpoint, with API key held only in environment, redirects/proxies
disabled, and a separate data-egress opt-in.

## Operator Bridge boundary

Operator mode does not create a public remote-control endpoint. It reuses the authenticated
loopback API and permits extra workspace roots only through explicit runtime scope. When a
workspace root would contain the repository's normal backup directory, Operator mode uses
an external backup vault so the agent cannot read or modify its own recovery evidence.

The command catalog adds two development primitives rather than an unrestricted shell:
`python.compileall` is MODERATE and `python.pytest` is HIGH. Both execute fixed
`sys.executable -m ...` prefixes through `TerminalManager`; shells, inline Python, dangerous
Git actions, unbounded environment injection, and outside-workspace absolute arguments remain
denied. The VS Code side panel is a localhost client of the existing developer state machine;
it may auto-review only LOW/MODERATE actions after the operator opts in and never auto-decides
HIGH/DESTRUCTIVE durable approvals.

The OpenAI transport is optional. It receives only the already-defined provider request or an
AutoCAD copilot's separately approved exact egress fields. Provider output is still untrusted
and must pass the existing strict local parsers before any command can reach TaskEngine.

## Visual Studio Code boundary

`integrations.vscode.VSCodeManager` is a narrow adapter for Microsoft's supported
`code` CLI. It discovers the CLI from `PATH`, validates bounded version output, and
returns no installation paths or diagnostic stderr. A missing CLI is an honest
`installed: false` result.

Windows installations commonly expose a `code.cmd` wrapper. The adapter reads at
most 64 KiB, accepts only the official two-target wrapper shape, confines the
resolved `Code.exe` and `cli.js` to one installation root, and executes them directly
with `shell=False`. The child receives a filtered environment, no stdin, bounded
captured output, and a timeout. Other native `code` launchers are invoked directly.

Open requests accept only existing regular files or directories resolved through
`FileManager`. Line and column navigation is bounded and applies only to files. The
manager itself requires confirmation, while the registered `vscode.open` command is
also high risk and consumes a durable one-use approval before the handler can run.
`vscode.detect` is low-risk read-only metadata. Both commands flow through the
existing authenticated tasks API and developer-agent review loop.

This is application launching, not GUI automation. No extension installation,
settings mutation, editor input injection, VS Code terminal execution, or hidden
desktop control exists.

## AutoCAD 2021 boundary

`integrations.autocad.AutoCADManager` searches only the Windows AutoCAD R24.0 product
registry and validates that `acad.exe` and optional `accoreconsole.exe` remain beneath
the reported installation root. It checks the exact version-dependent
`AutoCAD.Application.24` registration. Public status omits all paths, user/profile
data, serial numbers, and license values.

The first executable capability is not arbitrary COM or command input. An approved
scratch inspection resolves one `.dwg`/`.dwt` through `FileManager`, bounds it to 256
MiB, and atomically copies it into a UUID workspace run. Core Console receives the
copy, a fixed source-controlled metadata script, `/readonly`, no stdin, a filtered
environment, a hard timeout, bounded stream capture, and process-tree termination.
The original path is never passed to AutoCAD.

The script only reads five allowlisted system variables. No caller or provider can
add script text. Success requires exit code zero and every strict output marker.
Bounded diagnostic streams stay in the scratch run and are omitted from API results
and structured logs. Failure retains recoverable diagnostic artifacts and identifies
the run without false success.

AutoLISP and ActiveX are detected future integration methods. The application never
changes `SECURELOAD`, `TRUSTEDPATHS`, Registry profiles, open documents, or AutoCAD
session visibility. The read-only Core Console path is verified under the initialized
interactive Windows user. Windows profiles are per-user: a service or sandbox account
cannot borrow another user's AutoCAD profile and must fail closed rather than create
Registry state. The inspection script is one fixed `progn` expression ending with a
silent `princ`, and the parser recognizes markers only on complete output lines so
Core Console command/return echoes cannot be mistaken for evidence.

`autocad.inventory_scratch` is a separate high-risk command using the same scratch
runner. Its fixed expression counts `LAYER` table records with `tblnext` and all
database entities with one `ssget "_X"` selection set plus `sslength`. It releases the
selection set and emits aggregate integers only—never layer names, entity types,
handles, text, attributes, or geometry. Configured count limits and `DBMOD=0` are
mandatory. SHA-256 is accumulated during the atomic copy, then both the original
source and scratch copy are re-hashed after Core Console exits; mismatches fail closed
before a result is returned.

Milestone 16 adds an independent read-only in-process boundary for structured drawing
context. A fixed AutoCAD 2021 .NET assembly accepts only the active drawing or the
engineer's explicit selection, reads allowlisted entity fields with `ForRead`, applies
independent entity/layer/attribute/point/text/payload/nesting/duration limits, and
atomically emits schema-v1 JSON beneath the configured workspace. The Python boundary
requires durable approval plus the exact context ID and SHA-256, rejects malformed or
ambiguous evidence, and conservatively maps observations into immutable electrical
records. Privacy classification and `provider_egress_allowed=false` are mandatory;
there is no provider call, arbitrary command, drawing write, or implicit selection.

Milestone 17 begins with provider-independent local contracts in `agent/copilot.py`.
Each immutable session is bound to one exact context ID and evidence SHA-256, supports
only Arabic or English bounded messages, validates chronological history and expiry,
and hard-codes `drawing_write_allowed=false`. Optional provider data must first be
represented as exact privacy-classified preview fields with values, byte counts,
per-field hashes, an aggregate hash, destination, and decision window. Review approval
is only a local record: it neither transmits data nor permits whole-drawing egress.
The authenticated loopback routes validate the local context ID and SHA-256 before
creation, return a high-entropy owner key once, and retain only its SHA-256. Every
subsequent session/message/cancellation/egress/result operation requires both the API
Bearer token and that owner key. The process-local manager serializes mutations,
expires sessions, limits active concurrency and per-session operations, and logs only
identities/counts/byte sizes/decisions. Provider execution remains absent until its
remaining queue boundaries are implemented and tested.

The same reviewed x64 AutoCAD assembly now includes `AIENGPALETTE`, a WinForms control
hosted by Autodesk's `PaletteSet`. It consumes only the most recent context evidence
created by the fixed context commands, then calls fixed copilot endpoints at literal
loopback with proxy use disabled. The token comes only from the AutoCAD process
environment and the returned owner key remains in control memory. The UI bounds input
and responses, shows counts and uncertainty, and offers local progress/cancellation.
It never opens database objects for write, sends AutoCAD command text, launches a
process, or changes the drawing/view. Local message submission does not contact a
provider. A separate review action displays the exact proposed values, byte counts,
and preview SHA-256; only a second approve-and-ask action can call the answer route.

The injected `CopilotProviderAdapter` receives only fields from one approved, unexpired,
unused review and has a fixed timeout plus bounded call concurrency and output bytes.
Provider JSON has exact `language`, `answer`, and `citations` fields—no command/action
shape—and citations must name approved preview fields. A validated answer records the
provider, session, context, review, preview hash, citations, performed egress, and
`drawing_write_performed=false`. Failure consumes the review to prevent silent repeated
egress. No provider is configured by default.

Milestone 18 drafting uses two separate representations. The durable lifecycle retains
only proposal/validation/model/catalog hashes, owner-key SHA-256, status/audit metadata,
and—after preview—its ID, SHA-256, and expiry. The transient authenticated preview response
contains the exact proposal and every ordered layer/device/terminal/circuit/route needed
for display. Preview creation re-imports the bound local AutoCAD evidence and reloads the
catalog before deterministic validation. Review cannot start until this exact preview is
bound; expiry, ownership mismatch, changed hashes, replay, or blocked validation fail
closed. Preview never calls a drawing writer and hard-codes mutation/execution approval
to false.

`autocad.create_line_scratch` is the first bounded writer and has its own durable
high-risk approval. Its validator accepts only a source path and two distinct finite
3D points within ±1e9, normalized to nine decimal places. Numeric values are rendered
into a fixed AutoLISP expression;
no caller text becomes a command, symbol, layer, output path, or format. The scratch
runner deliberately omits `/readonly` only for this operation, invokes `LINE` once,
checks the last entity type and before/after counts, and invokes `QSAVE` on the already
named copy. Source-hash equality, scratch-hash inequality, exactly one added entity,
type `LINE`, complete markers, and post-save `DBMOD=0` must all hold.

`autocad.create_wire_route_scratch` extends the same writable-copy mechanism with a
single fixed electrical schema: `E-WIRE`, ACI color 3, and `CONTINUOUS`. Its validator
accepts 2–16 finite bounded points and no layer or command text. The fixed expression
creates or validates the layer, restores the previous current layer, emits 1–15
`LINE` entities, checks every new entity's DXF type and layer, and saves once. Exact
count growth, clean-save state, source-hash equality, and scratch-hash inequality are
mandatory. The layer naming is NCS/AIA-inspired; color 3 is a local policy and the
implementation does not claim complete standards compliance.

The maximum execution release gate uses a separate non-production operation rather
than weakening those drafting bounds. `autocad.create_maximum_performance_fixture_scratch`
accepts only a source path and exact reviewed SHA-256, creates exactly 1,792 fixed lines
on a reserved previously absent layer, verifies the count increase and clean save, and
writes source/output-bound recovery evidence. Its evidence explicitly disables handoff,
so the performance fixture cannot become a general bulk drawing command.

`integrations.electrical_symbols` treats the packaged JSON catalog as validated data,
not executable plugin code. It bounds bytes, entry and geometry counts, requires exact
fields and types, restricts names/units/layers/linetypes/primitives, normalizes finite
coordinates, and verifies a canonical SHA-256 over every definition. Catalog listing
exposes only bounded metadata. Placement requests repeat the hash, preventing a
reviewed task from silently resolving to later-edited geometry.

`autocad.place_electrical_symbol_scratch` uses the writable scratch runner and refuses
to redefine an existing same-named block. The fixed expression creates a `BLOCK` →
allowlisted geometry → `ENDBLK` sequence, then one `INSERT`; it verifies definition
existence, exact geometry count, reference type/name/layer/transform, the catalog's
empty attribute contract, one-entity model-space growth, clean save, and both hash
states. No command/API payload field is rendered as executable script text.

The placement script now checks `INSUNITS=4` before block creation because catalog
geometry is explicitly millimeter-based. A different or unitless drawing fails closed
without a saved scratch result.

`autocad.review_electrical_symbol_source` is a separate read-only intake boundary. A
strict safe block name is rendered into a fixed source-controlled expression; no
caller script is accepted. The expression uses `tblsearch`, `tblobjname`, `entnext`,
and `entget` to traverse at most 32 definition entities and emits numbered bounded
markers. Python independently validates marker sequence, numeric bounds, aggregate
counts, supported geometry, units, flags, layers, attributes, nesting, and `DBMOD=0`.
Only an eligible definition becomes canonical catalog JSON with SHA-256. Both drawing
hashes must remain unchanged, and no registration write occurs in this milestone.

Successful and ineligible reviews persist a bounded `symbol-review.json` beside the
scratch drawing through `FileManager` atomic creation. Registration accepts only its
run ID plus exact proposal/source digests and locates evidence under configured
workspace roots. It revalidates schema, eligibility, proposal digest, source path, and
current source bytes before calling the catalog writer.

The catalog writer parses both current catalog and proposal, rejects duplicate IDs or
block names, stages and validates the entire next document, detects concurrent source
changes, and creates a new timestamped backup. `os.replace` atomically installs the
staged file. A complete post-write reload is mandatory; failure atomically restores
the original bytes while retaining the backup. Registration has its own
`autocad.catalog.write` capability and durable high-risk approval.

Existing drawing block reuse is fail-closed. The fixed AutoLISP compares block flags,
base point, every ordered `LINE`/`CIRCLE` on layer 0, geometry count, and immediate
block termination against the hash-approved catalog object. No caller geometry or
script is accepted. The same verifier is shared by single placement and the bounded
circuit-fragment writer.

A circuit fragment is one approved scratch transaction containing 2–8 transforms for
one catalog definition and a connected route of 1–15 `E-WIRE` segments. Layer schemas,
millimeter units, transforms, endpoints, per-type valid counts, total model count, and
`DBMOD=0` must all agree before success. `QSAVE` occurs once after validation. Python
independently confirms that the source digest stayed fixed and that the output digest
changed, and returns both digests.

Every successful writable producer atomically persists exact schema-v1
`drawing-output.json` evidence beside its output. `integrations.drawing_handoff`
resolves evidence only from a configured workspace run ID and revalidates its fixed
path, supported suffix, byte count, output digest, and unchanged producer source. No
handoff accepts caller-selected source bytes, script text, or executable input.

New-file handoff stages bytes in the destination directory and uses an exclusive hard
link operation so a concurrently created destination cannot be overwritten. It then
revalidates size and SHA-256. Replacement is a separate permission and durable
approval bound to the destination's expected current digest. It verifies that digest,
creates and verifies a timestamped backup outside the workspace, checks again for a
concurrent change, atomically replaces, verifies, and restores the exact backup bytes
if post-write validation fails. Producer sources, scratch paths, internal run storage,
path traversal, and symlink/junction escapes are rejected.

## Code-profile evidence boundary

`core.code_profile` owns the initial Milestone 22 local trust contract. A project code
profile pins reviewed jurisdiction, project scope, authority precedence, source edition,
effective dates, access basis, locator, retrieval date, content SHA-256, requirements,
reviewer, and assumptions in a canonical self-hashed record. It stores no full protected
text and performs neither retrieval nor compliance evaluation. Edition discovery creates
a separate self-hashed assessment bound to the active profile; it can require impact
review but cannot activate or mutate the pinned edition. Later clause/rule evidence and
compliance findings retain these exact profile/source hashes.

`core.compliance_evidence` adds an offline deterministic trust layer rather than treating
a language model or retrieval provider as authority. Reviewed clause records retain only
a bounded rule summary and exact locator, edition, retrieval date, source hash, profile
hash, reviewer, and assumptions. Self-hashed rules compare canonical decimal strings in
matching explicit units against self-hashed engineering facts whose upstream model or
calculation result is also hashed. Missing evidence or facts becomes `UNKNOWN`; competing
rules, duplicate fact identities, and unit mismatches become `CONFLICT`. Either state
keeps the result incomplete and unable to satisfy requirements. A completed `FAIL` is
still distinguishable from incomplete evidence. This layer performs no network retrieval,
regulatory approval, or AutoCAD mutation.

The authenticated local API exposes this evaluator only through strict bounded JSON at
`POST /v1/compliance/evaluate`. Every nested record is reconstructed with exact fields and
its canonical hash is rechecked before evaluation; the API does not accept a caller's
claimed result. The AutoCAD `Code Compliance` palette is a read-only review surface: it
loads one engineer-selected bounded JSON request, calls only the loopback API, verifies the
returned profile/source/evidence/result hashes and negative action flags, then displays
citations and all four finding states. It neither opens the drawing database for write nor
invokes an AutoCAD execution command.

## Manufacturer-evidence and asset boundary

`core.manufacturer_evidence` starts Milestone 23 with an immutable local trust contract.
One self-hashed bundle binds approved official manufacturer domains, exact product identity,
artifact provenance and content hash, lawful access/license metadata, and bounded reviewed
property claims with explicit product applicability. An official-public locator must use
HTTPS and its exact host must be approved in the manufacturer identity. CAD assets are
invariantly quarantined at this stage, executable content is rejected, and the bundle grants
no retrieval, provider egress, symbol-catalog registration, or drawing mutation authority.

`integrations.manufacturer_retrieval` performs only a separately approved exact-URL download
into a caller-configured quarantine. Its self-hashed request pins the full HTTPS locator,
exact host, media allowlist, maximum bytes, optional known content hash, approver, and date.
DNS must resolve only to public addresses; redirects and proxies are disabled. Streaming
enforces the limit before commit, validates supported document/CAD signatures and rejects
executable magic. The generated quarantine identity is atomically created without overwrite.
Its self-hashed record explicitly retains false redirect, proxy, executable, catalog, and
drawing flags. Retrieval alone never makes an asset eligible for the symbol catalog.
If an official server declares an exact reviewed `.dwg` or `.dxf` URL as generic binary,
the declaration is normalized only to the already-approved typed CAD media and still must
pass the matching signature check. No extension or type inference is allowed for other
generic content.

`integrations.manufacturer_mapping` keeps selection and drawing state separate. A reviewed
model sidecar is bound to the exact shared-model SHA-256 and an object that exists in that
model; it copies only explicitly selected reviewed claims and retains their artifact hashes
and manufacturer provenance. The immutable model is not rewritten. A manufacturer symbol
candidate binds the bundle, exact retrieval approval, quarantine record, CAD identity, and
canonical JSON for a catalog proposal that passes `validate_catalog_definition`. Geometry
derivation is explicitly manual/reviewed at this boundary. The candidate is not a catalog
entry and has no drawing authority; later registration must remain separate, confirmed,
backed up, provenance-preserving, and followed by the Milestone 18 placement lifecycle.

`register_manufacturer_symbol_candidate` implements that separate catalog transition. It
uses the existing atomic strict registrar and its timestamped backup, then persists a new
self-hashed lineage record that binds all upstream evidence and CAD hashes to exact catalog
before/after hashes and versions. The backup digest must equal the prior catalog. If lineage
write or verification fails, the prior catalog bytes are atomically restored and hashed.
Registration never places the symbol; any later placement starts again at the Milestone 18
proposal/preview/review/one-use-approval/recovery boundary.

The authenticated manufacturer-review API reparses exact JSON records and rechecks every
self hash before deterministic lineage review. Its AutoCAD `Manufacturers` tab is only a
bounded display client: it loads one reviewed request, validates bundle/artifact/result
hashes and all negative-action flags, and displays evidence plus
`PASS`/`UNKNOWN`/`CONFLICT`. It has no download, catalog-write, drawing-write, or AutoCAD
command-execution path.

`core.manufacturer_discovery` separates project requirements from provider retrieval. One
self-hashed approval binds model identity, category, search text, allowed official domains,
and requirements whose values each retain an upstream source identity and SHA-256. The
injected provider receives only that approved request and must return exact bounded JSON;
unapproved hosts, duplicate candidates, action claims, malformed properties, timeouts, and
oversize results fail closed. Local deterministic comparison handles exact/minimum/maximum
operators without unit conversion or model inference. The authenticated discovery route
returns a sanitized service-unavailable response when no provider is configured. The
The production adapter is separately opted in and uses the fixed OpenAI Responses endpoint
with web search restricted to the request's reviewed official domains and strict structured
output. Provider output is still untrusted: the local adapter resolves each exact host to
public addresses, re-fetches each product page with proxy and redirects disabled, enforces a
2 MiB text limit, confirms the part number and quoted property evidence occur in those bytes,
and computes the source SHA-256 itself. The AutoCAD `Product Discovery` tab can record one
self-hashed engineer choice scoped only to `candidate_for_follow_on_review`; that record
explicitly performs no product approval, download, catalog registration, or drawing action.

## Packaging, installation, migration, and recovery boundary

`launcher.py` exposes a fixed argparse command set for health checking, normal
lifecycle/API startup, package creation and installation, recovery, and migration.
It does not accept arbitrary executable names or shell text. `ai-engineer.cmd` only
forwards operator arguments to that Python launcher. A package-root `__main__.py`
also lets Python run the same fixed installer directly from the transferred ZIP,
without requiring an existing checkout on the destination computer.

`PackageBuilder` packages an explicit source allowlist and generated clean runtime
JSON templates. Local task history, approvals, developer runs, lifecycle counters,
logs, backups, work files, Git metadata, caches, and tests are excluded. Sources and
archives have entry, per-file, and total-byte limits; source links and archive links,
directories, duplicate names, absolute paths, traversal, and backslash paths fail
closed. A deterministic SHA-256 manifest covers every payload file.

`PackageInstaller` fully validates and hashes the archive before it writes. Explicit
confirmation and a nonexistent non-root destination are mandatory. Files are first
written into a sibling temporary directory with atomic file replacements, then the
completed tree is atomically renamed into place. The hash manifest provides
integrity, not publisher authenticity or trust in unreviewed source.

`RecoveryManager` snapshots only the six allowlisted schema-v1 JSON documents and
rejects recursively discovered secret-like keys. Restore validates the complete
archive and documents before mutation, creates a separate backup of current runtime
data, writes atomically, and rolls back files written during a failed restore.
Snapshots may still contain sensitive goals, task metadata, or mission text and are
therefore private local artifacts.

`MigrationManager` completes older schema-v1 runtime layouts and updates application
version metadata. It reports no changes without requiring confirmation, but any
actual mutation requires confirmation and a pre-migration recovery archive. Partial
writes roll back. This is layout migration within schema 1, not an importer for an
unknown future schema.

## Package boundaries

- `agent/`: lifecycle, configuration, state, logging, typed commands, permissions,
  durable approvals, task orchestration, provider boundary, reviewed developer loop,
  and the Safe Core command catalog.
- `core/`: contains the safe file, scan, terminal, system, process, Git, packaging,
  installation, recovery, and migration services.
- `api/`: authenticated localhost-only FastAPI application, strict models, routes,
  middleware, lifecycle, and single-worker Uvicorn runner.
- `integrations/`: supported VS Code CLI, bounded AutoCAD 2021 operations, verified
  drawing handoff, and reserved future adapters.
- `plugins/`: reserved for optional extensions governed by the same permissions.
- `data/`: non-secret local settings and durable state.
- `logs/`: local structured operational records; generated logs are ignored by Git.
- `workspace/`: future configured work roots.
- `backup/`: recoverable copies, migration snapshots, and recovery snapshots made
  before destructive changes.

Empty future-package modules are not operational implementations.

## Safety and trust boundaries

The current process accepts only authenticated loopback API traffic and does not
automate desktop applications.
Safe Core capabilities are exposed only through authenticated typed tasks on
localhost. The provider-driven developer loop uses the same boundaries, adds exact
plan/action review, and retains human approval for high-risk actions.

Secrets must not be stored in repository data or logs. Failures use the
`AIEngineerError` hierarchy so callers receive actionable errors without reporting
false success.
