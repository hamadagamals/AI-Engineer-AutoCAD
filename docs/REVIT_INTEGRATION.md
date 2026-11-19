# Revit 2024 read-only integration

## School lighting review V2

The bridge also provides the separately bounded action
`execute_school_lighting_review_v2_save_as`. Unlike the first whole-school overlay, it derives each
candidate from the exact Revit room boundary, checks the point with `Room.IsPointInRoom`, applies a
450 mm room-boundary clearance, and excludes detected stair runs, landings, and shaft openings with
a 500 mm clearance. It refuses partial placement, duplicate XY points, points outside their room,
obstacle hits, a changed source/preview, or an existing output file. The output remains detail-curve
review geometry rather than real lighting families or circuits.

Live Revit 2024 execution on 2026-10-30 created
`School-Electrical-Lighting-Review-V2-20261030.rvt` from the unchanged source at SHA-256
`2F4E3543...73F582`. All 1,152 scheduled lighting points were placed across 108 rooms and three
review views using 4,608 detail curves. The handoff reports zero exact duplicates, zero points
outside room boundaries, and zero detected stair/shaft obstacle hits. Direct visual inspection of
the Level 1 and Level 2 stair zones showed no review symbols on the stair flights or landings. The
output SHA-256 recorded by Revit is `DE509ED5...FBA08`. Engineer room-by-room visual approval is
still required before converting any review mark to a real MEP family.

## School distribution review views

The bridge now has one project-specific, hash-bound review action named
`execute_school_distribution_review_save_as`. It reads an exact reviewed point schedule, duplicates
or creates one plan view per bound Revit level, draws view-specific color-coded detail symbols, and
saves only to a new workspace RVT. The source model hash, preview file hash, preview identity,
output path, and explicit mutation/save flags are required before the bridge accepts the request.
The action refuses an existing output file, an expired preview, unknown point kinds, duplicate point
identities, changed source evidence, or more than 1,500 points. Its output is a visual review overlay,
not electrical family placement, circuit creation, cable routing, or code approval.

Live school execution on 2026-10-30 created three review plans from 1,400 bounded points and 6,852
detail curves in a new RVT. The operator subsequently rejected its bounding-box lighting layout
because points appeared on stairs, some rooms were incomplete, and the symbol presentation looked
overlapped. That V1 file is retained only as superseded diagnostic evidence. Its power and panel
guides have not been carried into or approved by the lighting-only V2 correction.

## Supported boundary

The first AI-Engineer Revit connector is a local, read-only foundation for Autodesk
Revit 2024. It uses the installed official `RevitAPI.dll` and `RevitAPIUI.dll` through
an `IExternalApplication` plus one fixed `IExternalCommand`.

The application adds an **AI-Engineer** panel with a **Read-Only Inventory** button. The
command is declared with `TransactionMode.ReadOnly`; it does not start a transaction,
save a model, export geometry, call an AI provider, or mutate the active document.

The bounded inventory currently records:

- Revit version, document title, family/workshared flags, and active-view name/type;
- walls, floors, ceilings, doors, windows, rooms, and MEP spaces;
- lighting fixtures/devices, electrical fixtures/equipment, conduit, and cable tray;
- sheets, schedules, loaded families, and electrical systems;
- explicit no-egress and no-mutation flags plus limitations.

Linked-model contents, element identities, geometry, parameters, circuit topology, and
all write operations are outside this first gate.

## Build and install

```powershell
python launcher.py revit-readiness
.\integrations\revit_readonly_plugin\build.ps1 -WorkspaceRoot $PWD -RevitYear 2024 -Install
```

The installed files are user-scoped:

- `%LOCALAPPDATA%\AI-Engineer\revit\2024\bridge\AIEngineer.Revit.ReadOnly.dll`
- `%APPDATA%\Autodesk\Revit\Addins\2024\AIEngineer.Revit.addin`

Startup and inventory evidence is also user-scoped:

- `%LOCALAPPDATA%\AI-Engineer\revit\startup.json`
- `%LOCALAPPDATA%\AI-Engineer\revit\latest-inventory.json`
- `%LOCALAPPDATA%\AI-Engineer\revit\runs\inventory-*.json`

AI-Engineer validates inventory size, schema, fixed command identity, count types,
configured-root containment, and the no-egress/no-mutation assertions before accepting it.

## Live acceptance

