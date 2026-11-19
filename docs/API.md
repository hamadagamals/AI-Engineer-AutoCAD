# Local API

The AI-Engineer API is an authenticated local control surface for typed tasks. It is
not a remote-control server and must not be published through port forwarding,
reverse proxies, tunnels, LAN binding, or public networking.

## Start

Set a high-entropy token in the current process environment:

```powershell
$bytes = New-Object byte[] 32
[Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
$env:AI_ENGINEER_API_TOKEN = [Convert]::ToBase64String($bytes)
python -m api.server --check
python -m api.server
```

Optional non-secret environment settings:

- `AI_ENGINEER_API_PORT`: integer 1024–65535; default `8765`.
- `AI_ENGINEER_API_MAX_BODY_BYTES`: 1024–10485760; default `1048576`.
- `AI_ENGINEER_API_HOST`: if supplied, it must be exactly `127.0.0.1`.

`AI_ENGINEER_API_TOKEN` must be at least 32 non-whitespace characters with adequate
character diversity. It is never stored in repository settings, logs, response data,
or middleware representations.

## Authentication and transport

Only `GET /health` is public. Every other path is rejected before body parsing unless
it includes exactly one valid header:

```text
Authorization: Bearer <AI_ENGINEER_API_TOKEN>
```

The server binds to `127.0.0.1`, uses one worker, rejects untrusted Host headers,
ignores proxy headers, disables access logging, and does not expose `/docs`, `/redoc`,
or `/openapi.json`. Responses include `Cache-Control: no-store`, `nosniff`, frame
denial, and no-referrer headers.

## Routes

- `GET /health`: minimal status and version; no authentication.
- `GET /v1/status`: lifecycle, command count, and task counts.
- `GET /v1/commands`: safe command metadata without handlers.
- `POST /v1/tasks`: validate and queue a typed command.
- `GET /v1/tasks?limit=100`: newest task summaries without submitted payloads.
- `GET /v1/tasks/{task_id}`: one task summary.
- `POST /v1/tasks/{task_id}/run`: run with optional `approval_id`.
- `POST /v1/tasks/{task_id}/cancel`: cancel or request cooperative cancellation.
- `GET /v1/approvals/{approval_id}`: approval state.
- `POST /v1/approvals/{approval_id}/decision`: explicit approve/deny decision.
- `POST /v1/files/read`: submit and run `file.read`.
- `POST /v1/files/write`: submit `file.write`; returns awaiting approval.
- `POST /v1/files/delete`: submit `file.delete`; returns awaiting approval.
- `POST /v1/scans`: submit and run `project.scan`.
- `POST /v1/takeoff/reports`: re-import exact AutoCAD context evidence and calculate a
  transient source-linked device and route quantity report without drawing mutation.
- `POST /v1/takeoff/exports/csv`: recalculate the same report and return bounded CSV only
  when its SHA-256 matches the exact reviewed report hash.
- `POST /v1/calculations/low-voltage`: re-import exact AutoCAD context and calculate
  source-linked balanced current and approximate voltage drop from reviewed inputs.
- `POST /v1/calculations/load-aggregation`: apply a reviewed versioned demand, diversity,
  and phase-allocation rule set to exact source-backed load objects.
- `POST /v1/calculations/cable-ampacity`: apply an ordered reviewed derating chain to a
  route-linked base cable ampacity without selection or compliance claims.
- `POST /v1/calculations/short-circuit`: calculate bounded symmetrical RMS fault current
  from reviewed route-linked source/path R/X without equipment or compliance claims.
- `GET /v1/developer/status`: provider availability and non-secret identifier.
- `POST /v1/developer/runs`: create a bounded goal and generate a reviewable plan.
- `GET /v1/developer/runs?limit=100`: newest protected developer runs.
- `GET /v1/developer/runs/{run_id}`: one run including reviewable plan/action data.
- `POST /v1/developer/runs/{run_id}/plan-review`: approve or deny the plan.
- `POST /v1/developer/runs/{run_id}/advance`: request the next proposal or resume a
  separately decided command approval.
- `POST /v1/developer/runs/{run_id}/action-review`: approve or deny the exact command
  payload.
- `POST /v1/developer/runs/{run_id}/cancel`: cancel a non-terminal developer run.

