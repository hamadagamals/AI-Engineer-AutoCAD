# AI-Engineer Cross-Computer Handoff — 2026-08-08

## Current transfer checkpoint - 2026-09-17 (r50)

This checkpoint supersedes the historical startup/checkpoint instructions below for the latest
transfer. Read `docs/transfer-20260917/START-HERE-AR.md`, `docs/WORK_QUEUE.md`, and
`docs/AUTOCAD_COMPONENT_MOVES.md`. Core version remains 0.18.0; Operator extension is now 0.1.6.
The latest source includes general chat, reviewed Project Agent runs, explicit native selection,
goal egress review, and registered read-only existing-component move previews. Relocation execution
is not implemented or enabled; native extents/dependencies, fingerprint reconciliation, exact
Milestone 18 binding, scratch transaction, recovery and live acceptance remain next.

The transfer bundle contains sanitized portable source, tests and the Hutchison Ports R01-R03
review project with its 17 supplied 2D component sources. It excludes credentials, runtime session
history and unrelated workspace projects; it does not carry Git history. Preserve source drawings.
The original PC's focused transfer baseline is 175 passing tests and a successful AutoCAD 2021 DLL build,
not a complete-suite or destination-PC native acceptance claim. Gemini startup can select AutoCAD
2021/2023 and rebuild against the destination's assemblies; 2023 live acceptance remains open.

## Read this first

This repository snapshot is the current continuation package for **AI-Engineer Operator
v5.4**. The core application version intentionally remains `0.18.0`; the VS Code
Operator extension is `0.1.4`. Operator/Hybrid work is post-0.18.0 and has not yet been
released as a new core semantic version.

Do not rely on chat history as the source of truth. On the destination computer, read
these files in this order before making changes:

1. `AGENTS.md`
2. `docs/TRANSFER_HANDOFF.md`
3. `docs/WORK_QUEUE.md`
4. `docs/PRODUCT_VISION.md`
5. `docs/KNOWN_ISSUES_V5_4.md`

## Latest checkpoint — 2026-08-16

- Full source verification is `526 passed, 1 skipped`; compileall and the AutoCAD 2021 x64 DLL
  build pass.
- The allowlisted local AutoCAD connection-review channel passed live request, exact drawing/handle
  validation, view/highlight, engineer Yes/No, atomic result, request binding, and anchor retention.
  It has no arbitrary command path and never authorizes drawing mutation.
- The superseded unclear `lighting.dwg` batch was repeated with endpoint-marker-v2. Seven electrical
  routes now have 14/14 reviewed connections; two polylines are explicitly classified as one
  circuit-number arrow/leader and excluded. Review SHA-256 is
  `c58bdf23acfec53d319e9bd222e93317ce791efd12359df0135eb576f5b870db`.
- Its calculation-readiness report correctly remains false because electrical load, cable,
  protection, installed-length, and project-code inputs are missing rather than guessed.
- Portable candidate r26 supersedes r25 and contains the live-corrected Gemini REST response parser,
  bounded redacted provider diagnostics, isolated port `8766`, and a separate raw `INSERT` block
  count to the independently gated Gemini provider and existing provider choices. The first live
  explicitly approved read-only Gemini question returned a bounded Arabic answer in AutoCAD and
  performed no drawing mutation; it also exposed that the prior summary merged all unregistered
  entities into `unknown`. It also makes the copilot buttons prerequisite-aware and labels a
  successfully created session as active, fixing the misleading live workflow. r26 passed clean
  install, twelve-file readiness, installed DLL build,
  and all six local-core benchmark targets. Archive SHA-256 is
  `f5268cfcf31c9d55e13b3bf052d0b3c34114b17b565c37ac9d8f7133a6a88398`.
- The provider set remains the independently gated Gemini provider plus the
  existing OpenAI, Ollama, Codex, and Hybrid choices while retaining the live-accepted cancellation.
- The exact no-mutation operator channel passed live context, status, and palette actions without a
  manually entered AutoCAD command. It captured 512 entities in `26.5282 ms` at `DBMOD=0 -> 0` and
  returned request-bound result evidence automatically.