1. Start licensed Revit 2024 and confirm `startup.json` reports `startup_status=ready`.
2. Open a disposable or backed-up sample RVT.
3. Press **Add-Ins > AI-Engineer > Read-Only Inventory**.
4. Load `latest-inventory.json` through `integrations.revit_inventory`.
5. Compare selected category counts independently in Revit.
6. Close without saving and verify the source RVT hash is unchanged.

On 2026-09-26 the operator resolved the Autodesk license outside AI-Engineer. Licensed
Revit 2024 then reached its Home workspace, loaded the add-in, wrote
`startup_status=ready`, and registered the **AI-Engineer / Read-Only Inventory** button.
The add-in also supports an explicit process-only
`AI_ENGINEER_REVIT_READONLY_AUTOCAPTURE=1` acceptance switch. It performs the same fixed
inventory once after a document becomes active; it is not a general automation or write
surface.

The live model-read pipeline passed on Autodesk's installed
`Snowdon Towers Sample Electrical.rvt`. Revit reported 588 lighting fixtures, 196
lighting devices, 538 electrical fixtures, 200 electrical-equipment instances, 530
conduits, 80 MEP spaces, 26 sheets, 39 schedules, and 448 electrical systems. The strict
Python loader accepted the 977-byte schema-v1 result with no egress and no mutation.
Revit closed without saving, and the source SHA-256 remained
`9015E932EB03B2D482A87B13D90D79CE354C4112337EEE51B218B03504CBEB30`.

The first live run exposed that the default .NET serializer represented `counts` as
`Key`/`Value` pairs. The add-in now enables `UseSimpleDictionaryFormat`; the corrected
live result passed the strict loader and regression tests. Because manual schedule
counting was not practical for the operator, every category is now counted again with an
independent `ElementCategoryFilter` collector. Any disagreement fails the inventory.

That dual-count gate passed on the 6.46 MiB Autodesk `DynamoSample_2024.rvt` review copy:
38 walls, 6 floors, 2 ceilings, 5 doors, 7 windows, 14 rooms, 56 families, 5 sheets, and
7 schedules matched across both collectors. The strict loader accepted the result, Revit
closed without saving, and the review-copy SHA-256 remained
`B9D1DB386AEEF1C6EFD9EA3010DD0411E8777E2ACFD704746FC3F837691FC5E4`.
The bounded inventory connector is live accepted; selection, parameters, geometry,
circuits, linked-model expansion, and all write operations remain separate future gates.

## Chat-driven local request bridge

AI-Engineer can now submit one correlated `inventory` action from the local launcher
without requiring an in-product button press:

```powershell
python launcher.py revit-read "Exact active document title" --timeout 30
```

The Python side atomically creates a schema-v1 request with a UUID, exact document-title
binding, and explicit false egress/mutation permissions. A user-scoped
`FileSystemWatcher` raises Revit's supported `ExternalEvent`; the Revit thread accepts
only the fixed `inventory` action, rejects title mismatch or any permission escalation,
and writes a correlated bounded response. The launcher then reloads the normal strict
inventory contract.

Live acceptance passed on 2026-09-27 against the open Dynamo review copy. Request
`939465da-c92f-4ec3-af93-8b0435e108e7` completed during the same Revit session, returned
the expected dual-verified counts, reported no egress or mutation, and required no Revit
button press. Revit closed without saving and the review-copy SHA-256 remained
`B9D1DB386AEEF1C6EFD9EA3010DD0411E8777E2ACFD704746FC3F837691FC5E4`.
Only inventory is allowlisted. Free-form commands, parameter/geometry extraction,
previews, transactions, saving, and drawing remain disabled.

The allowlist now also includes `category_query` for 13 fixed architectural/electrical
categories. A request returns at most 32 top-level instances with document-local element
ID, category, name, type, family, level, and an optional axis-aligned bounding box in
millimetres. Unknown categories and limits outside 1..32 fail closed. Live acceptance
request `77615722-3d76-4413-b3f2-8302fb422275` returned three of 38 walls with levels,
families, types, and millimetre bounds during the same session. The strict loader passed,
no egress or mutation occurred, Revit closed without saving, and the model hash remained
unchanged.

Category results now include only nine allowlisted displayed parameters when present:
mark, type mark, manufacturer, model, voltage, wattage/true load, apparent load, circuit
number, and panel. Missing parameters are omitted. Live electrical acceptance returned
three panelboards from Autodesk's Snowdon model with marks, circuit numbers, upstream
panels, types, and millimetre bounds. Revit closed without saving and the source hash
remained unchanged.