## Destructive-operation flow

1. Submit a high-risk route or task.
2. Receive a task with `status: awaiting_approval` and `approval_id`.
3. Inspect the task and approval.
4. Decide via `/v1/approvals/{id}/decision` with `{"approved": true}` or `false`.
5. If approved, call `/v1/tasks/{task_id}/run` with the matching approval ID.
6. The approval is consumed once and cannot authorize another task or command.

Task responses intentionally omit the original payload. Results can contain requested
file content or scan data, so the Bearer token remains necessary even for reads.

## Developer-agent review flow

The normal `python -m api.server` process has no developer provider configured and
`POST /v1/developer/runs` returns 503. A trusted embedding application must inject a
`ProviderAdapter`; this milestone does not add cloud networking or credential
storage.

When a provider is configured:

1. Create a run with `goal` and `maximum_actions` from 1 through 10.
2. Inspect `plan_summary` and `plan_steps`, then send `{"approved": true}` to
   `plan-review`.
3. Call `advance`, inspect the exact `pending_action` command, risk, rationale, and
   payload, then send a decision to `action-review`.
4. Low-risk commands execute through `TaskEngine` and are verified. High-risk
   commands enter `awaiting_command_approval`; inspect and decide the referenced
   existing approval before calling `advance` again.
5. Continue one reviewed action at a time. Completion is accepted only after a
   successful task observation and an accepted verification.

Provider output never bypasses command registration, payload validation, permission
policy, task persistence, or durable approvals. Developer run responses intentionally
show proposed payloads because the local operator needs them for informed review.

## VS Code command flow

VS Code integration uses the generic authenticated task routes; no additional public
surface is added. Submit `vscode.detect` with an empty payload for bounded non-secret
CLI metadata. Submit `vscode.open` with `path` and optional `line`/`column`; it always
returns `awaiting_approval` before the CLI can launch. Inspect and decide the durable
approval, then resume the task with its one-use approval ID.

The open target must already exist inside a configured workspace root. Tokens are
never passed to VS Code command arguments, and no extension, editor input, integrated
terminal, or GUI automation route exists.

## AutoCAD command flow

AutoCAD also uses generic protected tasks. `autocad.detect` accepts an empty payload
and returns non-secret AutoCAD 2021 method availability. `autocad.inspect_scratch`
accepts `{"path": "drawing.dwg"}` and always pauses for a durable approval before
copying or launching anything. `autocad.inventory_scratch` accepts the same payload
and has an independent durable approval.

`autocad.import_drawing_context` accepts
`{"context_id":"<32 lowercase hex>","evidence_sha256":"<64 lowercase hex>"}`
and requires durable high-risk approval. The server resolves exactly one matching
JSON record below a configured workspace context inbox, bounds its size, verifies the
approved SHA-256, validates the exact schema and local limits, then maps it to the
shared electrical model. No drawing path, command text, provider request, or egress
permission is accepted.

`autocad.create_line_scratch` accepts
`{"path":"drawing.dwg","start":[0,0,0],"end":[1000,0,0]}` and also pauses for an
independent durable approval. Coordinates are strict finite numbers bounded to ±1e9
and normalized to nine decimal places; the resulting points must differ.

`autocad.create_wire_route_scratch` accepts
`{"path":"drawing.dwg","points":[[0,0,0],[1000,0,0],[1000,500,0]]}`.
It requires its own durable approval. The array contains 2–16 finite bounded 3D
points, and adjacent normalized points must differ. Layer, color, linetype, entity
type, save command, and output location are fixed by the implementation.

`autocad.list_electrical_symbols` accepts `{}` and returns the validated catalog
version plus bounded symbol metadata, including each exact `definition_sha256`.

`autocad.place_electrical_symbol_scratch` accepts
`{"path":"drawing.dwg","symbol_id":"socket_single","definition_sha256":"<64 lowercase hex>","insertion_point":[2000,1000,0],"rotation_degrees":0,"scale":1}`.
Rotation and scale are optional defaults of zero degrees and 1.0; rotation is bounded
to ±360 degrees and uniform scale to 0.001–1000. The operation pauses for durable
approval, and the supplied hash must still match the catalog at execution time.