- Its exact cooperative cancellation also passed live in `619.1593 ms`, with no partial context,
  no mutation, and a follow-up `DBMOD=0 -> 0` status. The project drive is a healthy NVMe SSD.
- Continue with live AutoCAD maximum-fixture capture/display and scratch execution/recovery timing,
  then a second initialized Windows/AutoCAD profile. Do not claim final 1.0 before those
  gates and the project-specific electrical inputs/code profile are supplied or explicitly scoped out.

## Latest autonomous checkpoint — 2026-08-13

- The complete local baseline is `499 passed, 1 skipped`; `compileall` and the x64 AutoCAD
  palette build pass. The latest normal-path DLL build SHA-256 is
  `9e52e02eca9d0e2ac9c5f378aa6791622993625553fa1a9d5c3f011672caffe2`.
- Milestone 23 Free browser-assisted discovery has passed in AutoCAD with Legrand `004280`
  and Schneider `A9A15306`, including exact local source hashes and a separately recorded
  engineer choice. Product approval, download, catalog registration, provider egress, and
  drawing mutation remained false. The funded Quality path also passed on 2026-08-13 with both
  candidates, exact result/selection hashes, and every automatic follow-on action false.
- Paid discovery now caches an exact successful request in process to prevent duplicate
  billable calls and records the exact Economy/Quality model in the result provider identity.
- The portable-package source omission was fixed: all four AutoCAD launchers and the canonical
  Free candidate template are now packaged. A clean installed-copy check, read-only AutoCAD
  readiness pass, and template materialization pass were completed on this PC.
- The current dated portable candidate excludes post-build package-identity reports, passed clean
  install/readiness and all six packaged local-core performance targets, and has its exact revision
  and hash in `docs/evidence/autocad-beta-package-candidate-2026-08-13.md`.
- `python launcher.py autocad-readiness` passed Windows, Python 3.13.14, six AutoCAD 2021
  dependencies, three build dependencies, seven exact product-source identities, and free
  port 8765 without starting a process, provider egress, or drawing mutation. See
  `docs/evidence/autocad-release-readiness-2026-08-12.md`.
- `docs/AUTOCAD_SUPPORTED_FEATURES.md` is now the honest beta boundary. Performance targets
  are frozen in `docs/AUTOCAD_PERFORMANCE_TARGETS.md`. Five-run local-core parsing, preview-hash,
  and LV-calculation trials pass with self-hashed evidence; live AutoCAD capture/display and
  copied-scratch execution/recovery measurements remain outstanding.
- Continue in this order: representative real-project acceptance against the matrix, live
  performance measurements, then validation on
  a second initialized Windows/AutoCAD 2021 profile. Do not begin ETAP or SOLIDWORKS early.
- This checkpoint performed no external provider call and no drawing mutation.
- `lighting.dwg` real-project acceptance now has one complete exact-context mapping: six reviewed
  devices, one endpoint-only route, panel `DB3-GA2`, and circuit `4`. Control tags remain explicitly
  unassociated; visible-copy DBMOD resolution and live palette revalidation are next. See
  `docs/evidence/autocad-real-project-lighting-2026-08-13.md`.
- A fresh source-identical context safely rebound that review and passed live AutoCAD palette/API
  validation with 7 devices, 6 routes, 8 unknowns, 5 warnings, DBMOD=16, and no egress/write.

## Session checkpoint — 2026-08-12

- Repository source, durable evidence, and documentation are preserved in place. No
  AutoCAD drawing mutation, source replacement, handoff, or provider egress was performed
  while creating this checkpoint.
- Milestones 0–21 are complete within their documented boundaries. The latest verified
  baseline is `452 passed, 1 skipped`; compileall and the AutoCAD palette DLL build pass.
- The authoritative active task is Milestone 23 manufacturer evidence and component assets
  in `docs/WORK_QUEUE.md`.
- Milestone 22 now has immutable pinned project code profiles, non-activating edition
  impact review, exact hash-bound clause/rule/fact evidence, and deterministic
  `PASS`/`FAIL`/`UNKNOWN`/`CONFLICT` findings. This local layer performs no provider
  retrieval, regulatory approval, protected-publication storage, or drawing mutation.