A separate non-mutating placement-preview module now produces self-hashed, expiring JSON
bound to an exact workspace RVT SHA-256, document title, family, type, level, and 1..32
millimetre points. It always records `review_required`, `execution_allowed=false`, and
no mutation. Tampering and source mismatch fail closed. Family placement mode and required
wall/ceiling/face host must still be resolved for the requested family before any Revit
family-placement transaction is designed. Circuits, full geometry, and saving remain
disabled.

## First approved transaction

The first write gate is limited to 1..16 horizontal Model Lines from an exact, expiring,
self-hashed preview. Before submission, Python verifies the workspace source hash,
creates and verifies a timestamped RVT backup, copies the approved preview into the
local bridge, and submits a UUID-correlated request with `save_allowed=false`. Revit
independently verifies the active-document and preview-file hashes, creates all lines
inside one supported transaction, and reports the resulting element IDs.

Live request `c81cb40d-bf2e-45d7-8b4b-9d4616823a49` executed approved preview
`F9F06DE76035DC846FCDADDF9497E9F17A493B01B2B8F788A357FD8EB7743CB0`.
It created exactly four Model Lines with element IDs `1058904`, `1058906`, `1058908`,
and `1058910`. The transaction committed, `saved=false`, and provider egress was false.
Revit closed without saving; the workspace source and backup retained SHA-256
`B9D1DB386AEEF1C6EFD9EA3010DD0411E8777E2ACFD704746FC3F837691FC5E4`.
No direct save, source-overwrite, family-placement, or arbitrary-command path exists.

## Loaded family types and placement preview

`revit-read-family-types` is a read-only bridge action for lighting fixtures, lighting
devices, electrical fixtures, and electrical equipment. It returns at most 64 loaded
types per request with family/type IDs, placement type, conservative host requirement,
and activation state. The live Snowdon review-copy inventory identified the under-cabinet
downlight type `symbol_id=1456719` with OneLevelBased placement, requiring only a level.

Review artifact `031A293EDEC95720C505CE6CB293822724CEA330977E83FDF16DCC59A4B594CB`
binds level L2 / `level_id=9946` and one 1000 mm horizontally offset point. It is
non-mutating, expires, and has `execution_allowed=false`; no family placement can occur
until a separate transaction and new-file handoff contract are reviewed and approved.

## Single-family new-file handoff

The live-accepted bridge path accepts only one unexpired, self-hashed `OneLevelBased`
preview on its exact level. It additionally binds a real reference instance and explicit
`Elevation from Level`; bounding-box centres are never treated as family origins. Before
Revit receives the request, Python verifies the source hash, makes and checks a timestamped
backup, and requires a nonexistent output RVT below `workspace`. Revit revalidates the
family, type, level, reference instance, physical Z, and elevation. It creates one instance
inside one transaction and checks its physical point before commit, so mismatch rolls back
without saving. A second read-only request checks the reopened output hash, instance ID,
type, level, and point.

Approved preview `09240345...2C59098` created element `1735582` in
`Snowdon-Towers-Electrical-family-placement-reference-verified.rvt`; output SHA-256 is
`6F19E8EA1845F7A8E71A6BA5797493C09251C796A6B4A64F62A13750ECEE167C`.
Reopened verification request `776998da-ecc7-44a4-970f-b4874b6cb15e` matched the exact
preview with no mutation or provider egress. Source and backup both retained SHA-256
`9015E932...04CBEB30`. Direct save, source overwrite, hosted families, multi-instance
placement, and arbitrary commands remain rejected. Two preceding files with incorrect Z
remain rejected diagnostic evidence and are not accepted deliverables.

## Bounded same-type family batch

The live-accepted extension expands only the reference-bound `OneLevelBased` contract to
1..32 instances of the same family type, level, and explicit elevation. The preview must
contain ordered finite millimetre points at one physical Z. Revit creates the complete batch
inside one transaction and rolls it back if any instance has the wrong symbol, level,
`LocationPoint`, or `Elevation from Level`. The Python result gate cross-checks the reported
count against the approved preview, rejects duplicate/invalid element IDs, and the reopened
verification must return the exact created IDs and output hash.

