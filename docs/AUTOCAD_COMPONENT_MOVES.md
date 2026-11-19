# Existing-component relocation: preview foundation

Status: source implementation and fixture tests pass; native relocation execution is **not
implemented or enabled**. The installed r49 does not yet contain this successor change.

## Implemented boundary

`autocad.preview_component_moves` is a registered HIGH-risk, authenticated local command.
Its durable approval gate is deliberately retained because it imports confidential CAD context;
LOW/MODERATE automatic continuation cannot silently approve that import. The command reads a
sandboxed saved DWG up to 2 MiB, verifies its exact expected SHA-256 before and after importing
the exact captured context, and returns a deterministic self-hashed, read-only proposal.

The Project Agent capture button uses AutoCAD's supported `ExecuteInCommandContextAsync` and
existing explicit-selection exporter. It never sends command text or modifies the drawing.
The captured references are shown in the goal field; preparing a run now requires a separate
fail-closed egress review showing the goal and explaining the bounded approved-tool observations.

The preview supports 1..32 uniquely identified visible top-level named BlockReference objects,
Model space, explicit millimetre units, clean or presentation-only DBMOD, and XY translations
preserving Z, rotation and scale. It rejects invalid/non-finite or unbounded coordinates,
unknown/duplicate handles, no-op moves, hidden/frozen/off/locked-layer entities, anonymous blocks,
proxy entities, truncated captures, and repeated target insertion points.

Example typed payload:

```json
{
  "path": "workspace/review-drawings/sample.dwg",
  "expected_source_sha256": "<exact lowercase source SHA-256>",
  "context_id": "<exact lowercase capture ID>",
  "evidence_sha256": "<exact lowercase capture SHA-256>",
  "moves": [{"object_id": "A1", "target_mm": [100, 200, 0]}]
}
```

## Mandatory execution hold points

The existing capture contains insertion points, not native block extents or Xref/group
dependency evidence. Matching a document filename does not prove that the saved source and
in-memory capture are identical. The source SHA and context SHA remain distinct identities;
neither can substitute for the other. Accordingly every returned preview explicitly reports
`drawing_mutation_performed=false`, `execution_approved=false`, `execution_ready=false`.

Before execution support is added:

1. Reconcile saved-source fingerprint and each exact block identity/transform natively.
2. Capture geometric extents and dependency facts; check collisions, enclosure boundaries,
   compartment zones and separately reviewed clearance values. Never invent code clearances.
3. Associate separate labels, leaders and wiring only by reviewed explicit grouping. Attributes
   are reported, but standalone text/wires are not silently included in a move.
4. Extend the existing Milestone 18 lifecycle to bind the exact move-preview hash to a one-use
   execution review, supported-API scratch transaction, recovery and postcheck. Do not expose
   an unrestricted live document write channel or execute provider-generated scripts.
5. Verify moved and unmoved control objects, preserve original source hashes, create a new-file
   handoff only after verified execution, and pass a native live acceptance test.

No optimized panel size, IEC 61439 compliance, thermal clearance, or fabrication readiness is
claimed by this geometric proposal. Existing Hutchison Ports engineering hold points remain open.
