# AutoCAD 2021 integration

AI-Engineer currently provides detection, read-only scratch inspection, bounded
aggregate drawing inventory, a fixed scratch-line writer, and a bounded electrical
wire-route writer. A versioned symbol catalog can create and place one hash-approved
block in a scratch drawing, and read-only intake can review a compatible user block.
Milestone 16 adds a buildable read-only .NET bridge that exports the active drawing or
the engineer's explicit selection as bounded structured JSON for hash-bound local
import. The operator channel also supports a chunked full-drawing index: the plugin
publishes a local active-document identity beacon, then an exact request scans top-level
entities in Model and paper layouts, deterministically orders them by handle, writes
complete adaptive context shards (starting at 64 entities and halving when payload density
requires it), and commits one source-SHA-bound manifest only after
every shard succeeds. The loader rejects overlap, omission, truncation, tampering, or
document-identity mismatch. Nested block definitions and Xrefs remain explicitly outside
this first index scope. The validated shards can be aggregated into one immutable inventory and
reloaded independently. Only exact handles from reviewed route-membership evidence become
electrical routes; a matching layer creates a route candidate, while reviewed arrow/leader
geometry remains annotation. Inventories can be compared by stable handle to distinguish added,
removed, geometry-changed, semantically reclassified, and unchanged entities. Complete-capture
comparison, same-session selective recapture, and validated delta composition into an immutable v2
inventory are supported. The application
does not modify an original DWG, accept arbitrary AutoCAD
commands, permanently change AutoCAD security settings, or automate the GUI.

The complete read-only cycle can be orchestrated without a manual selection while AutoCAD and the
local channel are running:

```powershell
python -m integrations.autocad_inventory_refresh workspace `
  plugins/electrical_symbols/catalog.json `
  --convention-profile workspace/.ai-engineer/autocad/project-conventions/lighting-routes-9446c5a6.json `
  --route-membership workspace/.ai-engineer/autocad/project-topology/lighting-db3-ga2-circuit-4-routes.json `
  --baseline-inventory workspace/.ai-engineer/autocad/drawing-inventory/765eab778e9eb39955175663bf686967.json
```

The coordinator fails closed when no exact active drawing or more than one match is found. A changed
source DWG may still receive a structural inventory and baseline comparison, but old source-bound
reviewed semantics are withheld and the report sets `semantic_review_required=true`.

The unchanged-source path passed live AutoCAD 2021 acceptance on 2026-08-23. The isolated
`lighting.dwg` copy produced 4,848/4,848 unchanged entities, no detected changes, valid reviewed
semantics, no provider egress, and `DBMOD=0 -> 0` in a 1.093-second index capture. Exact evidence is
recorded in `docs/evidence/autocad-inventory-refresh-live-acceptance-2026-08-23.md`.

The changed-source path also passed live acceptance using a separately approved Scratch copy with
one added LINE. It reported one exact added handle, no false removed/changed handles, invalidated
all 991 source-bound semantic classifications, and required semantic review. The original drawing
remained byte-identical. See
`docs/evidence/autocad-inventory-refresh-changed-source-live-acceptance-2026-08-23.md`.

## In-session selective delta

After a successful complete index, the plugin arms an in-memory baseline and listens only to
AutoCAD database append, modify, and erase notifications. A later saved same-session change can be
captured without re-exporting unchanged entities:

```powershell
python -m integrations.autocad_drawing_delta workspace `
  <baseline-index-id> <baseline-index-sha256>
```

The request is read-only and contains no command string. Added and modified entities are exported
in bounded hash-bound context shards; removed handles have no synthetic geometry. The journal is
one-use and capped at 4,096 candidates. Closing/restarting AutoCAD, using a different document,
missing the exact baseline, overlapping partitions, shard mismatch, or overflow fails closed and
requires a new complete index. Live same-session Scratch acceptance returned exactly one added,
one modified, and one removed handle in 25.6422 ms at `DBMOD=0 -> 0`; the unchanged control handle
was not reported. The exact evidence then composed into an independently reloadable v2 inventory:

```powershell
python -m integrations.autocad_inventory_delta workspace `
  <baseline-inventory.json> <delta-id> <delta-sha256> `
  plugins/electrical_symbols/catalog.json