Focused Python tests and the Revit 2024 DLL build pass. Approved preview
`64D00C9F...77089B74` created three instances with IDs `1735582`, `1735583`, and `1735584`
in one transaction and saved only
`Snowdon-Towers-Electrical-family-placement-three-instance-batch.rvt`. The output SHA-256 is
`09070A9D...A0A8475`; source and verified backup retain `9015E932...04CBEB30`. Reopened
read-only request `5cad5855-d2a5-4870-9a14-54d93297fd99` matched all three exact IDs,
points, symbols, levels, elevations, and the output hash with no mutation or provider egress.
This establishes exact transaction and reopened-file acceptance. The operator then isolated
the three selected fixtures in the L2 Lighting plan; the supplied screenshot visibly showed
three aligned, equally spaced symbols and Properties reported `Lighting Fixtures (3)` with the
approved `Downlight - Under Cabinet / 18\" 120V` type. This confirms visibility and spacing,
not room association, clearance, photometric suitability, or code compliance.
Rotation, mixed family types or levels, hosted families, parameter edits, and circuits remain
rejected.

## Per-instance rotation

New previews explicitly record one normalized `rotation_degrees` value in the range
`0 <= angle < 360` for every placement. Older schema-v1 previews remain verifiable only as
zero-degree placements. Revit rotates each new instance about its actual vertical
`LocationPoint` axis inside the same all-or-nothing transaction, then compares the resulting
`LocationPoint.Rotation` within 0.001 degrees. Reopened verification repeats the exact angle
check. Invalid, missing, non-finite, negative, or 360-degree values fail closed.

Twenty focused tests and compilation against Revit 2024 pass. Separately approved preview
`DAFEA9FE...D41090CE` created elements `1735582`, `1735583`, and `1735584` at `0`, `45`,
and `90` degrees in one transaction. Output
`Snowdon-Towers-Electrical-family-placement-three-instance-rotated.rvt` has SHA-256
`C5AFEA4A...09D02187`; source and backup retained `9015E932...04CBEB30`. Reopened request
`ed35d9ec-b919-4b3f-b443-83dfea3c7514` matched all IDs, points, levels, elevations, and
angles without mutation or provider egress. The operator's isolated L2 screenshot visibly
confirmed one horizontal, one 45-degree diagonal, and one vertical symbol.

## Read-only host identity candidate

The bounded category query now reports optional family-instance host metadata: local host or
Revit-link instance ID, locale-independent category key and display name, `HostFace` stable
representation, linked element ID, linked document title, and linked category/name. It also
supports bounded `hosted` and `ceiling_hosted` filters; the latter compares the Revit
`OST_Ceilings` identity rather than localized text. A one-second `Idling` recovery check prevents
a missed filesystem notification from leaving `pending.json` dormant. The strict loader rejects
inconsistent, incomplete, or non-ceiling results.

Live Snowdon query `194fa83e-0e09-4628-8229-86622aaf5cae` found exactly four ceiling-hosted
fixtures. Candidate `1622255` is symbol `1453791`, family `Recessed Lamp - Round - LED`, type
`4\" Trimmed Downlight`, on link instance `1362762` and architectural ceiling `1566775`
(`5/8\" GWB on Mtl. Stud`). The family-type query independently reports `WorkPlaneBased` /
`work_plane_or_face`. Source SHA-256 remained `9015E932...04CBEB30`; no document mutation or
provider egress occurred. This is still read-only discovery: hosted placement remains disabled
until the exact face identity is added to a review-only preview and separately approved.

Preview `E1EC36674B2E988DC3437359A27FB67236E569458F1C4233D74033DD69659B55`
(`workspace/revit-test/previews/Snowdon-ceiling-hosted-reference-preview.json`) now binds the
exact link instance, architectural ceiling, stable face reference, symbol, exemplar fixture, and
observed point. Its status is `review_required`, `execution_allowed=false`, and it cannot enter
the existing level-based handoff. The point intentionally reproduces the observed exemplar only
to verify the identity contract; it is not an approved new fixture location. A bounded face-
geometry read must establish a distinct in-face target before a separate hosted execution gate.