- The authenticated compliance API and the read-only AutoCAD `Code Compliance` tab are
  implemented and automated-tested. They revalidate exact nested hashes, expose citations
  and all four finding states, and contain no drawing-write path; direct live AutoCAD
  observation with lawful evidence remains.
- A public OSHA acceptance fixture has exact source/profile/clause/rule/request hashes and
  automated coverage for all four outcomes. Live API/AutoCAD palette acceptance passed;
  the operator visually confirmed the exact citations, hashes, PASS and negative action
  flags, and independent reconstruction matched the displayed result SHA-256. Milestone 22
  is complete. See `docs/evidence/m22-compliance-live-acceptance.md`.
- The first Milestone 23 contract slice is implemented: exact manufacturer/product/artifact
  provenance, reviewed property claims, lawful access/license metadata, official HTTPS host
  pinning, mandatory CAD quarantine, executable-content rejection, and invariantly false
  retrieval/provider/catalog/drawing authority.
- Bounded exact-URL manufacturer retrieval is implemented with engineer/date/hash-bound
  approval, public-address validation, no proxy/redirect, byte/type/signature/hash checks,
  atomic no-overwrite quarantine, and a self-hashed negative-authority result. Engineer
  property/asset mapping into the shared model and reviewed symbol catalog is next.
- Manufacturer model sidecars and symbol candidates now bind exact model/bundle/claim/
  retrieval/quarantine/CAD/proposal hashes without model, catalog, or drawing mutation.
  Separately confirmed backed-up catalog registration now preserves durable source lineage
  and restores the exact prior catalog if lineage persistence fails. Authenticated read-only
  API/palette review and live official manufacturer evidence/CAD acceptance are next.
- The immutable Milestone 21 contracts, deterministic schedule generator, supported fixed-
  marker extraction, explicit UNKNOWN/CONFLICT behavior, authenticated API, rejection
  tests, package coverage, and visible read-only AutoCAD palette implementation are
  complete; the x64 DLL builds. Exact-hash producer run
  `cdd846e7d3c24aa59400a12721f32bdb`, independent inventory run
  `34f3d86e784b492faf9ed50d0f4083de`, complete visual context
  `a5b86d29e9724b7bb94e6111edb26468`, and fail-closed visual context
  `fc768c23c4324a3b8b9f0c57adbaac06` all passed. Keep drawing creation/update behind
  the existing preview, review, one-use approval, scratch execution, recovery, and
  verification lifecycle.
- Direct visual VS Code Operator-panel observation remains a supporting acceptance gap;
  it does not block starting the read-only Milestone 21 contracts.

## Exact current status

### Completed and verified before the v5.4 handoff

- Milestones 0–17 are complete, including the authenticated loopback API, safe local
  developer platform, VS Code/AutoCAD foundations, shared electrical model, read-only
  AutoCAD context, and the Milestone 17 AutoCAD copilot palette live acceptance.
- Milestone 18 contract/lifecycle work is partially implemented: typed heterogeneous
  drafting proposals, deterministic validation, durable ownership/expiry/cancellation,
  approval binding, replay protection, and authenticated drafting API routes.
- The Operator Bridge supports Local Ollama, Codex, Hybrid Local/Codex, and optional
  OpenAI provider adapters while keeping execution authority inside AI-Engineer.
- The local provider target is Ollama with `qwen2.5-coder:7b` and bounded context/timeouts.
- v5.3 live Windows testing proved that the local provider could plan, execute real
  `project.scan` actions, verify results, and feed observations back to the model.
- That v5.3 run also exposed a no-progress loop: the model repeatedly proposed the same
  successful `project.scan` instead of progressing to `file.read`.
- v5.4 adds duplicate-action/short-cycle detection, preserved explicit ONLY-tool scope,
  stronger command-scope enforcement, live working/progress/activity UI, and persistent
  Cancel visibility.
- The v5.4 source in this handoff passes the full automated suite: **330 passed** with
  two dependency deprecation warnings. `python -m compileall -q .` also passes.