```

The v2 artifact records the baseline inventory SHA-256 and delta ID/SHA-256. It recalculates only
catalog-stable meaning, clears source-bound convention and topology hashes, and requires semantic
review. See `docs/evidence/autocad-selective-delta-live-acceptance-2026-08-23.md`.

For reviewed legacy/project conventions, context import may discover one self-hashed profile under
`workspace/.ai-engineer/autocad/project-conventions`. A profile is applied only when its document
fingerprint, context ID, and context-evidence SHA-256 all match exactly. It can alias exact
block/layer pairs, approve a route layer with endpoint-only connectivity, and approve fixed tag
grammars. It cannot connect crossings or spatially associate control tags. Invalid or multiple
matching profiles fail closed.

## Read-only drawing context

`integrations/autocad_context_plugin/ContextCommands.cs` exposes only
`AIENGDRAWINGCONTEXT`, `AIENGCONTEXT`, `AIENGDEVICETAGCONTEXT`, and `AIENGROUTECONTEXT`. All open database objects with
`OpenMode.ForRead`; the latter uses only an implied pick set or an explicit AutoCAD
selection prompt. `AIENGDEVICETAGCONTEXT` filters that explicit selection to block references,
TEXT, and MTEXT, allowing convention/topology review without accidentally ingesting dense route
or architectural geometry. The bridge allowlists lines, lightweight polylines, arcs, circles,
`AIENGROUTECONTEXT` filters an explicit region to LINE/lightweight-POLYLINE objects on the layer of
one separately picked representative route. It does not accept every line in the window or infer
that crossings connect. Route polylines retain up to 4,096 vertices; earlier 256-point contexts
remain readable only as their original stricter bound and truncation state declare.
The bridge allowlists lines, lightweight polylines, arcs, circles,
block references and attributes, text, mtext, and dimensions. Other runtime types are
retained as explicit `UNSUPPORTED` observations rather than assigned engineering
meaning.

The same assembly includes `AIENGPALETTE` from `PaletteCommands.cs`. It creates an
Autodesk `PaletteSet` containing a bounded Arabic/English WinForms control. After the
engineer runs `AIENGCONTEXT`, the palette displays the last context identity/count and
can create an owner-key-protected local session, show device/route/unknown/missing and
warning counts, append engineer messages, show local progress, and cancel. When no
provider is injected, it explicitly reports that messages are stored locally and
produces no AI answer or network egress.

The palette also includes a separate `Drafting Preview` tab. The engineer explicitly
selects a proposal JSON inside the configured workspace, and the tab binds it to the
current context ID and evidence SHA-256 before requesting deterministic validation and
preview from the authenticated loopback API. It displays the ordered proposed layer,
devices, terminals, circuit, and route geometry, plus the preview hash and expiry. This
tab rejects any preview response that claims drawing mutation or execution approval.
After preview, separate controls may bind one source DWG/DWT path and SHA-256 to an
execution review, approve or reject that exact request, and consume an approval once.
Execution supports only one validated circuit, one `E-WIRE` route, and two through
eight placements of one reviewed symbol type. It modifies a new scratch copy, records
the unchanged source plus output and timestamped recovery-evidence hashes, and requires
another approval for any later handoff; it never replaces the source. `DBMOD`
view/window-only bits 8 and 16 are permitted; persistent object, database-variable, or
field changes (bits 1, 4, and 32) are blocked before review.

The `Quantity Takeoff` tab reuses the exact in-memory context identity and evidence
SHA-256. It accepts explicit vertical, per-end termination, and waste inputs, displays
the self-hashed `m19.takeoff.v1` device/route report, and leaves unclassified objects
visible. CSV creation is a separate confirmation bound to the displayed report SHA-256;
the server re-imports and recalculates the context before returning bounded UTF-8 content.
The tab displays the CSV and its hash but has no drawing-write or file-write path.

The `LV Calculations` tab first requires the same complete recognized context, then derives
the exact device and route IDs through the takeoff API. The engineer visibly supplies and
reviews phase system, output power, demand factor, nominal voltage, power factor,
efficiency, conductor R/X, reviewer, and assumptions. The result displays deterministic
current/drop values, formula identities and expressions, warnings, result SHA-256, and
explicit false compliance/mutation flags. The tab has no drawing-write path and makes no
cable-sizing or standards-compliance claim.

The same tab also provides a separate `Aggregate loads` command. It applies visible
per-load connected power and demand factor, one explicit diversity factor for the selected
group, and an exact comma-separated phase assignment for every recognized device using
`l1`, `l2`, `l3`, or `three_phase`. The returned group and phase totals, spread, maximum
deviation, formulas, warnings, and result SHA-256 are displayed locally. This is a reviewed
project rule-set calculation with no built-in phase-balance acceptance limit, provider
call, drawing write, or compliance claim.

The `Apply derating` command in the same tab selects one recognized route by visible
one-based route number. The engineer supplies a cable designation, base ampacity, ambient
and grouping factors, their reviewed basis, reviewer, and assumption. The palette displays
the ordered incoming/factor/outgoing stages, combined factor, derated ampacity, formula,
warnings, result SHA-256, and explicit false cable/protection selection, compliance, and
mutation flags. It does not decide whether the candidate is adequate.

The `Calculate short circuit` command selects one recognized route and collects the fault
arrangement, nominal voltage, voltage multiplier, source/path R/X, impedance basis,
reviewer, and assumption. It displays total impedance, symmetrical RMS current, apparent
fault power, formulas, limitations, and result SHA-256. Protective-device adequacy, peak
current, arc flash, compliance, and drawing mutation remain explicitly false.

Because this tab contains several independent calculation slices, command buttons are
pinned above a separately scrollable input pane; the result pane remains visible below.
If AutoCAD starts as a background process when invoked with the automatic `/b` script,
the preview launcher supports `-ManualPluginLoad`: start the read-only drawing visibly,
then run `NETLOAD` for the printed bridge path followed by `AIENGPALETTE`.

The separate `Manufacturers` tab loads one bounded reviewed JSON package and calls only
authenticated loopback `POST /v1/manufacturers/review`. It displays the exact legal/brand
identity, family/model/part number, evidence and result hashes, official artifact URLs,
media and content hashes, optional quarantine/model/candidate/registration lineage, and
`PASS`/`UNKNOWN`/`CONFLICT` findings. It rejects any review response claiming provider
egress, retrieval, catalog registration, or drawing mutation. It contains no download,
file-write, database-write, or AutoCAD command path. Official Legrand part `004280` passed
live visual acceptance on 2026-08-12 with exact PDF/DWG, quarantine, bundle, and result
hashes; this was a functional fixture rather than project product approval.

The adjacent `Product Discovery` tab consumes a separately reviewed project-requirement
request. It offers `Verify free candidate file` for a user-prepared official-page candidate
draft without an AI provider, plus paid official-domain search when a provider is configured.
Free drafts may reference a browser-saved official HTML file inside the project when the
manufacturer blocks transparent automated retrieval; AI-Engineer validates its path, size,
official URL/host, part number, evidence text, and content hash locally. Paid search and the
direct free mode independently re-fetch official pages. Results show each
brand/model/part number, official product URL,
mandatory-requirement status, and per-requirement finding. The tab requires
the path-appropriate provider-egress flag and rejects any response claiming automatic selection,
download, catalog registration, or drawing mutation. Without a configured provider the
local API returns 503; it never invents a candidate from model memory.
The comparison output occupies the expandable panel area and wraps long hashes/URLs. `Copy
complete result` copies exactly the displayed review text to the Windows clipboard without
calling the API or granting any follow-on authority.

The optional production provider uses OpenAI Responses web search only after the separate
manufacturer-discovery and OpenAI egress opt-ins. Hosted search is limited to the approved
domains in the signed request. AI-Engineer then independently re-fetches each exact official
product page, checks public DNS/no proxy/no redirect/type/size, verifies the returned part
number and property evidence against the bytes, and calculates the page SHA-256 locally.
Successfully validated results are cached in the local API process by exact discovery-request
SHA-256 (maximum 32), so repeating the same approved search does not create another paid call.
Changed request hashes are searched separately, and failures are never cached.
The displayed evidence mode includes the exact configured model, so Economy and Quality results
remain distinguishable in copied output and signed review records.
After comparison, the engineer may click `Choose for separate review`; this produces a
self-hashed local choice record but does not approve, download, register, or insert anything.

For the fixed production-path acceptance, run
`START-AI-ENGINEER-AUTOCAD-DISCOVERY.ps1`. Choose `FREE`, `ECONOMY`, or `QUALITY` when
prompted (or pass `-SearchMode Free|Economy|Quality`). `FREE` needs no OpenAI key and verifies
a candidate JSON file. `ECONOMY` uses `gpt-5.6-luna` with low reasoning; `QUALITY` uses
`gpt-5.6` with high reasoning. Paid modes require exact one-session confirmation and keep
the OpenAI key only in the local API process, removing it before AutoCAD starts. Every mode
builds/reuses the self-hashed two-domain fixture and preloads it into `Product Discovery`.
The browser-assisted template is
`workspace/manufacturer-evidence/free-candidates.template.json`; save the matching official
pages as the template's `evidence_path` values before selecting that JSON file.
The run is incomplete unless at least two locally verified candidates are visible and one
choice-for-follow-on-review is recorded with all follow-on action flags false.

The read-only live acceptance passed on 2026-08-10 against an explicit four-entity
millimeter selection. Run `ac93826c27ec438ebba0b1f68205bc7a` reached only
`preview_ready`; preview SHA-256
`e5f47cc19fe894998313b62b4363629d4a422552994c56da56be889b93e432b0`
contained seven ordered objects. The OS-read-only drawing hash remained
`54d4ff5fa776e45612a939b26b6adfab1a5c556cc75eb9ad6cdea7803cca17c5`,
and both `drawing_mutation_performed` and `execution_approved` remained false.
The execution controls passed live AutoCAD 2021 acceptance on 2026-08-10 under a new
execution-specific approval. The run created exactly two block references and two route
segments in a scratch DWT, while the read-only source remained unchanged. Output
SHA-256 was
`4b9b3af7f0091c5392896087a196332c3bf5f048487720d9b2ab5c2f90cd8535`
and recovery-evidence SHA-256 was
`1d6292f2532539cb928484a7e4f72ebde46137e2340ea596a246d4d12c7f21e7`.
No handoff or source replacement occurred.

Quantity takeoff and reviewed CSV passed live AutoCAD 2021 acceptance on 2026-08-10
against eight selected metric objects. The complete report contained four reviewed
`socket_single` devices and four source-linked 0.5 m routes. Report SHA-256 was
`b6417994dfc1cb9a4598cd9ad93856b531001d5d089b8a6b26f7281b16d434e9`;
CSV SHA-256 was
`c21dfc0961dc0af9342288c964644c5195591b0f291a94e3e487b5c4e15db8eb`.
No drawing or file write occurred, and the OS-read-only drawing copy remained identical
to its source at SHA-256
`4b9b3af7f0091c5392896087a196332c3bf5f048487720d9b2ab5c2f90cd8535`.

When an injected provider is configured, local message submission remains separate.
The engineer must create an egress preview that displays the exact question and context
summary, each UTF-8 byte count, and the aggregate SHA-256. Only the distinct
approve-and-ask button can use that review once; reject records denial. Returned text
is shown only after server-side same-language, approved-citation, size, timeout, and
no-command validation. The palette never stores the API token or owner key on disk.

Build the bridge for the current machine with:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File integrations\autocad_context_plugin\build.ps1 -WorkspaceRoot (Resolve-Path workspace).Path
```