The bounded `revit-read-host-face` action now performs that read without opening a transaction.
It verifies the exact reference fixture, stable linked-face identity, loaded linked document,
`OST_Ceilings` element, planar face, bounded triangulation, and in-face projected candidates. Live
request `a109ccf2-b464-4fe6-8aee-8ef32c14ae56` returned a 4,789,345.26 mm2 face represented by
two triangles and four vertices. Its leading candidate is
`(13592.175, 11213.042, 2743.2)` mm with downward face normal and 366.486 mm centre clearance
from the nearest fixture sharing that face. Review-only preview
`DC5AE2D0EE725443E540CFAB1E8C2DCB66C03ABF9FFA4886D289FEAB998525C5` records that point and
exact host binding with `execution_allowed=false`. The geometric clearance is not a photometric,
constructability, or code-compliance approval. At that checkpoint hosted execution was unavailable;
the separately gated implementation below still requires exact approval and live acceptance.

## Single linked-ceiling family handoff

The separate hosted path is live accepted. It permits exactly one
`WorkPlaneBased` instance, zero rotation, one exact linked `OST_Ceilings` planar face, and a new
workspace RVT. Immediately before mutation Revit re-resolves the exemplar, symbol, link, linked
document, ceiling element, stable face, and proposed point. It also requires at least 250 mm
centre-to-centre from every existing lighting fixture on that exact face. This is an integration
anti-overlap gate, not a code, photometric, maintenance, or constructability clearance.

Creation occurs in one rollback-capable transaction. The created symbol, `LocationPoint`, and
`HostFace` must match before commit; Save As refuses overwrite. A separate read-only action checks
the output hash and exact created element after reopening. Fresh preview
`37FA4716D96B20C8D2687C8CCC4BBA1FCEBDDD4C6FB8F43C3AF953EF99CFDC8E` binds the candidate point
and exact face and expires at `2026-10-10T20:38:53.207654Z`. It remains non-executable until the
operator explicitly approves that exact hash.

The operator explicitly approved that hash. Live request
`465adfae-6b4f-4466-ab1e-fff5f7efdfa7` created exactly one instance, element `1735503`, and saved
`workspace/revit-test/outputs/Snowdon-Towers-Electrical-hosted-ceiling-placement.rvt` with
SHA-256 `B9D6A6101727ECF4D486AE8812942217BC4AE421F7593F4DF8AA9C61FA296D30`.
The verified source backup retains SHA-256 `9015E932...04CBEB30`. After reopening the output,
read-only request `76d5e38b-a197-4fe2-9866-4d66effda3f5` matched the output hash, created ID,
symbol, point, and exact linked host face with no mutation or provider egress. A subsequent exact
visual query compared created element `1735503` with exemplar `1622255`: family/type, linked host,
plan bounds, four visible geometry objects, orientation, flip/mirror state, category visibility,
matching view filters, and element graphics overrides agree. The first blank/point image was traced
to Revit exporting before its first UI redraw; `revit-focus-element` now performs two separate
read-only `ExternalEvent` passes and live evidence `f7b364fa-53fd-4557-88e6-f0d22aad96c0`
renders the complete symbol. The created instance reports zero offset from its linked host face;
its physical `LocationPoint.Z` remains `2743.2 mm`. The user's saved-view copy is preserved at
SHA-256 `1C3907037BCEAD138BA648289B03FBCB4FA6AD47678732D32E345806060BCCB8` and was not overwritten.
This placement does not establish photometric or code compliance.

## Lighting fixture circuit relationships

`revit-read-lighting-circuits` is a separate read-only gate. It uses the supported
`FamilyInstance.MEPModel.GetElectricalSystems()` relationship and does not treat the
displayed `Panel` or `Circuit Number` parameter text as proof of membership. Results
are capped at 32 fixtures and explicitly report truncation. Each actual system retains
its Revit element ID, type, circuit number, panel, load name, volts, apparent load,
true load, power factor, and poles.

Readiness `v28` includes deterministic continuation for projects larger than one page. Use
`--start-after-element-id` only with an all-fixtures request (`--element-id 0`). Every page
reports its starting identity and either the exact last returned element as the next cursor or
zero on the final page. The strict loader rejects unsorted, duplicate, non-advancing, or final
pages that still advertise a continuation. Live acceptance completed all 19 pages for 589 fixtures
without increasing the 32-fixture request bound. The resulting source-bound inventory contains
563 connected fixtures, 26 unassigned fixtures, 113 unique circuits, 31 unique panels, and 22
family/type rows. The ten-sheet workbook
`workspace/revit-test/outputs/Snowdon-lighting-project-schedules-and-quantities.xlsx` presents a
Panel Schedule, panel inventory, lighting BOQ, modelled cable quantities, and visibly green
engineer-input cells. Separate read-only category chains also capture 538 electrical fixtures,
196 lighting devices, 200 equipment instances, 530 conduit segments, 322 conduit fittings, and
zero cable-tray elements. Supported curve geometry totals 1,730.348 m of modelled conduit; 16
wiring pull boxes are retained in the equipment BOQ. Linked architectural host metadata remains
unresolved where the link document is unavailable. Modelled circuit/conduit length remains distinct from verified installed route,
allowances, slack, waste, parallel runs, and code-approved conductor/protection selection.