### Destination Windows connection repair — 2026-08-09

- Initial evidence showed no port 8765 listener and a failed health request while the
  session token was present and extension 0.1.3 was installed.
- Direct and wrapper-equivalent Python launches stayed alive; health returned
  `{"status":"ok","version":"0.18.0"}` and authenticated developer status returned
  200. The protected route returned 401 without authentication.
- Launchers now wait for real health instead of treating the persistent PowerShell
  wrapper as proof that Python is alive.
- VS Code extension 0.1.4 now distinguishes API unavailable, authentication failed,
  provider unavailable, and successful API/provider states. The rebuilt VSIX was
  integrity-tested and installed as `ai-engineer-local.ai-engineer-operator@0.1.4`.
- Destination verification now passes 340 tests with one intentional skip and one dependency
  warning; compileall, JavaScript syntax, and PowerShell parser checks pass.
- The separately approved `qwen2.5-coder:7b` Ollama model is installed and warmed. Live
  run `52554b66d7c94dc28ec0091552f81ba5` passed authenticated read-only acceptance with
  one `project.scan` and two useful `file.read` actions, all verified, then completed
  without repetition. A separate live run proved Cancel responded during Ollama inference
  in 0.039 seconds and remained durable after the late provider response.
- The ordered post-read-only API acceptance also passes. `file.write` created then
  overwrote one disposable workspace file with a verified external backup; bounded
  compileall returned 0; HIGH pytest stopped for a one-use approval then completed with
  337 passed/1 skipped; and AutoCAD Core Console inventoried an official copied Autodesk
  fixture under `/readonly` as 1 layer/24 entities with `DBMOD=0` and unchanged hashes.
- Milestone 18 lifecycle hardening and read-only preview acceptance are complete. The
  AutoCAD palette revalidated the exact context/catalog/proposal evidence and displayed
  seven ordered preview objects. Live run `ac93826c27ec438ebba0b1f68205bc7a`
  reached only `preview_ready`; the OS-read-only drawing hash remained unchanged and
  both drawing mutation and execution approval remained false.

### Important: final v5.4 Windows visual UI acceptance is NOT complete

On the last source computer, v5.4 was installed and started, but the VS Code panel stayed
at:

`Connecting to AI-Engineer…`

The misleading indefinite state is fixed in source and extension 0.1.4. Provider-backed
read-only API acceptance and responsive cancellation now pass on Windows. A user-visible
panel refresh plus direct observation of Working/progress/activity/Cancel still requires
acceptance in VS Code; do not infer visual success from API and automated evidence.

The direct VS Code Operator panel observation remains a supporting acceptance gap.
Milestone 18 live AutoCAD scratch execution acceptance passed on 2026-08-10 with exact
unchanged-source, output, recovery, and one-use lifecycle evidence. Milestone 19 now has
typed, self-hashed, source-linked device counts and deterministic route lengths plus an
authenticated read-only report/CSV API. Hash-bound CSV and live AutoCAD 2021 acceptance
passed on 2026-08-10 without drawing or file writes. The first balanced current and
approximate voltage-drop slice also passed live AutoCAD acceptance with result SHA-256
`582af6bc273247372539eecdbd334f4beeeb4f20848dc426025f783a93ea070e`.
The `m20.load_aggregation.v1` slice now passes deterministic engine, authenticated API,
strict rejection, package, palette, and live AutoCAD acceptance. Result SHA-256 is
`6fd06995638c3f52ecdb440dd2684d2b6692a466c73e9d069b4fbc29856e60e0`;
the exact balanced 4000 W result retained false compliance/mutation flags and an unchanged
read-only drawing. The active product task is now reviewed ampacity inputs and a traceable
derating chain before any cable/protective-device selection claim.

The `m20.cable_ampacity_derating.v1` engine, authenticated API, strict rejection, package,
AutoCAD palette, DLL build, and live AutoCAD acceptance pass the `385 passed, 1 skipped`
baseline. Result SHA-256 is
`53c66553f7c2af7c4760725fc9763f1d5851c84a809b52227e0476df94e96b5a`;
the exact route-linked 20 A x 0.94 x 0.8 chain produced 15.04 A with false selection,
compliance, and mutation flags and an unchanged read-only drawing. The active task is now
the short-circuit input/result boundary.