`autocad.review_electrical_symbol_source` accepts
`{"path":"user-symbol.dwg","block_name":"AIENG_E_SOCKET_CUSTOM_V1","symbol_id":"socket_custom","symbol_version":1,"insertion_layer":"E-POWR-DEVC","layer_color":2}`.
It pauses for durable approval, copies the source, forces `/readonly`, and reviews only
the exact safe block name. The result includes units, flags, base point, bounded entity
counts, layer/attribute/nested/unsupported status, source hash, eligibility reasons,
and an optional complete proposed definition. It never updates the catalog.

`autocad.register_electrical_symbol_review` accepts
`{"review_run_id":"<32 lowercase hex>","proposal_sha256":"<64 lowercase hex>","source_sha256":"<64 lowercase hex>"}`.
It requires a new durable approval distinct from review. The server resolves the
persisted run evidence and reviewed source itself; no definition geometry, catalog
path, backup path, or executable text is accepted from the request.

`autocad.create_electrical_circuit_fragment_scratch` accepts a workspace drawing path,
one symbol ID and exact definition SHA-256, `placements` containing 2–8 objects with a
three-number `point` and optional bounded `rotation_degrees`/`scale`, and `wire_points`
containing 2–16 connected points. It requires durable high-risk approval. Layer names,
block names, geometry, scripts, executables, and output paths are not request fields.

`autocad.create_panel_sld_fixture_scratch` accepts only
`{"path":"disposable.dwg","expected_source_sha256":"<64 lowercase hex>"}` and pauses
for durable high-risk approval. The fixed producer creates only the M21 scratch acceptance
fixture; the response reports source/output hashes, exact counts, clean-save state, and
source-unchanged/scratch-changed evidence. The isolated output is always a `.dwg`, including
when the reviewed source is a `.dwt`; it cannot mutate the source drawing.

`autocad.create_maximum_performance_fixture_scratch` accepts only
`{"path":"reviewed-disposable.dwg","expected_source_sha256":"<64 lowercase hex>"}`
and pauses for a separate durable high-risk approval. It creates exactly 1,792 fixed
`LINE` entities on reserved layer `AIENG-PERF-FIXTURE` in one copied scratch drawing,
fails if that layer already exists, verifies exact counts and `DBMOD=0`, and writes
self-hashed recovery evidence. This performance-only output has `handoff_allowed=false`
and cannot be delivered to a project through the verified drawing handoff path.

Inspection accepts only bounded workspace `.dwg`/`.dwt` files, passes only a scratch
copy to Core Console, forces `/readonly`, and runs a fixed metadata script. The API
returns structured variables and scratch references, never raw console output. No
route accepts arbitrary AutoCAD commands, AutoLISP, COM methods, or original-drawing
mutation.

Inventory returns aggregate `layer_count` and `entity_count` values only. Success also
requires `DBMOD=0`, unchanged source and scratch hashes, bounded counts, and complete
markers. It never returns drawing-internal names, types, handles, text, attributes, or
geometry.

The line writer returns only the scratch output path, validated points, before/after
counts, created type, clean-save state, hash-state booleans, and bounded run metadata.
It never overwrites the source or accepts script, command, layer, or output-path text.

The wire-route writer additionally returns the fixed layer schema, normalized points,
segment count, and verified count/hash state. It creates exactly one `LINE` per
adjacent pair on `E-WIRE` and never promotes the output automatically.

The symbol writer returns the approved symbol/block/version/hash, fixed insertion
layer, normalized transform, zero attribute count, before/after count, clean-save and
hash evidence, and scratch output path. It cannot accept arbitrary geometry, scripts,
block names, layer names, attributes, or output locations.

The review result exposes only bounded definition geometry for eligible line/circle
intake and aggregate reasons otherwise. The source and scratch hashes must remain
equal and `DBMOD` must remain zero. A proposal is evidence for a later approval, not a
registration success.

Registration returns the new catalog version, registered bounded symbol metadata,
review/source/proposal identifiers, and local catalog/backup paths. Duplicate IDs or
block names, missing/ineligible evidence, hash drift, changed source bytes, concurrent
catalog edits, and validation failures fail without success; post-write validation
failure triggers rollback.