## Direct electrical-system inventory

`revit-read-electrical-systems` queries supported Revit `ElectricalSystem` elements directly. It
does not require a lighting fixture to discover a circuit and does not infer circuit membership
from labels or geometry. Each request is capped at 32 systems, uses a strictly advancing element-ID
cursor, and reports no document mutation or provider egress. The strict Python loader rejects
unsorted, duplicate, non-advancing, count-inconsistent, or invalid final pages.

The installed readiness `v29` bridge completed 14 live pages against the approved Snowdon output.
The resulting self-hashed inventory contains all 448 power circuits across 54 panels: 113 lighting,
264 receptacle, 67 feeder/panel, and four spare/empty circuits. It retains Revit circuit, panel,
wire, phase-current, protection-parameter, load-classification, and bounded member-category
evidence. Source SHA-256 remains `A5B3AC709AD20D3C6B965680EE16A813649944510A03863541E0B656F16031CE`.

`workspace/revit-test/outputs/Snowdon-complete-panel-schedules.xlsx` presents one schedule per
panel in the operator's supplied odd/even form. It treats each of the 138 multi-pole systems as one
circuit and distributes its single Revit apparent-load value over R/Y/B using observed phase-current
shares, with an equal fallback only when phase-current evidence is absent. Every panel allocation
reconciles to its source circuit sum. Excel native recalculation reports 1,566 formulas and no
formula errors. Green cells remain required engineer inputs. The aggregate 2,093,743.732737 VA
includes upstream feeder and downstream loads together and therefore must not be used as a
non-duplicated project demand total.

Live exact-element acceptance proved exemplar `1622255` belongs to power circuit
`1622590` (`P109 / 5`, `120 V`, `80 VA`, `76 W`, PF `0.95`, one pole) while created
fixture `1735503` has zero electrical systems. The broader file contains 589 lighting
fixtures; a bounded 32-fixture sample returned 32 connected fixtures and
`truncated=true`. No model mutation or provider egress occurred.

The query now also reads the supported MEP connector manager. Exact live requests
`a463baa3-d1ab-465e-a9ca-72eed5e55096` and
`a6b21b5c-f080-46c0-97d6-cba098c42e7e` found one matching connector on created fixture
`1735503` and reference fixture `1622255`: connector `1`, `DomainElectrical`, `End`, and
`PowerCircuit`. The created fixture remains unassigned; target membership is established only
through the reference fixture's actual Revit electrical-system relationship.

`revit-preview-circuit-assignment` produced non-executable preview
`FE9DB83D03A740AB6111769DE50C10B0031119AB915A27E3C5FBBE6EAA7F3AEF` for adding fixture
`1735503` to system `1622590` (`P109 / 5`). It is bound to source SHA-256
`1C3907037BCEAD138BA648289B03FBCB4FA6AD47678732D32E345806060BCCB8`, expires after
24 hours, and records `execution_allowed=false`. Instance voltage and apparent-load parameters
were unavailable; spare capacity, demand, protection, and phase impact are not reviewed.
At that checkpoint, connecting the fixture remained unavailable pending exact-preview approval,
a separate one-transaction new-file implementation, reopened verification, and recovery evidence.

The operator then explicitly approved that exact preview while accepting the recorded unresolved
engineering reviews. The separate handoff uses Revit 2024's supported
`ElectricalSystem.AddToCircuit(ElementSet)` API for exactly one element. Request
`6c08de07-c84d-441b-a61d-9f4d50fe71ab` revalidated the live fixture, reference, system, family,
type, connector signature, source hash, and empty fixture membership, then committed one
transaction and saved only
`workspace/revit-test/outputs/Snowdon-Towers-Electrical-hosted-ceiling-circuit-P109-05.rvt`.
Its SHA-256 is `A5B3AC709AD20D3C6B965680EE16A813649944510A03863541E0B656F16031CE`.

