# Representative lighting-project acceptance — 2026-08-13

## Source and safety boundary

- Source: `workspace/lighting.dwg`
- Source size: `3323467` bytes
- Source SHA-256 before and after the read-only trials:
  `1d08ccb0557e740f7d471e006d5accad0675d54b82e45d9aabc64e239ecfb117`
- AutoCAD/Core Console: `24.0` / AutoCAD 2021
- Drawing units: millimeters (`INSUNITS=4`); metric measurement; Model space
- Provider egress: none
- Drawing execution/handoff: none

The source was never opened for write by AI-Engineer. Core Console used isolated read-only copies,
and the visible palette used a separate OS-read-only copy beneath `workspace/autocad-preview`.

## Aggregate read-only inspection

- Layers: `98`
- Database entities: `4848`
- Inspection duration: `2.137916 s`
- Inventory duration: `2.093898 s`
- Core Console `DBMOD=0`; source and both scratch copies remained byte-identical.

## Explicit-selection trials

The first selection reached the 512-entity limit and was intentionally rejected as a complete
semantic fixture. It exposed a real compatibility defect: valid mirrored block references can have
negative scale factors. Context parsing now accepts bounded finite non-zero scale components while
still rejecting zero; the negative sign is retained as observed mirroring.

The second selection is context `45e7eac907864543b9f233864c75aed9`, evidence SHA-256
`6696d15b5682ce0d105bef838e572b55af180383ff9d5e9bc941e89ba2e8f566`, 87,315 bytes:

- 102 selected entities on 7 layers: 38 MTEXT, 34 INSERT, 22 LWPOLYLINE, 4 unsupported HATCH,
  3 LINE, and 1 ARC.
- The selection did not exceed the entity limit, but long architectural polylines caused
  `maximum_points_per_entity`; therefore it remains incomplete.
- The visible AutoCAD copy reported `DBMOD=21`. On-disk source SHA-256 remained unchanged, but this
  does not satisfy the zero/presentation-only DBMOD acceptance requirement.
- Conservative model import produced zero approved devices/routes and 102 explicit unknowns:
  34 unregistered block names, 25 non-`E-WIRE` line/polyline objects, 39 unclassified observed
  geometries, and 4 unsupported hatches.
- Canonical imported-model SHA-256:
  `ee2807f7671a778ac26e79d2bc596219fb87a4a9049b78dcd1ff20615d08ca83`.

Observed candidate conventions include `E-Lite`, `E-Lite-Swch`, `E-Lite-Circ`, `E-Lite-Cnmb`, and
`E-Lite-Iden` prefixed by `BLOCK003-G$0$`; fixture, pendant, LED-tape, fan, and one-/two-way switch
block names; and circuit annotations such as `5/ DB3-GA2`. These are observations only. They do not
yet prove block function, circuit topology, phase, load, conductor, protection, or connectivity.

## Remaining acceptance work

1. Capture a complete electrical-only selection that excludes long architectural polylines.
2. Review exact project block definitions and create explicit project-scoped symbol mappings.
3. Review circuit/tag grammar and route-layer semantics; crossings must not create connections.
4. Determine why the visible read-only copy reports persistent DBMOD bits and obtain an acceptable
   unchanged-source capture.
5. Only after a complete recognized context, run takeoff/calculation preview and a separately
   approved copied-scratch execution/recovery trial.

## Complete electrical sample

The third explicit selection is complete and non-truncated:

- Context ID: `aa3e580be218476490529ed264226fad`
- Evidence SHA-256: `f1fb2c9ceb897aed2e08820ab3ab80dfcf4bf68a1bf63ef32f6c0c43c60f22a1`
- Evidence size: `7075` bytes
- 15 entities on 5 layers: 6 INSERT, 7 MTEXT, 1 three-point LWPOLYLINE, and 1 unsupported HATCH.
- Observed items include one mirrored `L05` pendant, three mirrored rectangular spot references,
  one mirrored one-gang/one-way switch, one mirrored two-gang/two-way switch, circuit text
  `4/ DB3-GA2`, control identifiers `a,b` and `c`, and a short object on the `E-Lite-Circ` layer.
