# AI-Engineer Product Vision

## Product mission

AI-Engineer is a safe, local-first electrical-engineering copilot for Windows. Its
primary product experience is a conversational palette inside AutoCAD where an
engineer can select drawing content, ask questions in Arabic or English, review a
proposed plan, preview changes, approve execution, and receive verified drawings,
calculations, schedules, quantities, and compliance reports.

The long-term product is an engineering integration platform rather than an AutoCAD
macro. A shared electrical project model connects drawings, deterministic calculation
engines, current code evidence, manufacturer data, and reviewed adapters for tools
such as ETAP and SOLIDWORKS Electrical. The engineer remains the approving authority.

## Operator experience

The intended workflow is:

1. The engineer opens an AutoCAD project and its AI-Engineer palette.
2. The engineer chooses a drawing, layout, or explicit selection and asks a question
   or requests work.
3. The local adapter extracts bounded structured CAD data rather than relying only on
   screenshots: entities, blocks, attributes, layers, geometry, and relationships.
4. AI-Engineer maps known content into the shared electrical model, reports unknown or
   missing data, and does not invent engineering inputs.
5. The AI proposes a bounded plan with sources, assumptions, affected objects, and
   required approvals.
6. Deterministic, tested code performs calculations and validation; the AI interprets
   intent, retrieves evidence, plans work, and explains results.
7. Drawing changes are previewed and applied only after approval through supported
   APIs inside an undoable transaction, scratch drawing, or verified handoff.
8. Results retain provenance so the engineer can trace every value to a drawing,
   manual input, calculation, code clause, manufacturer document, or external tool.

## Target capabilities

### AutoCAD electrical copilot

- An in-product palette with Arabic and English chat, progress, review, preview,
  approval, cancellation, and result navigation.
- Explicit selection-aware and drawing-aware context with bounded read access.
- Structured understanding of layers, blocks, attributes, text, dimensions, lines,
  polylines, arcs, layouts, and supported electrical relationships.
- Reviewed block generation, intake, editing, cataloging, and insertion.
- Previewed and undoable creation or modification of devices, circuits, routes,
  annotations, schedules, and diagrams.
- User-created content remains editable through ordinary AutoCAD tools.

### Shared electrical project model

The canonical model represents projects, sources, transformers, switchboards, panels,
circuits, protective devices, cables, conductors, terminals, loads, earthing, routes,
drawings, calculations, requirements, and manufacturer components. Each object has a
stable identity, units, provenance, validation state, and links to its representations
in connected applications.

The model is the digital thread between drawings, calculations, schedules, quantities,
single-line diagrams, compliance findings, and external engineering tools. Adapters
must never silently treat one application's incomplete representation as authoritative.

### Quantities and deterministic engineering

- Cable, wire, conduit, tray, device, and component quantities with explicit route,
  vertical allowance, waste, and classification rules.
- Connected and demand loads, diversity, phase balancing, cable sizing, current
  capacity, derating, voltage drop, protective-device checks, earth-conductor sizing,
  panel schedules, and short-circuit calculations.
- Single-line diagram generation from approved project data and structured extraction
  from supported existing diagrams, with ambiguity reported rather than guessed.
- Calculations use versioned, unit-tested formulas and rule sets. AI-generated prose is
  never the calculation authority.

### Live code intelligence and compliance

- A project code profile records jurisdiction, project type, authority hierarchy,
  standard names, editions, effective dates, and project-specific requirements.
- The engineer may request the Egyptian code, IEC, NEC/NFPA, BS, or another reviewed
  code profile. The system identifies the relevant topic but shows the selected code
  and clause before treating it as a requirement.
- Current clauses come from official or lawfully licensed websites, APIs, subscriptions,
  or user-provided licensed documents. The platform does not bypass access controls or
  reproduce protected publications wholesale.
- Every finding records source, edition, clause, retrieval date, content identity,
  extracted rule, drawing facts, assumptions, formula, result, and confidence.
- Projects pin reviewed editions. Discovery of a newer edition produces an alert and
  impact review; it never silently changes an active project's rules.
- Conflicting authorities or missing evidence stop automatic acceptance and require an
  engineer decision. AI-Engineer assists review but does not issue regulatory approval.

### Manufacturer and internet evidence