After closing and reopening that output, read-only verification
`119f68bf-3956-4cae-8095-b6f8733ca399` matched the exact preview and actual fixture membership.
Independent query `1ad807e3-1127-403f-81c9-f8b33b5fb64e` returned fixture `1735503` with exactly
one system: `1622590`, `P109 / 5`, `120 V`, `100 VA`, `95 W`, PF `0.95`, one pole. Compared with
the pre-assignment target reading, the observed circuit change is `+20 VA / +19 W`. Revit closed
without further saving; source and backup both retain SHA-256 `1C390703...BCCB8`. These values
prove model membership and observed load change only. Circuit capacity, demand, protection,
phase balance, and code compliance remain outside this acceptance.

## Circuit engineering evidence review

The read-only circuit result now includes modelled circuit rating/frame, apparent/true current,
per-phase current, phase label, voltage drop, wire description, conductor counts, and supplying
panel data. Live request `fc117125-d5c8-422e-957d-61f063360857` reported `P109 / 5` at
`120 V`, `20 A`, `100 VA`, `95 W`, `0.833333 A` apparent current, phase `C`, and a modelled
`1.473381%` voltage drop. Revit describes one hot, one neutral, and one ground as
`1-#12, 1-#12, 1-#12`. Panel element `1478846` is modelled as a `225 A`, 3-phase/4-wire
panel supplied from `MB13`, with phase currents `85.491306`, `62.241306`, and `57.097760 A`.

Deterministic review
`workspace/revit-test/reports/Snowdon-P109-05-engineering-review.json` is bound to output
SHA-256 `A5B3AC70...16031CE`, preview `FE9DB83D...A7F3AEF`, and that exact live query.
Its self-hash is `369C8EC5AACAC6BF1199F8790476B580CDD93A4B5F7DF3F679FA56E6D32B615D`.
It calculates `4.166665%` branch-rating utilization, `19.166667 A` branch rating headroom,
`30.119672%` panel demand/MCB utilization, and `157.230737 A` modelled panel headroom. The
three-phase maximum-deviation/average indicator changes from an estimated `25.314802%` before
the added load to `25.212836%` after it, an improvement of `0.101966` percentage points.
These are arithmetic observations from Revit values, not approval. Cable material/insulation,
installation and derating, breaker curve/interrupting rating, fault current/selectivity, governing
Egyptian code edition, project voltage-drop criterion, and applicability of the model's 120/208 V
system remain unresolved.

The next strict read-only extension adds circuit length/runs/ways/notes, exact Revit `WireType`
properties, and panel short-circuit-rating text. Live request
`e6cbd42d-870c-4d4b-b701-c07e91ac3736` reported `12988.925 mm`, one run and one way, no circuit
notes, and WireType element `83912`: Copper `THWN`, `60` temperature-rating text,
`Non-Magnetic` conduit, with a required neutral sized to the hot conductor. The panel's
short-circuit-rating field is empty. Revit also reports a `2000` maximum wire size with `560 A`
ampacity for the type definition; this is not the selected `#12` conductor's derated ampacity and
must not be used as one.

Deterministic review
`workspace/revit-test/reports/Snowdon-P109-05-engineering-review-wire-evidence.json` is bound to
the same output and approved preview plus the exact live request. Its self-hash is
`F0EE0DACA2155A9BBCCAAB359B5AA04A2D692DC65D24D19F4EB76957A172414F`. It retains installation
method, ambient/grouping corrections, selected-conductor derated ampacity, breaker curve, panel
fault rating, fault current/selectivity, pinned code/project criteria, phase-current semantics,
and 120/208 V project applicability as unresolved. Revit closed without saving and the output
SHA-256 remained `A5B3AC709AD20D3C6B965680EE16A813649944510A03863541E0B656F16031CE`.

The protection-evidence extension reads only parameter definitions whose names match a bounded
protection vocabulary, from the exact circuit/panel instance and type. Each result is capped at 32
entries and retains source, parameter ID, storage type, and Revit's displayed value. Live request
`6441f18d-59c5-4dfe-98e4-db9716ceb075` found circuit `Rating = 20 A` (ID `-1140038`) and
`Frame = 400 A` (ID `-1140154`), plus panel `MCB Rating = 225 A` (ID `-1140140`) and
`Max Number of Single Pole Breakers = 30` (ID `-1140079`). No curve, trip, fault-current,
interrupting-capacity/AIC, fuse, or selectivity parameter was returned. The `400 A` frame display
is preserved as model evidence only and is not fault duty or proof of protection adequacy.