- Import remains intentionally conservative: zero recognized devices/routes and 15 explicit
  unknowns until the block, route-layer, circuit-tag, and control-tag meanings are reviewed.
- The source remains SHA-256 identical. The visible read-only copy still reports `DBMOD=21`, so
  this is valid convention-discovery evidence but not yet a zero/presentation-only DBMOD pass.

The complete sample also proved that mirrored block scales occur on both fixtures and switches,
including `[-1, 1, 1]` and `[-1, 1, -1]`. Both context parsing and the shared device model now
preserve bounded finite non-zero signed scale values; zero remains rejected.

## Reviewed exact-context convention result

The engineer approved the observed meanings and clarified the ambiguous rectangular block as
`L02`. The resulting self-hashed profile is
`workspace/.ai-engineer/autocad/project-conventions/lighting-aa3e580b.json`:

- Profile SHA-256: `aa4485c38ab1f4748a2dcbc78faf60e605919503d4fc2552b1e766cea8b01c0d`
- Profile-file SHA-256: `db7e414fc768f65e0373e53d108a0d2bfc2cf36887fa13df3b9832ace70cb294`
- Exact bindings: document fingerprint, context ID, context-evidence SHA-256, and source-drawing
  SHA-256.
- Exact block/layer mappings: `L05` pendant, `L02` rectangular spot, one-gang/one-way switch,
  and two-gang/two-way switch.
- Route mapping: `E-Lite-Circ`, with `explicit_endpoints_only`; crossings do not connect.
- Tag grammars: circuit-number/panel reference and comma-separated control identifiers.

The normal context service automatically discovered the one exact matching profile and produced
six recognized devices, one route, panel `DB3-GA2`, and circuit `4`. The circuit intentionally has
no device membership. Six valid control tags remain unassociated, and the unsupported hatch remains
unknown. Canonical model SHA-256 is
`76feba860cfe4fb6b80fdd29e2032e1d8fb1c021c43fe8636cd68b3d6bbae88a`.

Multiple matching profiles, profile tampering, changed context evidence, a different document, zero
scale, unsupported tag grammar, and crossing-based connectivity all fail closed. No drawing or
provider action was performed.

## Live palette revalidation

A fresh read-only AutoCAD session produced complete context
`66e7c7c7e680493f93ff8ecf673db1c6`, evidence SHA-256
`0f1faeee5b0dc04851b41eb79e795f963a4e054e655e27d896f8e36c56af917d`, with 22 entities,
no truncation, and `DBMOD=16` (view/window state only). The source drawing SHA-256 remained exact.

The prior review was rebound only after automated checks proved the same source SHA-256 and document
fingerprint, no new block/layer pair, only approved route/tag layers and grammars, and a complete
context. Rebound profile SHA-256 is
`afe17a1e033d11a00d517a5fbccffb5fae7cc3e590c6ad242198a7b8b6a2baea`; profile-file SHA-256 is
`37c8e7e0a2de6bf5a55283bc85f3f456cef0440737af1349a5755ca2dcba7eb7`.

The normal authenticated API logged context import and local Arabic Copilot session creation with
`drawing_write_allowed=false`. The AutoCAD 2021 palette visually displayed:

- 7 devices
- 6 routes
- 8 unknown/missing
- 5 warnings
- local session ready

Screenshot SHA-256 is `af28a49d4689bb13a2d602bb4f9ab3b3a4ac4c8927597d049bb972f662617492`
(42,862 bytes). No AI-egress action was selected. Canonical imported-model SHA-256 is
`1bf372b22c30c1a40468ba4c40a3212e6d7ce939ef1ca3a05a3b53fd0a608dd4`.

## Topology-selection rejection

The next engineer selection was treated only as a proposed circuit-4 topology. Context
`06e6ddf1dbaf428787708589e5ddd838`, evidence SHA-256
`b6fec6a00d5749efb09b021ce3badcbe1139e6d66138d7ff40dd49c6d6c4f3d3`, was complete with
35 entities, but it was not approved or rebound:

- it introduced unreviewed `SPOT Toilet` and `Propeller Fan EF-02` block/layer pairs;
- it included additional two-gang/two-way switch observations and `a`, `b`, and `WP` tags;
- it contained 12 blocks and 8 routes rather than an unambiguous reviewed circuit subset;
- it reported `DBMOD=20`, including persistent database-variable bit 4 as well as view bit 16.