The build script validates the AutoCAD 2021 managed references and creates an x64 DLL
plus bounded `settings.json` below the selected workspace. AutoCAD `SECURELOAD` stays
enabled; the bridge directory must be reviewed and trusted before `NETLOAD`. Context
files are written atomically only under
`.ai-engineer/autocad/context/inbox/<context-id>.json` and declare
`provider_egress_allowed=false`.

Local import requires the exact context ID, SHA-256, workspace containment, matching
limits, and durable approval. It maps catalog-reviewed blocks and `E-WIRE` linework,
keeps handles and document identity as provenance, and reports everything else as an
unknown object. No Milestone 16 path calls a provider or the internet.

## Validated platform choices

Autodesk documents ActiveX Automation as a Windows-only supported interface for
AutoCAD, and identifies `AutoCAD.Application.24` as the version-dependent AutoCAD
2021 ProgID:

- [AutoCAD 2021 Application Object](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-ActiveX-Reference/files/GUID-0225808C-8C91-407B-990C-15AB966FFFA8.htm)
- [AutoCAD 2021 ActiveX development](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-Customization/files/GUID-2090E4E8-9AE0-4E01-B5EB-0843A30EB0E9.htm)
- [AutoCAD version-dependent ProgIDs](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-ActiveX-CAO/files/GUID-0EDC04D5-2ACB-4555-B5AC-936D54A9FF61.htm)

