# Engineering Assurance Plan

This plan turns AI-Engineer from a coding/operator agent into a traceable electrical
engineering assistant without treating language-model memory as a calculation or code
source.

## Assurance stack

`Drawing evidence -> Shared electrical model -> Deterministic calculations -> Standards evidence -> Compliance rules -> AI explanation -> Engineer approval -> Draft/execute/verify`

Each arrow is a typed boundary with hashes, units, provenance, limits, and explicit
unknowns. Provider choice (local, Codex, OpenAI) changes reasoning quality only; it does
not change the evidence or execution authority.

## Milestone 19 — Traceable quantities

- object-linked cable/wire/conduit/tray/device quantities;
- separate geometric, vertical, termination, waste, and engineer-override values;
- explicit units and source handles;
- no inferred route length when geometry is missing;
- reviewed CSV/Excel export.

## Milestone 20 — Deterministic LV calculations

Implement pure, versioned calculation functions with golden test vectors before adding
AI orchestration. Initial scope:

- connected and demand load;
- three-phase/single-phase current;
- diversity and demand factors supplied by the active project rule set;
- phase balance;
- cable ampacity input model and derating chain;
- voltage drop;
- protective-device sizing/coordination inputs;
- PE/earthing-conductor sizing rule interface;
- short-circuit inputs and calculation boundary.

Every result must include exact inputs, units, formula/version, assumptions, warnings,
source-object links, and reproducible numeric output. No code-specific limit may be
hard-coded without a pinned rule-set source.

## Milestone 21 — Panels and SLD

- stable circuit identity across model, panel schedule, and SLD;
- deterministic schedule generation from approved data;
- extraction of supported existing SLD entities back to the model;
- conflict/ambiguity findings instead of silent reconciliation;
- preview and separate approval before drawing changes.

## Milestone 22 — Standards and compliance

- project profile pins jurisdiction, standard, edition, amendments, and effective date;
- only official or lawfully licensed evidence is indexed;
- clause-level source identity and content hashes are stored with findings;
- deterministic rule evaluator produces PASS/FAIL/UNKNOWN/NOT-APPLICABLE findings;
- AI explains findings but cannot change the deterministic result or invent evidence;
- changing an active edition requires an impact review rather than silent replacement;
- copyrighted standards are not reproduced wholesale.

## Milestone 23 — Manufacturer evidence

- opt-in official manufacturer retrieval;
- source URL/document/version/date/hash provenance;
- quarantined downloads and file-type/size checks;
- reviewed property mapping into the shared model;
- imported CAD assets pass through existing block/catalog review controls.

## Acceptance rule

No feature is considered professionally complete from unit tests alone. Safety-critical
engineering stages require representative fixtures plus live Windows/AutoCAD acceptance,
recovery testing, failure injection, provenance review, and an engineer-visible audit
report before the milestone is released.