The strict rebind failed closed and no topology record was created. Rebind validation now
explicitly rejects DBMOD persistent object/database-variable/field bits `1/4/32`; only view/window
bits `8/16` may pass the DBMOD gate. The on-disk source remained SHA-256 identical.

Two subsequent window selections still included the long circuit polyline; one was truncated at the
256-point entity bound and neither was admitted as topology evidence. To make the next review exact
without asking the operator to fight AutoCAD window geometry, the read-only bridge now provides
`AIENGDEVICETAGCONTEXT`. It accepts the explicit selection but exports only block references and
TEXT/MTEXT, excluding route geometry before bounded serialization. Source build, PowerShell parse,
and the full automated baseline pass (`500 passed, 1 skipped`); live command acceptance remains next.

The preview launcher lifecycle was also corrected after an observed orphaned Python bridge. It now
records the direct Python PID, records the exact AutoCAD PID, and launches a hidden exact-PID monitor
that stops only that Python process after AutoCAD exits. The reviewed stale PID was removed; no broad
process-name termination was used.

## Device/tag-only live acceptance

The engineer selected the complete circuit-4 review region and ran `AIENGDEVICETAGCONTEXT` in a
fresh read-only copy. Context `abf6a4627d574cae8aa34ad6bd3a5efe`, evidence SHA-256
`0ebda1fd6741134c794caf82d95930d8e333382beb93bbcbd690a776cf41d361`, contains 26 complete
entities on five layers at DBMOD=16: 12 INSERT and 14 MTEXT, with no route geometry and no
truncation. The source `lighting.dwg` remained exact at SHA-256
`1d08ccb0557e740f7d471e006d5accad0675d54b82e45d9aabc64e239ecfb117`.

The exact self-hashed profile `lighting-abf6a462.json` has profile SHA-256
`d6ec74a203fd55a0e7125486f78f6cc03f611bd179202a2597959deaf0cdfc8a` and file SHA-256
`c20c3a4a62e3fef4ebaec8e4a14351a64939e298d8f74a07d00216d1fd37905b`. Deterministic local
import produced model SHA-256 `a19c668557e153d61e01a7a9df461465952d3c6e984cec42d55817d88542d4dc`
with 12 devices, one panel, one circuit, zero routes, and zero unknown objects:

- 1 pendant L05 and 4 rectangular spots L02;
- 2 toilet spots and 1 EF-02 exhaust fan/load;
- 1 one-gang/one-way, 2 two-gang/two-way, and 1 two-gang/one-way Weatherproof switch.

The 12 `a/b/c` control tag observations and one exact `WP` Weatherproof qualifier remain explicitly
unassociated. The engineer's statement that all selected devices belong to circuit 4 is retained as
an assumption pending a separate exact topology record; the convention profile does not infer that
membership. One reopened palette rendering remains required because the running bridge predates the
new Weatherproof grammar.

The first reopened session imported the exact rebound context successfully (the API log recorded
26 entities and zero unknowns), but pressing Refresh replaced the visible session counts with the
raw context/DBMOD line. This was a palette-state display defect, not an import failure. The Copilot
palette now retains the validated session summary across Refresh and clears it only on cancellation;
the rebuilt bridge and full automated baseline pass (`501 passed, 1 skipped`). Final reopened visual
confirmation remains pending.

Final reopened visual confirmation passed on exact context
`8683c155257048be8784dafc4a977b28` after its entity set was proven identical to the reviewed
26-entity baseline. The exact rebound profile SHA-256 is
`fdafee5858d8b5e7f519015e92348d087eedbb6a1733949abc547c1b617f7e14`; its file SHA-256 is
`edaf367fa4b7b8e8cb22d2d55712204e018921c51a287fec7408d1c0ec9b299c`. The API audit records
session `7164ccabd29e4f57b905d190d45aef07`, 26 imported entities, zero unknowns, no provider egress,
and `drawing_write_allowed=false`. The Arabic palette displayed 12 devices, zero routes, zero
unknown/missing objects, and five warnings, and retained these counts after Refresh. Screenshot
SHA-256 is `4f65a3a2589a14f01a1cee3d0ae5504e2be96649947c708ab63187e65cd961f4`
(25,527 bytes). The source remained 3,323,467 bytes at its exact prior SHA-256; no drawing mutation
was performed.

