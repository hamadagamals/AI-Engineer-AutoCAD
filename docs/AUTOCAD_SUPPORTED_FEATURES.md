# AutoCAD supported-feature matrix

## Purpose and status labels

This matrix is the honest product boundary for the current AutoCAD-only AI-Engineer beta source.
It is derived from implemented contracts, automated tests, and recorded AutoCAD 2021 acceptance;
it is not a promise that arbitrary drawings or project conventions are understood.

- **Live accepted** — exercised through the supported AutoCAD 2021 interface with recorded evidence.
- **Automated verified** — implemented and tested, but not accepted on a representative live project for this exact use.
- **Prepared** — tooling exists, but an external prerequisite or separate acceptance remains.
- **Not supported** — the product must report this boundary rather than infer or fabricate it.

## Platform and drawing context

| Capability | Status | Current boundary |
|---|---|---|
| Windows 11 + AutoCAD 2021 English | Live accepted | AutoCAD R24.0 fixed target; other releases are not claimed. |
| Active drawing / explicit selection export | Live accepted | Read-only structured JSON; explicit selection is preferred. |
| Entity types | Live accepted | `LINE`, 2D `POLYLINE`, `ARC`, `CIRCLE`, `INSERT`, `TEXT`, `MTEXT`, and `DIMENSION`; unknown types remain unknown. |
| Block attributes and layers | Live accepted | Bounded attributes/layers, geometry, and source identities. |
| Exact-context project block/tag conventions | Live accepted | Separate source-identical captures recognize 12 devices and 9 routes for `DB3-GA2 / 4`; changed/new/duplicate identities fail closed and no crossing or control topology is inferred. |
| Reviewed device/route connection anchors | Live accepted | Manual `AIENGCONNECTIONANCHOR` and an allowlisted local request/result channel record exact device, route, endpoint, terminal, world point, DBMOD, request/result hashes, and no-mutation flag. Endpoint-marker-v2 uses a bounded device/endpoint zoom, red endpoint cross, and yellow guide. For `lighting.dwg`, seven electrical routes have all 14 endpoints connected; two additional polylines were explicitly classified as one circuit-number arrow/leader and excluded from cable topology and length. |
| AI-to-AutoCAD read-only operator channel | Live accepted | Exact expiring SHA-bound requests for bounded drawing context, document status, and palette display are live accepted. The new chunked drawing-index action is automated verified separately; no arbitrary command string or drawing mutation capability exists. |
| Automatic full-drawing top-level index | Live accepted | The local channel discovered `lighting.dwg` without a manual command/selection and exported 4,848 unique top-level entities in 76 deterministic hash-bound adaptive shards in 1.119 s at `DBMOD=0 -> 0`. Any missing, changed, truncated, duplicate, or cross-document shard fails closed. The bounded scope is 200,000 entities/4,096 chunks/120 s; nested block definitions and Xrefs are not included. |
| Drawing-wide inventory and comparison | Automated verified on live evidence | All 4,848 accepted index entities reload as one immutable source-bound inventory. Exact reviewed handles yielded 336 devices, 7 routes, and 2 annotations; 647 same-layer primitives remain candidates and 3,856 entities remain unclassified. Handle-level comparison separates additions, removals, geometry changes, and semantic reclassification. A validated selective delta now produces a reload-verified v2 inventory with explicit baseline/delta lineage and mandatory semantic review. |
| Automatic inventory-refresh orchestration | Live accepted | Discovers exactly one active drawing, submits the allowlisted no-mutation full index, validates every shard, writes the inventory, compares an optional baseline, and stores a self-hashed report without manual selection. Unchanged `lighting.dwg` returned 4,848 unchanged; a one-LINE Scratch change returned exactly one added handle and invalidated 991 stale source-bound semantics with review required. Both ran at `DBMOD=0 -> 0`. |
| In-session selective drawing delta | Live accepted | A complete index arms an exact in-memory baseline; AutoCAD append/modify/erase events feed a one-use allowlisted read-only delta containing changed-entity shards and removed handles only. A saved Scratch trial returned exactly added `1A1C523C`, modified `1A1C51F4`, and removed `1A1C520C` in 25.6422 ms at `DBMOD=0 -> 0`; unchanged control `1A1C5224` was absent. Its v2 inventory composition reproduced the same partitions plus 4,849 unchanged handles. Missing/restarted baseline, overlap, tampering, reuse, or more than 4,096 candidates fails closed. |
| Default context limits | Live accepted | 512 entities, 128 layers, 32 attributes/entity, 4096 points/entity for current captures, 4096 text bytes, 1 MiB payload, nesting 8, duration 30 s; legacy evidence retains its recorded 256-point limit. |
| Automatic semantic meaning from arbitrary geometry | Not supported | A line is not silently treated as a cable; blocks/tags/markers and reviewed relationships provide meaning. |
| Xrefs, dynamic behavior, hatches, splines, 3D solids, custom/proxy objects | Not supported semantically | They may be unknown; no electrical meaning or safe rewrite is claimed. |

## Drafting, blocks, and drawing change