AutoLISP and other executable customization must obey `SECURELOAD` and
`TRUSTEDPATHS`. AI-Engineer does not lower or rewrite either setting:

- [AutoCAD 2021 SECURELOAD](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-Core/files/GUID-541566C6-2738-49DD-87C3-C1490E924A02.htm)
- [AutoCAD application security requirements](https://help.autodesk.com/cloudhelp/2021/ENU/OARX-IOP/files/GUID-332144C1-3562-493F-B361-9D1A21AF540E.htm)

The inventory pilot uses documented aggregate database operations only:

- [`tblnext`](https://help.autodesk.com/cloudhelp/2024/DEU/AutoCAD-LT-AutoLISP-Reference/files/GUID-1720D8DC-5559-4AC1-8A75-3C932834D77C.htm)
  rewinds and iterates the `LAYER` symbol table;
- [`ssget` with `X`](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-AutoLISP-Reference/files/GUID-0F37CC5E-1559-4011-B8CF-A3BA0973B2C3.htm)
  selects all database entities, including those on off or frozen layers;
- [`sslength`](https://help.autodesk.com/cloudhelp/2025/ENU/AutoCAD-LT-AutoLISP-Reference/files/GUID-034B9EC8-0945-48A0-802A-9725DDBA0EF2.htm)
  returns the selection-set count.

The line writer uses Autodesk's documented command and entity-query behavior:

- [`command-s`](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-AutoLISP-Reference/files/GUID-F9ABE09D-9A71-4C00-8E5D-5BA70C3797B5.htm)
  executes the fixed `LINE` and `QSAVE` commands with supplied bounded arguments;
- [`entlast`](https://help.autodesk.com/cloudhelp/2026/PLK/AutoCAD-LT-AutoLISP-Reference/files/GUID-75DBA9B2-034B-4377-A4E2-21D37B298D86.htm)
  returns the newly added main entity;
- [`entget`](https://help.autodesk.com/cloudhelp/2024/PLK/AutoCAD-LT-AutoLISP-Reference/files/GUID-12540DAE-C84B-4BDB-AEEC-DDFE5BE3C42A.htm)
  provides its DXF type for strict `LINE` validation;
- [`QSAVE`](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-Core/files/GUID-99900C99-230D-4709-B8DD-A44435328A13.htm)
  saves the already named scratch drawing without choosing another path.

The electrical layer policy uses these published references:

- [United States National CAD Standard layer guidelines](https://www.nationalcadstandard.org/ncs5/pdfs/ncs5_clg_lnf.pdf)
  define a discipline designator followed by a major group; `E-WIRE` adopts that
  naming shape for electrical wirework;
- [NCS examples and updates](https://www.nationalcadstandard.org/ncs5/new.php)
  explain that listed layer names are not exhaustive;
- [Autodesk LAYER DXF group codes](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-DXF/files/GUID-D94802B0-8BE8-4AC9-8054-17197688AFDB.htm)
  define name, color, and linetype records used for exact validation;
- [Autodesk symbol-table creation](https://help.autodesk.com/cloudhelp/2026/PLK/AutoCAD-AutoLISP/files/GUID-7B91F774-B0CD-4F5B-9FFA-1130443E659B.htm)
  documents creating table entries with `entmake`.

The schema is NCS/AIA-inspired, not certified full NCS compliance. ACI color 3 is a
local project display choice and `CONTINUOUS` is fixed by this milestone.

The symbol writer follows Autodesk's documented block database model:

- [BLOCKS section](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-DXF/files/GUID-9DDFC343-6C87-4AF8-B3B9-77E0AB5A3031.htm)
  places definition geometry between `BLOCK` and `ENDBLK` entities;
- [BLOCK group codes](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-DXF/files/GUID-66D32572-005A-4E23-8B8B-8726E8C14302.htm)
  define the name, flags, layer, and base point;
- [INSERT group codes](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-DXF/files/GUID-28FA4CFB-9D5E-4880-9F11-36C97578252F.htm)
  define the block name, layer, insertion point, scale, rotation, and attribute flag;
- [`entmake`](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-AutoLISP-Reference/files/GUID-D47983BA-1E5D-417D-85B8-6F3DE5F506BA.htm)
  returns `nil` when required entity data cannot be created, enabling fail-closed
  geometry-count checks.

The installed `accoreconsole.exe /?` confirms support for input drawing and script
arguments plus `/readonly`. The current foundation uses that headless interface for
inspection. ActiveX and secure AutoLISP are detected future methods only; no COM call
or LSP loading is implemented yet.

## Commands

- `autocad.detect` is low-risk and returns product, release, language, exact ProgID,
  and boolean method availability without installation paths or serial/license data.
- `autocad.inspect_scratch` is high-risk and cannot run until a durable approval is
  consumed. It accepts one existing `.dwg` or `.dwt` inside a workspace root.
- `autocad.inventory_scratch` is independently high-risk and returns only bounded
  layer and entity counts from a copied drawing after durable approval.
- `autocad.create_line_scratch` is high-risk and creates exactly one line in a saved
  scratch copy from two finite, bounded three-number points.
- `autocad.create_wire_route_scratch` is high-risk and creates 1–15 `LINE` segments
  on fixed layer `E-WIRE` from 2–16 finite bounded three-number points.
- `autocad.list_electrical_symbols` is low-risk and returns validated catalog metadata
  and definition hashes without drawing execution.
- `autocad.place_electrical_symbol_scratch` is high-risk and creates one exact
  hash-approved `INSERT` in a saved scratch copy.
- `autocad.review_electrical_symbol_source` is high-risk and reviews one exact named
  block in a read-only scratch copy, producing an eligibility report and optional
  catalog proposal without registration.
- `autocad.register_electrical_symbol_review` is independently high-risk and appends
  only one exact persisted eligible proposal after source re-hashing and backup.
- `autocad.create_electrical_circuit_fragment_scratch` is high-risk and creates 2–8
  verified references plus 1–15 connected wire segments in one saved scratch copy.

- `autocad.create_panel_sld_fixture_scratch` is high-risk and accepts only a disposable
  drawing path plus its exact reviewed SHA-256. It creates the one fixed M21 acceptance
  fixture in a saved scratch copy; no marker value, block, script, or output path is caller supplied.
- `autocad.create_maximum_performance_fixture_scratch` is a high-risk release-test-only
  command. It accepts only a reviewed source path and exact SHA-256, creates a fixed
  1,792-LINE grid on reserved layer `AIENG-PERF-FIXTURE` in a scratch copy, and emits
  exact recovery evidence marked ineligible for drawing handoff.

Scratch inspection creates a UUID directory beneath
`workspace/.ai-engineer/autocad/runs`, atomically copies at most 256 MiB, writes a
fixed inspection script, and launches Core Console with `shell=False`, `/readonly`,
a filtered environment, no stdin, a 120-second timeout, bounded output, and process-
tree cleanup. The script reads only `ACADVER`, `INSUNITS`, `MEASUREMENT`, `TILEMODE`,
and `DBMOD`. It contains no save, load, object creation, arbitrary provider content,
or supplied command text.

The inventory script is also fixed. It iterates the layer table, obtains one `X`
selection set for all database entities, records only their counts, releases the set,
and requires `DBMOD=0`. It does not emit names, types, handles, text, attributes,
coordinates, geometry, or drawing-internal paths. The manager hashes bytes while
copying and re-hashes the source and scratch after Core Console exits; both must match
before success.

The writer never receives a caller-supplied command, script, layer name, file path for
output, or save format. Python normalizes validated finite coordinates to nine decimal
places within ±1,000,000,000 and renders only those numbers into a fixed expression.
Core Console receives the copied drawing
without `/readonly`, invokes `LINE` once and `QSAVE` once, and saves that same named
scratch file. Success requires one additional entity, created type `LINE`, `DBMOD=0`,
an unchanged source hash, and a changed scratch hash.

The wire-route writer uses the same writable scratch boundary. Its fixed expression
creates `E-WIRE` only when absent, then requires ACI color 3 and `CONTINUOUS`; a
conflicting existing layer fails closed without reporting success. It restores the
previous current layer, creates one `LINE` for each adjacent point pair, verifies each
last entity's DXF type and layer, and saves once. Success requires the exact requested
segment count, exact aggregate count growth, `DBMOD=0`, unchanged source hash, and
changed output hash. Callers cannot supply a layer, color, linetype, command, script,
block, or output path.

The catalog is a bounded UTF-8 JSON document under `plugins/electrical_symbols`. Each
entry has a version, strict identifier and block name, millimeter units, insertion
layer schema, empty attribute contract, allowlisted line/circle geometry, and a
canonical SHA-256. Loading re-computes the hash, so manual edits are detected. The
placement request repeats the exact hash, binding its durable approval to the reviewed
definition even if the catalog changes between listing and execution.

The fixed symbol expression refuses any same-named block already present rather than
silently redefining user content. It creates the definition on layer 0, validates the
three starter geometry entities, creates one `INSERT` on `E-POWR-DEVC`, verifies its
block name, layer, point, rotation, uniform scale, and absent attribute-follow flag,
then saves once. Success additionally requires one-entity growth, `DBMOD=0`, unchanged
source bytes, and changed scratch bytes. Caller input cannot add geometry, attributes,
commands, layer names, block names, or executable text.

Placement additionally requires `INSUNITS=4`, matching the current millimeter-only
catalog. This prevents millimeter geometry from being silently inserted with inch or
unitless interpretation.

User block intake is intentionally review-only. Its fixed expression looks up one
strict safe block name and follows its definition entities with `entnext`, stopping at
`ENDBLK` or 32 entities. Numbered markers carry only bounded `LINE`/`CIRCLE` geometry
and layer-0 flags; all other types are counted as unsupported, with separate `ATTDEF`
and nested `INSERT` counts. Python requires millimeter units, regular block flags,
nonempty untruncated supported geometry, layer 0 internals, no attributes/nesting,
consistent aggregate counts, `DBMOD=0`, and unchanged source/scratch hashes. Eligible
reviews produce proposed catalog JSON and its canonical hash but do not modify the
catalog or drawing.

Each review writes bounded evidence beside its scratch drawing. Registration accepts
only the run ID and exact source/proposal hashes, finds that evidence under configured
workspace roots, re-hashes the source, and refuses missing, changed, ineligible, or
duplicate definitions. It validates a staged complete next catalog, checks for a
concurrent catalog change, creates a timestamped byte-identical backup, atomically
replaces the catalog, and reloads it. Post-write failure restores the original bytes.
No drawing is opened and no block is imported during registration.

Existing blocks are reused only when flags, base point, ordered catalog geometry,
internal layer 0, exact entity count, and clean block termination all match. Circuit
fragment requests contain one symbol ID/hash, bounded transforms, and connected route
points; they cannot provide AutoLISP, layers, block names, geometry, or output paths.
The writer verifies each reference transform and wire endpoint, the exact total model
count, millimeter units, layer schemas, and `DBMOD=0`, then returns source/output hashes.

Live AutoCAD 2021 verification reused `AIENG_E_SOCKET_SINGLE_V1` and created three
references plus two `E-WIRE` segments. The source stayed at SHA-256
`f9566fb35f0a57007d343f1dd2a14886c57f99d1c99799f13b45bd460ae414ad`; the scratch
output SHA-256 was `1054d93b5e7008e6fe2427624eea09bd05cacd074b2e00c634a6b99ff6f2a839`.
An independent read-only inventory confirmed six entities, three layers, and `DBMOD=0`.
The complementary clean-template run created an absent approved block, two references,
and one wire segment, changing model entity count from zero to three with `DBMOD=0`.

Bounded console output is retained inside the UUID run directory for local diagnosis
but is never returned or copied to structured logs. A failed run retains its scratch
artifacts and returns the run ID; it never reports success without all markers.

## Current-machine verification

On 2026-08-05 detection succeeded for AutoCAD 2021 English release `24.0.47.0`:

- `acad.exe`: available;
- `accoreconsole.exe`: available;
- `AutoCAD.Application.24`: registered;
- ActiveX, secure AutoLISP, and read-only Core Console: detected as supported methods.

On 2026-08-06 a real `/readonly` inspection of a copied Autodesk `acad.dwt` completed
successfully under the initialized interactive Windows user. Core Console returned
AutoCAD `24.0`, `INSUNITS=1`, `MEASUREMENT=0`, `TILEMODE=1`, and `DBMOD=0` in about
0.67 seconds. The original Autodesk template was never passed to Core Console and no
output stream was truncated.

Milestone 17 live acceptance loaded the reviewed x64 assembly in interactive AutoCAD
2021, restored the prior `TRUSTEDPATHS` while retaining `SECURELOAD=1`, created an
explicit three-object selection context, and completed `AIENGPALETTE`. The exported
evidence contained three entities on two layers, `DBMOD=0`, no truncation, and
`provider_egress_allowed=false`. Interactive AutoCAD rewrote writable acceptance
copies despite reporting `DBMOD=0`; the passing safety run therefore used a separate
OS-read-only controlled DWG. Its SHA-256 remained
`ac3a5ad27cbcacfa6ad2bb4970200d19aecfab59fbc239f006ab426033a33c4e` before and after.
The original Milestone 16 DWT was restored byte-for-byte to its documented hash after
the first writable-host experiment, and the unexpected result was retained under
`backup` for diagnosis rather than represented as a successful unchanged-file run.

Milestone 16 live verification built the read-only x64 context assembly against the
installed AutoCAD 2021 managed API and loaded its exact reviewed directory while
leaving `SECURELOAD=1`; the pre-existing empty `TRUSTEDPATHS` value was restored
immediately after `NETLOAD`. `AIENGDRAWINGCONTEXT` and an explicit three-object pick
set each exported three entities from
`workspace/autocad-live/ai-engineer-beta-0.16.0.dwt`. Both evidence records imported
locally as two reviewed devices and one `E-WIRE` route with no unknown objects,
`DBMOD=0`, `provider_egress_performed=false`, and no network/provider call. The source
SHA-256 remained
`11fddeb136d129b2f6c38312c9780e2080519bb393edcf03e9528bf9fb1c4682` before and
after both exports.

Two fail-closed retries exposed environment and parsing issues before that success.
The Codex sandbox service account could not use the interactive user's AutoCAD
profile and correctly failed with `Problem with setting up current profile`.
Execution under the interactive user resolved that boundary. Core Console also
echoes AutoLISP expression return values, so the fixed script now uses one `progn`
expression ending in a silent `princ`; the marker parser accepts only complete marker
lines. Unit tests cover the observed echoed-output form.

The rejected `/isolate` alternative remains absent because it attempts to create
Registry state and copy installation data. AI-Engineer does not create, reset,
export, or import AutoCAD profiles. A packaged installation must be run by the same
Windows user who initialized AutoCAD 2021.

On the same date, the inventory pilot completed against the copied empty template in
about 0.66 seconds. It returned one layer and zero database entities, `DBMOD=0`,
unchanged source and scratch hashes, and no truncated output. This validates aggregate
counting only; it is not yet electrical semantic analysis.

The fixed writer pilot then completed in about 2.10 seconds. It created a line from
`(0,0,0)` to `(1000,0,0)` in the scratch DWT, increased the entity count from zero to
one, returned type `LINE` and `DBMOD=0`, preserved the source hash, changed the output
hash, and produced no truncated output. The resulting drawing remains an isolated
artifact and is not automatically promoted over any workspace drawing.

The electrical route pilot completed in about 2.15 seconds. It created two `LINE`
entities through `(0,0,0)`, `(1000,0,0)`, and `(1000,500,0)` on `E-WIRE` with ACI
color 3 and `CONTINUOUS`. Entity count changed from zero to two, `DBMOD=0`, the source
hash remained unchanged, the scratch hash changed, and neither stream was truncated.

The symbol pilot completed in about 2.17 seconds. It created
`AIENG_E_SOCKET_SINGLE_V1` with three internal entities and placed one `INSERT` at
`(2000,1000,0)` on `E-POWR-DEVC`. Model-space entity count changed from zero to one,
the attribute count was zero, `DBMOD=0`, the source hash stayed unchanged, the scratch
hash changed, and neither stream was truncated.

The intake pilot first placed the same symbol in Autodesk's millimeter `acadiso.dwt`,
then reviewed the resulting DWT through `/readonly`. It found the regular block, base
point `(0,0,0)`, one circle, two lines, all three internal entities on layer 0, and no
attributes, nested blocks, unsupported entities, or truncation. Review completed in
about 0.68 seconds with `eligible=true`, `DBMOD=0`, identical source/scratch hashes,
and a complete proposed definition hash.
## Milestone 21 supported panel/SLD markers

The initial existing-drawing extractor recognizes only four explicit block names:
`AIENG_M21_PANEL_V1`, `AIENG_M21_WAY_V1`, `AIENG_M21_SLD_NODE_V1`, and
`AIENG_M21_SLD_EDGE_V1`. Each requires its exact documented API/profile attributes;
way-marker load bindings and name columns use `|` as the bounded separator and must have
equal lengths. Each `LOAD_BINDINGS` value is an atomic
`load-id@AutoCAD-handle` pair whose handle must resolve to a recognized
device in the exact context, so device mapping does not depend on selection order. Empty
load columns are valid only for an explicit `spare` way.

Block-name matching is case-insensitive, while semantic IDs remain bounded lowercase
identifiers. Generic blocks, nearby text, and geometric crossings are not treated as
semantic connections. Malformed or truncated contexts produce `UNKNOWN`; duplicate or
inconsistent identities/topology produce `CONFLICT`. Extraction retains source handles
and performs no save, provider egress, or drawing mutation. Extraction profile v6 records
the stable-handle rule. Producer, independent inventory, complete-result palette, and
fail-closed visible negative-path acceptance passed on 2026-08-12.

The `Panels / SLD` palette tab calls the authenticated extraction route using only the
last explicit context identity and evidence hash plus a bounded reviewer and assumption.
It displays findings when extraction is incomplete and refuses any partial semantic model
or schedule. Complete results show ordered way, circuit, kind, phase, and reviewed
protective-device rows after exact contract-hash validation. The tab has no write command;
its DLL build, automated coverage, and disposable-fixture live acceptance pass.

The fixed fixture producer always names its isolated output `source.dwg`; a reviewed DWT
is byte-copied only into that run-local DWG before Core Console opens it, while the DWT
source remains unchanged. Autodesk documents DWT as the template form used to create a
new drawing and as the same native drawing format distinguished by extension. The producer
then creates exactly four new marker definitions, ten marker
references, 58 attribute references, and one reviewed `socket_single` load reference.
It re-reads and verifies every expected tag/value, including that the load-way attribute
contains the reference's actual AutoCAD handle, that top-level entity count grows by
exactly 11, and that `DBMOD=0` after one save. Existing
marker-name collisions, non-millimeter drawings, hash drift, incomplete markers, count
drift, or source/output hash violations fail closed. Promotion or source replacement is
never automatic and requires the existing separate verified-handoff approval.
The producer follows Autodesk's documented `entmake` complex-entity contract: group 66
on an `INSERT` declares following `ATTRIB` entities, terminated by `SEQEND`; every
`entmake` result is accepted only through the later exact reference/content/count checks.
See [Autodesk entmake reference](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-AutoLISP-Reference/files/GUID-D47983BA-1E5D-417D-85B8-6F3DE5F506BA.htm)
and [complex entities guidance](https://help.autodesk.com/cloudhelp/2025/ENU/AutoCAD-AutoLISP/files/GUID-7DA0E2BA-963F-4E41-AF33-C6B9D89D4688.htm).