Circuit-fragment success returns exact reference/segment counts, normalized transforms
and route points, whether the block was reused, source/output SHA-256 values, layer and
catalog identities, scratch run/path, source-unchanged/scratch-changed flags, and
`DBMOD=0`. Inconsistent existing definitions, units, layers, transforms, endpoints,
counts, save state, or hashes fail without modifying the original drawing.

## Copilot session flow

All copilot routes require the normal Bearer token. `POST /v1/copilot/sessions`
accepts an exact local `context_id`, `evidence_sha256`, `language` (`ar` or `en`), and
5–120 minute TTL. Creation revalidates the bounded workspace evidence and returns the
read-only session plus a high-entropy `owner_key` once. The service retains only its
SHA-256.

Every route below additionally requires the owner key in
`X-AI-Engineer-Session-Key`:

- `GET /v1/copilot/sessions/{session_id}` returns the bounded local result/history;
- `POST /v1/copilot/sessions/{session_id}/messages` adds one Arabic/English engineer
  message and accepts no role, command, provider, or drawing-write field;
- `POST /v1/copilot/sessions/{session_id}/cancel` irreversibly cancels local activity;
- `POST /v1/copilot/sessions/{session_id}/egress-reviews` creates an exact preview from
  1–32 named text fields, privacy classification, destination, and 1–30 minute TTL;
- `GET /v1/copilot/sessions/{session_id}/egress-reviews/{review_id}` returns that exact
  hash-bound preview;
- `POST /v1/copilot/sessions/{session_id}/egress-reviews/{review_id}/decision` records
  approval or rejection but performs no transmission.
- `GET /v1/copilot/provider-status` reports only whether an injected provider exists
  and its bounded name;
- `POST /v1/copilot/sessions/{session_id}/answers` accepts one approved `review_id`,
  consumes it once, invokes only the bounded read-only answer adapter, and returns the
  validated answer plus updated session;
- `GET /v1/copilot/sessions/{session_id}/answers/{answer_id}` returns a previously
  validated owner-bound answer without another provider call.

Sessions are process-local, limited to eight active sessions and 30 operations per
session per rolling minute, and disappear on API restart. Missing/wrong owner keys do
not reveal session existence. Whole-drawing egress, drawing writes, provider calls,
and arbitrary commands are not available through these routes. No provider is
configured by default.

## Drafting preview flow

All drafting routes require the normal Bearer token. `POST /v1/drafting/runs` accepts
one exact proposal and TTL, re-imports its local AutoCAD context, reloads the reviewed
symbol catalog, validates every binding/object/topology field, and returns a one-time
owner key. The durable lifecycle stores hashes and metadata, never the owner key or
proposal geometry.

Every later route requires `X-AI-Engineer-Drafting-Key`:

- `POST /v1/drafting/runs/{run_id}/preview` accepts the same exact proposal, re-imports
  and revalidates it, then returns a transient ordered preview plus validation evidence;
- `POST /v1/drafting/runs/{run_id}/preview-review` can run only after the exact preview
  ID/SHA-256/expiry is durably bound;
- `POST /v1/drafting/runs/{run_id}/preview-review/decision` records one terminal approve
  or reject decision but grants no execution capability;
- `GET /v1/drafting/runs/{run_id}` and `POST .../cancel` remain owner-bound.

The preview includes the proposal geometry needed for display, but both
`drawing_mutation_performed` and `execution_approved` are always false. Only preview
identity/hash/expiry persist. Wrong ownership, changed proposal/model/catalog hashes,
blocked validation, replay, and preview/run expiry fail closed. No route executes an
AutoCAD drawing write.

## Quantity takeoff

`POST /v1/takeoff/reports` requires the normal Bearer token plus an exact 32-character
AutoCAD `context_id` and its 64-character `context_evidence_sha256`. The route re-imports
that local evidence and returns one transient, self-hashed `m19.takeoff.v1` report. It
does not persist or modify the drawing and always reports
`drawing_mutation_performed=false`.

The optional policy accepts non-negative vertical allowance per route, termination
allowance per end, waste from 0 through 100 percent, and bounded route overrides. Every
override contains an exact route ID, final total in meters, non-empty reason, and reviewer.
Recognized devices are grouped only by exact reviewed symbol ID and block name. Route
geometry is measured in 3D and converted from explicit inch, foot, millimeter, centimeter,
or meter units. Unitless/unknown drawings, zero-length segments, changed evidence, duplicate
overrides, and overrides for absent routes fail closed. Unknown source objects are returned
and make `is_complete=false`; they are never silently classified.