| Capability | Status | Current boundary |
|---|---|---|
| Multi-object electrical proposal and preview | Live accepted | Typed layers, devices, terminals, circuits, and routes; exact hashes precede execution. |
| Bounded line and `E-WIRE` route in scratch drawing | Live accepted | Fixed primitives/layer policy; live `lighting.dwg` Scratch execution added exactly one line (`4848 -> 4849`) in 2.1374 s while the source SHA-256 remained unchanged. Existing incompatible project layers fail closed. |
| Catalog symbol placement | Live accepted | Millimeter drawings; exact definition SHA-256, bounded transform, no silent redefinition. |
| Existing block intake and catalog registration | Live accepted | Bounded `LINE`/`CIRCLE` definition, read-only review, separate approval, backup and rollback. |
| Circuit fragment | Live accepted | 2–8 verified symbol placements plus 1–15 connected line segments in scratch output. |
| Fixed maximum execution performance fixture | Live accepted, release-test only | Exactly 1,792 fixed lines on a reserved layer in a hash-bound Scratch copy; recovery evidence disables handoff. This does not raise production drafting limits. |
| Verified new-file handoff / replacement | Live accepted | Exact hash; replacement needs separate approval, current hash, backup, atomic install, and recovery. |
| Arbitrary active-drawing edits | Not supported | No unrestricted AutoCAD command or model-generated mutation path. |
| General block synthesis from photographs, PDFs, or arbitrary vendor CAD | Not supported | Vendor CAD must pass evidence, quarantine, review, and supported geometry/lineage gates. |

## Quantities and deterministic calculations

| Capability | Status | Current boundary |
|---|---|---|
| Device counts and route lengths | Live accepted | Explicit objects/routes, units, allowances, assumptions, and reviewed CSV. |
| LV current and approximate voltage drop | Live accepted | Single-phase or balanced three-phase; not unbalanced harmonic/load-flow analysis. |
| Load aggregation and phase allocation | Live accepted | Explicit allocations and demand/diversity inputs. |
| Cable ampacity derating | Live accepted | Reviewed base ampacity and factor chain; no automatic code-table lookup. |
| Source-equivalent short circuit | Live accepted | Reviewed R/X basis; symmetrical RMS within the documented formula. |
| Cable/protective-device candidate adequacy | Live accepted | Recalculated evidence comparison; no automatic selection. |
| Protective conductor adiabatic sizing | Live accepted | Explicit applicability, duration, conductor factor, and source basis; uncertainty fails closed. |
| Harmonics, motor starting, arc flash, coordination curves, load flow | Not supported | Requires separate engines or reviewed external-tool connectors. |

## Panels, schedules, and SLD

| Capability | Status | Current boundary |
|---|---|---|
| Deterministic panel schedule | Live accepted | Typed panel/ways/loads/spares with reviewed phase and protection data. |
| Existing schedule/SLD extraction | Live accepted | Only fixed `AIENG_M21_*_V1` markers and validated attributes/edges. |
| Fail-closed ambiguity | Live accepted | Invalid markers return `UNKNOWN`/`CONFLICT`; no topology is invented. |
| Arbitrary legacy SLD recognition from lines/text alone | Not supported | Requires explicit intake for each convention. |

## Code and manufacturer evidence

| Capability | Status | Current boundary |
|---|---|---|
| Hash-bound code profile and findings | Live accepted | Exact lawful evidence; four finding states; no regulatory approval. |
| OSHA public-clause acceptance | Live accepted | One public fixture proves the path; it is not a general code library. |
| Egyptian/IEC/NEC/BS comprehensive clause database | Not supported | Editions and lawful sources must be reviewed and pinned. |
| Manufacturer evidence/package/CAD quarantine | Live accepted | Official Legrand fixture; exact URLs, hashes, types, approvals, and negative flags. |
| Free browser-assisted product candidates | Live accepted | Saved official UTF-8 HTML in workspace, up to 2 MiB; exact part/evidence/hash checks. |
| Paid official-domain search | Live accepted | Quality returned two official candidates in AutoCAD 2021; exact model/result/choice identities and cost-safe request caching were recorded, with no automatic follow-on action. |
| Automatic product approval/download/insertion | Not supported | Candidate choice is only for a separate review. |

## Copilot, integration, and release

| Capability | Status | Current boundary |
|---|---|---|
| AutoCAD read-only copilot palette | Live accepted | Authenticated localhost API and explicit data-egress review. |
| Ollama/Codex/OpenAI provider modes | Prepared / partly live | Local Ollama accepted; providers have no execution authority. |
| AutoCAD beta portable package | Prepared | Manifested ZIP, atomic install, clean runtime, readiness check; real-project/multi-PC gate remains. |
| ETAP and SOLIDWORKS Electrical | Not supported yet | Must not begin until the AutoCAD product gate is complete. |
| Publisher-signed installer / automatic updater | Not supported | Current ZIP manifest is not a publisher signature. |

## Required beta behavior

Outside the supported boundary, AI-Engineer must return an explicit unknown, conflict,
unsupported object, missing input, missing evidence, or required-review result. It must not infer
cable connectivity from crossings, choose equipment, claim code compliance, download a
manufacturer asset, or modify a drawing merely because a language model suggests it.

Before a beta is called usable on a project, that project's layers, block/tag conventions, units,
object count, calculation assumptions, code profile, and recovery path must be reviewed against
this matrix and recorded in a representative real-project acceptance report.