- Opt-in searches prioritize official manufacturer and authority sources.
- Data sheets, manuals, product properties, dimensions, and official CAD assets retain
  source links, document versions, retrieval dates, and hashes where possible.
- Downloaded documents and drawings are quarantined, bounded, validated, and reviewed
  before project use. Untrusted executable content is never imported.
- If no official CAD asset exists, a proposed block may be generated from cited,
  reviewed dimensions and must pass the normal catalog approval workflow.

### Engineering application connectors

- AutoCAD is the first production connector.
- ETAP is the first planned calculation-system connector for reviewed network exchange
  and result reconciliation.
- SOLIDWORKS Electrical is planned for schematics, wires/cables, terminals, component
  data, cross-references, and bills of materials.
- Excel and future tools such as Revit or EPLAN can be added only through capability-
  scoped adapters and the shared model.
- The user-directed Revit 2024 foundation starts as a supporting read-only adapter. It
  may prove installation and bounded model inventory while AutoCAD remains the production
  target, but it cannot claim geometry/parameter completeness or introduce model mutation
  before its own preview, transaction, recovery, and live-acceptance gates pass.
- Prefer supported APIs, SDKs, documented automation, and validated exchange formats.
  Fragile mouse/keyboard automation is not a normal integration mechanism.

## AI, privacy, and trust boundaries

- The local application decides what drawing context is available. A cloud provider
  never receives a whole drawing or project implicitly.
- Data egress is explicit, previewable, bounded, logged, and configurable per project.
- Provider output is untrusted input to typed validators, deterministic engines,
  permissions, approvals, and verification.
- Secrets, proprietary drawings, licensed code text, and confidential data are not
  stored in source control or operational logs.
- Destructive or high-risk actions require durable approval, backups, and recovery.
- No public unauthenticated API, unrestricted terminal, arbitrary AutoCAD command,
  silent code-edition change, or uncontrolled self-modification is permitted.

## Delivery strategy

Development proceeds as one production-quality vertical slice at a time:

1. Structured read-only drawing context and shared electrical model.
2. Read-only AutoCAD palette and provider/privacy boundary.
3. Previewed multi-device drafting with safe transactions and handoff.
4. Quantity takeoff and deterministic electrical calculations.
5. Panels, schedules, and bidirectional single-line workflows.
6. Live, cited code intelligence and manufacturer evidence.
7. ETAP, then SOLIDWORKS Electrical, through reviewed connectors.
8. Real-project acceptance, performance, recovery, security, and `1.0.0` hardening.

### AutoCAD-first product gate

The first complete product release is an AutoCAD-only electrical-engineering edition.
It must provide the full reviewed workflow inside AutoCAD: structured drawing reading,
Arabic/English chat, deterministic electrical calculations, selected-code review,
quantities, block/catalog workflows, previewed drafting, undoable execution, recovery,
and traceable reports before production work begins on ETAP or SOLIDWORKS Electrical
connectors.

"Works for any project" means the AutoCAD edition is project-agnostic within an
explicitly documented and tested supported-feature matrix; it must report unsupported
objects, project types, voltage levels, calculation methods, or code editions instead
of guessing. Real project fixtures expand that matrix through acceptance testing.

The current implemented boundary is recorded in `docs/AUTOCAD_SUPPORTED_FEATURES.md`; release
claims must not exceed it without implementation, tests, and applicable live evidence.

Experimental read-only connector foundations requested by the operator do not reorder
the production roadmap. They must remain isolated, no-egress, non-mutating, and honestly
blocked if their host application has not passed live acceptance.

After the AutoCAD edition passes its release gate, integrations are added one product
at a time. ETAP is the first separate integration edition, followed by SOLIDWORKS
Electrical. Each connector starts read-only, preserves identities through the shared
model, and receives its own round-trip, recovery, and live-acceptance gate before the
next application is started.

Breadth does not replace verification. A connector or engineering feature is complete
only after fixture tests, failure tests, live validation in its supported application,
traceable output, documentation, and an honest limitation statement.

## Proposed initial engineering profile

Until the operator confirms project-specific settings, planning assumes building
low-voltage electrical work, metric units, Arabic/English interaction, an Egyptian
jurisdiction profile, and explicitly selected Egyptian/IEC requirements. These are
planning defaults only; calculations and compliance checks must not use them silently.