## Exact circuit-membership topology

The engineer explicitly confirmed that every block in the accepted 26-entity device/tag selection
belongs to circuit `4` of panel `DB3-GA2`. A separate immutable sidecar records that decision by the
12 stable AutoCAD source handles, not selection order. It is bound to the exact context evidence,
source drawing, and convention-profile hashes.

- Topology SHA-256: `8018e2dc346379c23f352143297cdf498423d19b6545728939aaaa80a0f7793a`
- Topology-file SHA-256: `2efc63e0f5b6c1d98bd3607ec6b643d666bfe32dea4c71ce84fb47fd43cde87f`
- Resulting canonical model SHA-256: `21f51947893e98e9c5e203e49b8203993c5ac15bea198391fa15f8f1901fd1c5`
- Circuit membership: 12 unique devices
- Retained observations: 12 `a/b/c` control tags and one `WP` Weatherproof qualifier
- `control_associations_performed=false`
- `route_connectivity_performed=false`

Thus circuit membership is reviewed, while no tag-to-device control association, cable route,
crossing connection, length, conductor, or protection fact is claimed. Those require separate exact
evidence. No drawing mutation or provider egress was performed.
## Route-only live acceptance

The engineer selected the circuit-4 region and separately picked a representative green route.
`AIENGROUTECONTEXT` exported only the matching `BLOCK003-G$0$E-Lite-Circ` layer. Exact context
`9446c5a6a484430d9dfd1753c1c21628`, evidence SHA-256
`48a4b9d533bfd9ce7b7deb06ea67f41f9eb1b0dd722d2b28615862c5b5b71063`, contains nine
LWPOLYLINE entities, zero unknowns, no truncation, and one 1,025-vertex long polyline. The bridge's
bounded point policy is 4,096; earlier 256-point contexts retain their original stricter policy.

The exact convention profile has profile SHA-256
`2bb6d1d4d4563e36798b0a99f85db9763aa3fbeff4f4e052c197cef3c8cc3092` and file SHA-256
`33288ece89e1a7303ca6559ae06b60b1b9bdeb0622c53a5013a8d23553536f66`. The resulting route-only
model SHA-256 is `d17fe02ff567f22f07e08eaaaffa74ea413b17eb847c41abb4fa5105f954d664`.

An exact route-membership sidecar assigns all nine source handles to circuit `4 / DB3-GA2`:

- membership SHA-256: `a07d3d672f96776ec20b018ff7af147a22e7264bbafdaeab8e75acc152263cdc`;
- membership-file SHA-256: `44d3513659f2e83ad6684825093e6312c3faf3791391c87cf24827f4e43d6809`;
- deterministically reproduced observed centerline: `10.2773298425 m`;
- `connectivity_confirmed=false`;
- `installed_cable_length_confirmed=false`.

No two route endpoints coincide exactly. Several drawn routes stop 50–100 mm before fixture
insertion points, and switch insertion points are not usable connection anchors for these nested
blocks. Therefore this evidence proves route membership and centerline geometry only. It does not
prove endpoint connectivity, cable start/end, installed cable length, vertical drops, termination
allowance, waste, conductor properties, or protection. No drawing mutation or provider egress was
performed.

## Connection-anchor implementation checkpoint — 2026-08-16

The cross-context review contract is implemented and automated-verified. It binds the exact device
topology and route-membership hashes, requires every route endpoint to appear exactly once as either
connected or unresolved, and accepts a connection only when its reviewed device-terminal anchor is
exactly the selected route endpoint. It rejects foreign/missing handles, displaced geometry,
duplicate or unaccounted endpoints, changed source/context/profile identities, and altered JSON.