The `m20.short_circuit_source_equivalent.v1` boundary, authenticated API, benchmark and
rejection tests, package, AutoCAD palette, and DLL build pass the `392 passed, 1 skipped`
automated baseline. It retains one exact route, reviewed voltage and source/path R/X basis,
symmetrical RMS/fault-power outputs, and false equipment/peak/arc-flash/compliance/mutation
flags. Live AutoCAD acceptance passed with result SHA-256
`f2e11584c488c85a9410b9c0deaf74593729246068fcc5ada0d9ff4a73d17fd6`,
2434.322478 A symmetrical RMS, false equipment/peak/arc-flash/compliance/mutation flags,
and an unchanged read-only drawing. The automatic `/b` restart twice produced a background
AutoCAD process without a main window, so the accepted retry used the launcher's new manual
plugin-load mode and `NETLOAD`; the calculation/API boundary was unchanged.

Milestone 20 is not closed yet. Reconciliation against its documented scope found the
remaining reviewed cable/protective-device adequacy and PE/earthing rule interfaces. They
are now explicit in `WORK_QUEUE.md` and must not be represented as completed by the
short-circuit boundary.

`m20.candidate_adequacy.v1` now includes the deterministic core, strict authenticated
composite API, package coverage, and AutoCAD review palette. The API recalculates its
current/drop, ampacity, and fault inputs server-side against re-imported exact context
evidence and rejects client result payloads, coercion, and inconsistent routes. The palette
shows reviewed candidate inputs and all five PASS/FAIL findings without selection,
compliance, or mutation claims. The repository passes `397 passed, 1 skipped` plus
compileall. Read-only AutoCAD 2021 acceptance passed on 2026-08-11 with all five findings
PASS under result SHA-256
`4695740c2634ba899ab77a3fc70e7aad9a0542288fedb251682808ed8d8eca42`.
The source and disposable copy remained byte-identical at SHA-256
`4b9b3af7f0091c5392896087a196332c3bf5f048487720d9b2ab5c2f90cd8535`.

`m20.protective_conductor_adiabatic.v1` is implemented through core, strict API,
package, and AutoCAD review UI. It uses only explicit reviewed applicability,
disconnection time/basis, conductor factor, source identity, fixed units, and exact
same-route recalculated fault evidence. `UNKNOWN` and `NOT_APPLICABLE` do not emit a
calculated value. Compileall, DLL build, and `403 passed, 1 skipped` pass. Read-only live
acceptance passed on 2026-08-11 with result SHA-256
`b69287cd88b700b520e19b2daa5cec0646018f954f32ed4fb354fc8cc36abb3b`,
9.466627 mm2, unchanged source/copy hashes, and false selection/compliance/mutation flags.
Milestone 20 is complete within its deterministic reviewed-input boundary.

## Session checkpoint - 2026-08-10 end of day

Start the next session from `docs/WORK_QUEUE.md`; do not reconstruct the state from chat.

Completed and live accepted today:

- Milestone 19 quantity takeoff and reviewed CSV on four devices/four routes.
- `m20.lv_current_voltage_drop.v1`, live result SHA-256
  `582af6bc273247372539eecdbd334f4beeeb4f20848dc426025f783a93ea070e`.
- `m20.load_aggregation.v1`, live result SHA-256
  `6fd06995638c3f52ecdb440dd2684d2b6692a466c73e9d069b4fbc29856e60e0`.
- `m20.cable_ampacity_derating.v1`, live result SHA-256
  `53c66553f7c2af7c4760725fc9763f1d5851c84a809b52227e0476df94e96b5a`.
- `m20.short_circuit_source_equivalent.v1`, live result SHA-256
  `f2e11584c488c85a9410b9c0deaf74593729246068fcc5ada0d9ff4a73d17fd6`.