The corresponding report is
`workspace/revit-test/reports/Snowdon-P109-05-engineering-review-protection-evidence.json`,
self-hash `F7CFA4BB5AE7FF6E4C0FFA08E12ACF166AFDECF348314B151143A5042FE4CF83`.
It remains `engineering_review_required`; absence from this bounded snapshot is not proof that an
external study, specification, or manufacturer record does not exist.

## Circuit calculation intake

`revit-build-circuit-calculation-intake` turns one validated engineering review into a new atomic,
self-hashed inventory of calculation inputs. It never supplies typical values. Revit observations
are marked `model_observation_requires_engineer_confirmation`; absent engineering values are
`missing`, retain their expected units and evidence descriptions, and keep all calculation-ready
flags false.

For `P109 / 5`,
`workspace/revit-test/reports/Snowdon-P109-05-calculation-intake.json` is bound to engineering
review `F7CFA4BB...CF83`, query `6441f18d...b075`, approved preview `FE9DB83D...A7F3AEF`, and RVT
`A5B3AC70...16031CE`. Its self-hash is
`AA0C22269C0AD96A2396067B2D69AB6C2EA8C0693093A2FBC1220767D3C04198`. The five model observations
are voltage, circuit rating, conductor display, material, and insulation. The 25 missing reviewed
inputs cover code/project criteria, installation and derating, fault impedance, exact breaker
evidence, selectivity, and responsible review. No ampacity, short-circuit, coordination, or
compliance result is produced from incomplete inputs.

## Upstream feeder evidence

The circuit query follows a single bounded upstream step for the supplying panel. It accepts no
more than eight incoming systems and requires the panel to be present in each system's actual
`Elements` set while the upstream source is a different `BaseEquipment`. This prevents the
displayed `Supply From` text from being used as proof of connectivity.

Live request `dc67827d-c99a-4c25-9701-ddf2f913e175` proved `P109` is supplied by system
`1672726`, circuit `3`, from meter-bank element `1668955` (`MB13`, type `225A - 4 Socket`). Revit
reports `208 V`, `24,414.999967 VA`, `67.769263 A`, `20 A` rating, `400 A` frame display,
`26,129.17061 mm`, wire display `3-#4, 1-#4, 1-#4`, and `45.438594%` modelled voltage drop.
The upstream short-circuit-rating field and protection parameters are empty.

Review `workspace/revit-test/reports/Snowdon-P109-05-engineering-review-upstream-feeder.json`
has self-hash `D6CDC73EB37102D595E0F494D826900BB952FCB95217B9CDFC7450C702AC260A` and calculates
`338.846315%` rating utilization with `-47.769263 A` headroom. Updated intake
`workspace/revit-test/reports/Snowdon-P109-05-calculation-intake-upstream-feeder.json` has
self-hash `74CDE7846D3E53304DC6823036D02E780EDB1226596EE571573CFCA280FF3B9F`. Both fail closed.
The figures may expose stale or intentionally simplified sample-model data; they are not a code
finding, cable verdict, or permission to alter the model.

## New-file handoff

The next gate adds only `execute_model_lines_save_as` and
`verify_model_lines_handoff`. The first action requires a separately confirmed exact
preview hash, a verified source backup, and a nonexistent `.rvt` below `workspace`;
both Python and Revit reject overwrite and path escape. The second action is read-only
and, after the operator reopens the output, verifies its SHA-256, the four element IDs,
and each line endpoint against the approved preview. Direct save and source overwrite
remain unavailable.

The DLL builds against Revit 2024 and 14 focused tests pass. Explicitly approved preview
`56EF15B01BB239940BC7F3F4414598A6A4DE78C20975A9406D011C8C4E79A2AC`
created the new file
`workspace/revit-test/outputs/DynamoSample_2024-model-lines-handoff.rvt`.
The output SHA-256 is
`78DAEE6D3AE16D323FD8C3EC7123F1D41E014236D790FF48B5B3441C89D13625`.
Read-only reopened request `10a90880-90d4-4916-89a2-75adc45a29b1` verified element IDs
`1058904`, `1058906`, `1058908`, and `1058910`, exact preview geometry, no mutation,
and no provider egress. The source retained its original SHA-256.