Read-only command `AIENGCONNECTIONANCHOR` captures one explicit device block, one exact LINE or
LWPOLYLINE, an engineer click near the intended endpoint, and a terminal name defaulted to `CIRCUIT`.
The first live UI trial accepted a device but was cancelled without saving after it exposed that an
operator cannot reliably know a polyline's internal Start/End direction. The corrected command now
records the click, both endpoint distances, and only the strictly nearer endpoint. It saves the endpoint
atomically beneath the workspace, checks that DBMOD did not change, and records
`drawing_mutation_performed=false`. The AutoCAD 2021 DLL build passed; topology tests are `8 passed`
and the complete baseline is `509 passed, 1 skipped`. No `lighting.dwg` anchor was captured in this
automated checkpoint, so connectivity and installed cable length remain unconfirmed.

The first accepted live capture is `3f7e32ce6fcf488ca350e46ca797dab3`, SHA-256
`79cad9e9f6d64c80732321b155f907db94ba1377b711b2f3f620bcf5e3c6835d`. It explicitly binds
reviewed L05 device handle `1A1C2F8D` to route `1A1C30B5` endpoint `end`, terminal `CIRCUIT`, with
zero pick distance, DBMOD=16 before/after, and `drawing_mutation_performed=false`.

Two later captures were rejected from the active inbox and retained as evidence: SHA-256
`a70e10e26d64cde67b6f5a3e13cb9e347806b1ba111e3f13bd8ff555898a6fe4` references handles outside
the reviewed circuit-4 memberships; SHA-256
`021e9db8cd7c6a8f1069961e2633218fac33e2a4872a78911c2dc784ac695ab2` used the intended circuit-4
handles but reported DBMOD=20. The command and Python loader now reject all DBMOD bits except the
accepted presentation-only `8/16` mask, even when DBMOD remains unchanged during capture.

## Local two-way review-channel acceptance — 2026-08-16

The connection workflow now has a narrow local dispatcher rather than relying on copied chat
instructions and repeated manual AutoCAD selections. It accepts only
`review_connection_pair`; every request expires within 30 minutes and fixes the exact document,
source SHA-256, device handle, route handle, endpoint, terminal, and
`drawing_mutation_allowed=false`. The AutoCAD plugin uses supported read-only database APIs and
`Editor.SetCurrentView`, highlights only the requested pair, and displays a modal engineer Yes/No
review. There is no arbitrary command string or `SendStringToExecute` path.

The first complete live cycle used:

- request ID: `6a94eaf6c20b42fa96c6ec891f835f95`;
- request SHA-256: `5704eb694c7ff1399030e16feeda0af3e1dcafd9a0dae752f4111a955849a391`;
- device: `1A1C2F20` (reviewed L02 block);
- route/endpoint: `1A1C308D / start`;
- terminal: `CIRCUIT`;
- engineer decision: `approved` through `engineer_modal_yes_no`;
- exact endpoint: `[-4207.7104444143333, -22247.237481656724, 0]`;
- result SHA-256: `25c101a066bc7a9a0e0dba6c980de84908982e32c130bc1ab8ce501ffa42c289`;
- converted anchor SHA-256: `0c54fd83d26f0d483fe8ac33f31ddc58be6dc8b2c39a23e1dd986e0134f677a7`;
- DBMOD: `0 -> 16` (presentation/view state only);
- `drawing_mutation_performed=false`.

The source `lighting.dwg` remained byte-identical at SHA-256
`1d08ccb0557e740f7d471e006d5accad0675d54b82e45d9aabc64e239ecfb117`. The rebuilt x64 DLL
SHA-256 is `03333f82798f8451aa974bc5245fbe09887a9475cbd175f471f519e4cca3d356`.
Automated verification after adding exact result/request binding and atomic anchor retention is
`514 passed, 1 skipped`. Complete route connectivity is not yet claimed: five of eighteen reviewed
route endpoints have accepted evidence, and every remaining endpoint must be connected or explicitly
recorded unresolved.

The completed review supersedes that partial count. After a clean read-only document reopen removed
a launcher-created database-variable DBMOD bit, 13 further exact candidates were presented through
the same channel. Six were approved and seven were rejected. Together with the five prior captures,
the immutable record accounts for all 18 endpoints exactly once:

- connected endpoints: 11;
- explicitly unresolved endpoints: 7;
- connection-review SHA-256: `69e28f6854ff3fd495cefa050729135d14e531f260900496e7262d53ab3d9145`;
- review-file SHA-256: `73d8c7efacdfe7322758a9433d44a8ea54ec6a0bf99f0e4a319dde676db5167e`;
- complete endpoint accounting: `true`;
- drawing mutation performed: `false`;
- unchanged source SHA-256: `1d08ccb0557e740f7d471e006d5accad0675d54b82e45d9aabc64e239ecfb117`.

Each unresolved record includes the SHA-256 of the exact rejected modal result. “Unresolved” means
that no reviewed device candidate was accepted; it does not claim an open circuit, invent a hidden
connection, or convert annotation geometry into installed cable length.

The preview-launcher DBMOD issue is now corrected at its source. The launcher waits until the fixed
plugin bootstrap finishes, observes the exact disposable path and `CMDACTIVE=0` through AutoCAD's
supported COM interface, and discards/reopens only the read-only preview when forbidden DBMOD bits
exist. Live acceptance opened exactly one `lighting - Read Only 20260816-130151.dwg` document with
`DBMOD=0`, `CMDACTIVE=0`, and `ReadOnly=true`. It then closed without saving; the source retained
SHA-256 `1d08ccb0557e740f7d471e006d5accad0675d54b82e45d9aabc64e239ecfb117`.

## Visibility correction, route classification, and Scratch acceptance — 2026-08-16

The preceding 11-connected/7-unresolved result is retained as superseded evidence only. The engineer
reported that some batch decisions lacked a visible marker and the first view was too far out to
identify reliably. Six derived captures and both derived reports were moved under
`superseded-visibility-20260816`; none was deleted or left active.

Review UI `engineer_modal_endpoint_marker_v2` bounds the view to the device plus exact route endpoint,
draws a large red cross at that endpoint, and draws a yellow device-to-endpoint guide. After one
explicit repeat confirmed the visualization was clear, all uncertain pairs were reviewed individually.
The engineer also identified handles `1A1C3109` and `1A1C3093` as the line and arrowhead of a
circuit-number leader, not installed cable. The exact final evidence is therefore:

- seven electrical routes and two annotation polylines;
- 14 connected electrical endpoints, zero unresolved electrical endpoints;
- electrical observed centerline: `9.391922726917718 m`;
- excluded annotation centerline: `0.8854071155906424 m`;
- route-membership SHA-256: `04d99bcd581a696fac2d9a0a7596f5a9b1fe965d176ccd565cd910b908f8d2e2`;
- connection-review SHA-256: `c58bdf23acfec53d319e9bd222e93317ce791efd12359df0135eb576f5b870db`;
- connection-review file SHA-256: `1983501a094a23105a3ba807c3f4cb972e04218134ad05fbcb1313c2fe88b31e`;
- drawing mutation during review: `false`;
- source SHA-256 unchanged: `1d08ccb0557e740f7d471e006d5accad0675d54b82e45d9aabc64e239ecfb117`.

Live copied-Scratch execution run `ceca1501d7ed42199182498b1b00ef39` then added one bounded LINE
outside the project area only in its copied output. Entity count changed `4848 -> 4849` in
`2.1373586000 s`; output SHA-256 is
`7774bfb14d1b8983a1b5c3180b29af5b5e4bfdd4b811721e54cede57dba2e34f` and
`drawing-output.json` retains recovery identity. The source stayed byte-identical. A preceding circuit-
fragment attempt correctly created nothing because the existing `E-WIRE` layer did not match the
registered color policy; that fail-closed run is retained as evidence, not reported as success.

## Maximum bounded drawing-context and palette check — 2026-08-16

The source-tree launcher opened disposable read-only copy
`lighting - Read Only 20260816-143354.dwg`. `AIENGDRAWINGCONTEXT` exported the bounded first 512
entities, observed nine layers, and reported the expected `maximum_entities` truncation. The Arabic
palette rendered `512` context objects and `DBMOD=0`; all 512 were conservatively unknown because this
raw full-drawing slice was not rebound to the project convention profile. Context ID is
`292c724bc0e947f38b212f5053bdfc21`; context-file SHA-256 is
`48f29c9be99f779406d436293f0be90f782c51c835fc52bb52f4e6b31f64d40f`.