- The long LV palette now pins command buttons above independently scrollable inputs.
  `START-AI-ENGINEER-AUTOCAD-PREVIEW.ps1 -ManualPluginLoad` is the accepted fallback
  when AutoCAD `/b` startup creates a background process without a visible main window.
- The source/read-only acceptance drawing stayed byte-identical at SHA-256
  `4b9b3af7f0091c5392896087a196332c3bf5f048487720d9b2ab5c2f90cd8535`.
- Full verification baseline: `395 passed, 1 skipped`; compileall passed. AutoCAD and
  localhost port 8765 were closed at session end.

Implemented but not product-complete:

- `m20.candidate_adequacy.v1` core/API/package/AutoCAD UI and DLL build are complete.
- The composite API recalculates all three source results against exact context evidence;
  it never trusts client-submitted result numbers or performs automatic selection,
  compliance approval, or drawing mutation.
- Automated verification is `397 passed, 1 skipped`; compileall and live AutoCAD
  acceptance passed. The candidate-adequacy slice is complete within its stated boundary.

Next session, in order:

1. Start Milestone 21 from `docs/WORK_QUEUE.md`: define exact-evidence-bound panel,
   outgoing-way, load-assignment, protective-device, and SLD topology contracts.
2. Keep schedule/SLD extraction and generation read-only until exact preview/review and
   the existing one-use drafting execution/recovery lifecycle are deliberately applied.

The temporary `START-SHORT-CIRCUIT-ACCEPTANCE.bat` was deleted after acceptance; a stale
open VS Code tab does not mean the file still exists.

## Destination-computer start sequence

1. Copy/extract this package into a new folder. Do not overwrite a known-good older copy.
2. Close all VS Code windows and any old AI-Engineer bridge windows.
3. Run `CHECK-NEW-PC.bat` and review the prerequisite report.
4. Run `SETUP-AI-ENGINEER.bat` once. It installs Python requirements, runs tests, installs
   the VS Code extension, checks the bridge configuration, starts a local bridge, and
   opens VS Code.
5. Close VS Code and the temporary setup bridge after setup completes.
6. Run `START-AI-ENGINEER-LOCAL.bat` for the real local-Ollama acceptance session.
7. Keep the Operator Bridge PowerShell window open.
8. Before starting a developer run, open this URL in the browser:

   `http://127.0.0.1:8765/health`

   Expected result is JSON with `status` equal to `ok` and version `0.18.0`.
9. In VS Code, open **AI-ENGINEER: OPERATOR**, press Refresh, and expect:

   `API ok · 0.18.0 · provider ollama`

10. If the browser health endpoint works but the panel still says Connecting, follow
    `docs/KNOWN_ISSUES_V5_4.md` and capture exact diagnostics before changing code.

## Read-only Operator acceptance test

After the panel shows API/provider success, submit exactly this request:

```text
Inspect the current AI-Engineer project using ONLY these tools:

- project.scan
- file.read

Do not use any other tool.
Do not modify any file.
Do not run tests or terminal commands.

Report:
1. Current architecture
2. Main components
3. Current development status
4. Completed parts
5. Unfinished parts
6. Recommended next development step
```

Expected behavior:

- reviewed plan remains inside the two-command allowlist;
- `project.scan` succeeds once and is not repeatedly re-executed with the same payload;
- the agent progresses to useful `file.read` actions or completes with a justified report;
- the panel visibly shows Working state, elapsed time, progress, activity, and Cancel;
- no file modification, terminal command, Git action, or AutoCAD command occurs.

If an exact already-successful action is proposed again, the orchestrator must block it
before execution and request a different useful step. Preserve the run state and exact
error/activity text if that fails.

## Acceptance sequence after read-only success

Do not jump directly to AutoCAD mutation. Continue in this order:

The authenticated API path for steps 1–4 passed on destination Windows on 2026-08-09.
Repeat them through the visible panel only as UI acceptance; do not repeat HIGH commands
silently or treat this as approval for later drawing mutation.

1. **Disposable file-write test** in the transferred test folder only, with backup and
   exact verification.
2. **`python.compileall` test** through the bounded command path.
3. **`python.pytest` HIGH-risk approval test**: confirm it cannot execute without the
   explicit durable approval, then approve only the controlled test run.