`POST /v1/takeoff/exports/csv` accepts the same exact context and policy plus
`reviewed_report_sha256`. It re-imports and recalculates rather than trusting submitted
quantities. A changed context, policy, route, or reviewed hash fails closed. The response
contains deterministic UTF-8 CSV content, content/report SHA-256 values, row/byte counts,
spreadsheet-formula escaping status, and explicit false drawing/file-write flags. It does
not persist a file; the AutoCAD palette displays the reviewed artifact after a separate
confirmation.

## Low-voltage current and voltage drop

`POST /v1/calculations/low-voltage` accepts an exact context ID/evidence SHA-256 plus one
strict `m20.lv_current_voltage_drop.v1` input. The input identifies source-backed load
objects and exact route IDs, phase system, output power, demand factor, nominal voltage,
power factor, efficiency, conductor AC resistance/reactance, reviewer, and assumptions.
The server re-imports context evidence, derives total 3D route length from the model, and
returns current and approximate voltage drop with formula identities/expressions, source
references, warnings, model/context hashes, and a deterministic result SHA-256.

Only `single_phase` and `balanced_three_phase` are supported. The first interprets voltage
as line-to-neutral and the second as line-to-line. The calculation assumes a sinusoidal
lagging balanced steady-state basis. It does not apply temperature, harmonics, grouping,
installation, derating, ampacity, protection, short-circuit, or code rules and always
returns `compliance_evaluation_performed=false` and
`drawing_mutation_performed=false`. Unknown units, changed evidence, missing source IDs,
incomplete routes, coercion, and invalid engineering ranges fail closed.

## Load aggregation, diversity, and phase allocation

`POST /v1/calculations/load-aggregation` accepts an exact context ID/evidence SHA-256
and a strict `m20.load_aggregation.v1` rule set. Each allocation names one source-backed
load object and supplies connected output power, demand factor, and `l1`, `l2`, `l3`, or
`three_phase`. Explicit diversity groups must form an exact partition of all allocations;
missing, duplicate, overlapping, unknown, or unsourced load identities fail closed.

The deterministic result includes connected power, demand before/after diversity,
per-group values, ordered L1/L2/L3 connected and demand totals, average phase demand,
maximum spread, and maximum deviation from average. A three-phase load is divided equally
across L1/L2/L3. Inputs, formulas, warnings, sources, model/context hashes, and result hash
remain traceable. The imbalance value is a power-allocation metric only: no acceptance
threshold, current/voltage/harmonic/neutral analysis, code rule, compliance conclusion,
provider call, persistence, or drawing mutation is performed.

## Short-circuit source-equivalent boundary

`POST /v1/calculations/short-circuit` accepts an exact context ID/evidence SHA-256 and a
strict `m20.short_circuit_source_equivalent.v1` input. `single_phase_loop` interprets the
nominal voltage as L-N and requires the reviewed path R/X to represent the complete fault
loop. `balanced_three_phase` interprets it as L-L for a balanced bolted fault. Both retain
an explicit voltage multiplier, separate source/path resistance and reactance, non-empty
impedance basis, reviewer, and assumptions linked to one source-backed route.

The result returns total R/X and impedance magnitude, symmetrical RMS current, apparent
fault power, formulas, warnings, route/model/context evidence, and deterministic SHA-256.
It does not verify external impedance evidence or calculate sequence networks,
generator/motor/converter contribution, transient/peak current, arc impedance/arc flash,
equipment interruption rating, coordination, or standards compliance. All corresponding
flags and drawing mutation are false.

## Cable ampacity input and derating chain

`POST /v1/calculations/cable-ampacity` accepts an exact context ID/evidence SHA-256 and
strict `m20.cable_ampacity_derating.v1` input. It identifies one source-backed route and
retains a reviewed cable designation, base ampacity, base basis reference, reviewer, and
assumptions. Each ordered factor has a unique ID, label, value greater than zero and no
greater than one, and non-empty basis reference.