AutoCAD and the loopback bridge then closed without a listener. Source SHA-256 remained
`1d08ccb0557e740f7d471e006d5accad0675d54b82e45d9aabc64e239ecfb117`. This passes bounded capture,
palette rendering, truncation disclosure, and no-mutation behavior on the current profile. It does not
claim the time target: r16 adds an exact `AIENG_CONTEXT_DURATION_MS` marker for the required repeat.

## No-mutation operator-channel acceptance — 2026-08-16

With read-only copy `lighting - Read Only 20260816-150142.dwg` open, the external operator enqueued
three exact actions; the engineer did not type an AutoCAD command. `capture_drawing_context` request
`196e98aa1c1e404c8ec93f9a5b2735eb` returned 512 entities in `26.5282 ms`, context ID
`ff00aa6ac7154c69b85433f52e3740ed`, context SHA-256
`9f25972864e9666dc66c721750b4be7ddce2502669c99bb33540cbbf4ad37b54`, and result SHA-256
`8a6c6544bcd2717a0965426bad75938903052a8119d887b3cbbebf8b9eda0a91`.

`get_document_status` and `show_palette` returned result SHA-256 values
`166bb0c12ff1588bf2bb82a935350e45c857f94e707e7adab9a302ad75a18056` and
`944803f4fcd37190fb7c69dd399dfaf9764480249d1121c0c9911c18009c7721` respectively. All three
results bind the same document fingerprint and source SHA-256, report `DBMOD=0 -> 0`,
`drawing_mutation_performed=false`, and no error. The channel has no arbitrary command-text field and
does not grant providers or the operator a drawing-write capability.

The engineer then confirmed the AI-Engineer tab was visible and its palette opened when clicked.
AutoCAD and the exact loopback bridge closed without saving or a retained listener. The source
drawing SHA-256 remained unchanged.

## Operator-channel cancellation acceptance — 2026-08-16

Read-only copy `lighting - Read Only 20260816-152340.dwg` retained document fingerprint
`476310dc-0eb2-4b04-8b70-77e670dcf581` and source SHA-256
`1d08ccb0557e740f7d471e006d5accad0675d54b82e45d9aabc64e239ecfb117`. Capture request
`17a946ac5a994e20a67f4a38e31b2756` had request SHA-256
`837fe54f53d2de939a45e7ba47cdac74b5db39db8599f512579c1e71d9810939`. Its separately written
cancel record bound both identifiers and was acknowledged as `status=cancelled` in `619.1593 ms`.

The cancelled result contains no `context_id`, context hash, entity count, duration, error, or
partial drawing evidence; `drawing_mutation_performed=false`. No newer context file was published.
Follow-up status request `b3d2a4a626e2491f97664b40ce0e73a6` returned result SHA-256
`77a0ae7015bea62b70e7ff40aff733e1cb8522c421d3b109c5274bf6666b4220` and confirmed
`DBMOD=0 -> 0`. AutoCAD then closed without saving. This passes the current-profile two-second
cancellation target; maximum-fixture Scratch and second-profile acceptance remain separate gates.

## Raw block and route-primitive summary acceptance — 2026-08-17

The engineer captured a fresh explicit selection from disposable read-only copy
`lighting - Read Only 20260817-092024.dwg`. Context ID
`644b7b85c0454631b28124b839318e64` is complete, contains 156 entities, reports presentation-only
`DBMOD=16`, and has context-file SHA-256
`a0e3efe062c5db5cac32830cfc23389b4504208278ad3f92e0c4c0f2de8d89ab`.

Independent inspection and the live Arabic palette agreed exactly on 49 top-level raw `INSERT`
references and 38 raw `LINE`/`LWPOLYLINE` primitives. The palette separately displayed zero
classified devices and zero classified routes because no convention profile is bound to this new
exact context evidence. It therefore no longer presents those zeros as absence of drawing content.
Unknown count remained 156 and warnings remained two; no provider egress, approval, or drawing
mutation was performed. The loaded source-tree bridge DLL SHA-256 after rebuild is
`b252a5bacdbf72f08d266044df4dd865b90b2309cee88627089c7b8f229086de`.

Raw route primitives are geometry observations only. They do not claim 38 complete circuits,
installed cables, connectivity, or cable length. Classification and aggregation remain behind the
existing exact convention/topology review boundary.