4. **AutoCAD read-only acceptance** on a disposable/read-only fixture.
5. Preserve the completed **Milestone 18 scratch execution** and **Milestone 19 takeoff**
   evidence, then continue with Milestone 20 deterministic low-voltage calculations. Do
   not treat either completed run as approval for handoff or source replacement.

## Active development work

The authoritative unfinished work is `docs/WORK_QUEUE.md`. In summary:

- continue Milestone 20 with typed connected/demand aggregation, explicit diversity rule
  inputs, and phase allocation/balance; the first current/voltage-drop slice is complete;
- complete Operator Bridge Windows live acceptance, including the current v5.4
  connection check and Local/Codex/Hybrid provider acceptance;
- do not begin ETAP or SOLIDWORKS Electrical before the AutoCAD product gate completes;
- after AutoCAD drafting: quantities, deterministic electrical calculations, panels/SLD,
  pinned cited standards evidence, manufacturer evidence, ETAP, SOLIDWORKS Electrical,
  and final multi-PC professional 1.0 acceptance.

## Safety boundaries that must remain intact

- API remains authenticated and bound to `127.0.0.1` by default.
- File operations stay inside configured workspace roots.
- Backups/recovery material stay outside the writable workspace where required.
- HIGH/DESTRUCTIVE operations require explicit approval; no silent bypass.
- AutoCAD drawing mutation remains behind proposal → validation → preview → approval →
  execution → verification/recovery boundaries.
- Engineering calculations and code-compliance decisions must use deterministic tested
  engines and project-pinned official/licensed evidence; do not rely on model memory for
  safety-significant values or code clauses.
- Never claim live Windows/AutoCAD success from Linux-only automated tests.

## Test baseline for this transferred snapshot

Verified in the packaging environment on 2026-08-08:

```text
PYTHONPATH=. python -m pytest -q
330 passed, 2 warnings

python -m compileall -q .
PASS
```

The warnings are dependency deprecation warnings from the websockets/uvicorn stack, not
functional test failures. Destination verification must use a short explicit writable
`--basetemp` because the deeply nested transferred package path exceeds Windows Git
test path limits. The exact post-preview baseline is recorded in `HANDOFF_STATE.json`.
## 2026-08-16 saved continuation point

The AutoCAD beta real-project acceptance state is preserved in
`docs/evidence/autocad-real-project-lighting-2026-08-13.md`, `docs/WORK_QUEUE.md`, and the exact
self-hashed sidecars under `workspace/.ai-engineer/autocad/project-conventions` and
`workspace/.ai-engineer/autocad/project-topology`.

- `lighting.dwg`: 3,323,467 bytes; SHA-256
  `1d08ccb0557e740f7d471e006d5accad0675d54b82e45d9aabc64e239ecfb117`.
- Device/tag acceptance: 12 reviewed devices, panel `DB3-GA2`, circuit `4`, zero unknowns.
- Exact circuit membership: all 12 stable block handles assigned to circuit 4; `a/b/c` and `WP`
  remain observed but not spatially associated.
- Route acceptance: nine complete E-Lite-Circ polylines, zero unknowns, maximum 1,025 vertices,
  and 10.2773298425 m observed centerline.
- The exact connection-anchor contract and read-only `AIENGCONNECTIONANCHOR` command are implemented,
  tested, and compiled for AutoCAD 2021. They require explicit device/route/endpoint/terminal review,
  unchanged DBMOD, complete connected-or-unresolved endpoint accounting, and exact evidence hashes.
- Route connectivity and installed cable length remain false/unconfirmed because the new command has
  not yet been run across the real drawing endpoints.
- Current automated baseline: `509 passed, 1 skipped`; compileall and the AutoCAD DLL build pass.

Resume by opening a fresh read-only `lighting.dwg` copy and running `AIENGCONNECTIONANCHOR` for each
reviewed endpoint decision, then assemble and validate the complete self-hashed review. Do not use
proximity alone, crossing geometry, or insertion points of nested switch blocks as electrical
connectivity. Do not treat observed centerline as procurement/installed cable length.