The result records every stage's incoming ampacity, factor, and outgoing ampacity, plus
the combined factor, final derated ampacity, exact route/model/context source evidence,
formula, warnings, and deterministic SHA-256. It does not verify external tables or their
applicability and never compares load current, selects a cable/protective device, evaluates
voltage drop/short circuit/earthing/compliance, calls a provider, or mutates the drawing.

## Error behavior

- `400`: safe project/configuration request error.
- `401`: missing or invalid Bearer token.
- `403`: permission denied.
- `404`: task, approval, command, or route not found.
- `409`: approval or task lifecycle conflict.
- `413`: request body exceeded the configured byte limit.
- `422`: strict model or command payload validation failure.
- `500`: unexpected failure with generic response text.
- `503`: application services are not initialized.

Validation errors omit Pydantic input values, and unexpected exceptions expose only a
generic response while the local structured log records the path and exception type.

## Manufacturer discovery and engineer choice

`POST /v1/manufacturers/discover` accepts one exact self-hashed, egress-approved project
request plus discovery/reviewer metadata. A configured provider may return only bounded
official-host candidates. The server performs deterministic requirement comparison and
returns source hashes plus `PASS`/`FAIL`/`UNKNOWN`/`CONFLICT`; automatic selection,
download, catalog registration, and drawing mutation must remain false. Without the
separately configured provider the route returns a sanitized 503.
Within one API process, the bounded provider adapter caches up to 32 validated candidate tuples
by the exact self-hashed discovery request. Repeating the identical request reuses that immutable
validated result and does not issue a second paid provider call. A changed request has a different
hash and remains a new explicitly approved search; failures are never cached.
Paid results and logs identify the configured model in `provider_name`, for example
`openai-official-search:gpt-5.6-luna` for Economy and
`openai-official-search:gpt-5.6` for Quality.

`POST /v1/manufacturers/discover/manual` is the no-AI-cost path. It accepts the same
reviewed request plus `candidate_draft.candidates`. Each draft candidate must contain
`manufacturer_name`, `brand_name`, `family_name`, `model`, `part_number`, `product_url`,
`official_host`, and `properties`. Each property contains `property_name`, `value`, optional
`unit`, `source_locator`, and a short exact `evidence_text` visible on that product page.
AI-Engineer independently fetches the HTTPS page from an approved official domain, blocks
private DNS/proxies/redirects and oversized or non-text responses, verifies the part number
and evidence text, hashes the retrieved bytes, and then performs the same deterministic
comparison. The result explicitly reports `provider_egress_performed=false`; official-page
network retrieval is still performed under the request's separate egress approval.

When an approved official site blocks transparent automated access, every candidate may
instead add `evidence_path` pointing to a browser-saved UTF-8 `.html`/`.htm` file inside a
configured workspace root. The API uses the sandboxed file service, enforces a dedicated
2 MiB saved-HTML limit while the general file API remains at 1 MiB, verifies the official
HTTPS URL/host, part number, and exact evidence text
against the saved HTML, then hashes those exact bytes. This mode reports provider name
`engineer-saved-official-evidence` and `provider_egress_performed=false`; the hash proves the
reviewed snapshot's identity, while the engineer remains responsible for saving it from the
stated official URL without editing it.

`POST /v1/manufacturers/discover/select` accepts the exact self-hashed discovery result,
one candidate ID contained in it, reviewer/date, and rationale. It reparses and rehashes the
whole result and returns a self-hashed `candidate_for_follow_on_review` choice. This is not
product approval and cannot download, register, or modify a drawing.

## Panel and SLD extraction

`POST /v1/panels/extract-schedule` requires authentication and accepts an exact local
`context_id`, `context_evidence_sha256`, reviewer, and 1–16 reviewed assumptions. The
server re-imports the evidence and recognizes only the fixed `AIENG_M21_PANEL_V1`,
`AIENG_M21_WAY_V1`, `AIENG_M21_SLD_NODE_V1`, and `AIENG_M21_SLD_EDGE_V1` block markers.

A complete context returns a self-hashed extraction containing the semantic model and
panel/SLD contract plus a deterministic `m21.panel_schedule.v1` schedule. Incomplete or
conflicting evidence returns HTTP 201 with explicit findings, `semantic_model=null`,
`contract=null`, and `schedule=null`; this is a reviewed read-only result, not an HTTP or
execution failure. The route never calls a provider, writes a file, or mutates AutoCAD.
