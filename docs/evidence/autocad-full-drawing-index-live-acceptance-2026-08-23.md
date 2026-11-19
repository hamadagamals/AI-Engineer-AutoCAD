# AutoCAD full-drawing index live acceptance — 2026-08-23

## Scope

This acceptance exercised the source-tree AutoCAD 2021 x64 plugin against a disposable,
read-only copy of `workspace/lighting.dwg`. It tested the new local active-document beacon,
the exact `capture_drawing_index` operator request, adaptive chunking, strict local manifest
import, global handle uniqueness, follow-up channel responsiveness, and unchanged-source
evidence. It did not use a manual AutoCAD selection, provider egress, or drawing mutation.

## Source identity

- Source drawing SHA-256:
  `1d08ccb0557e740f7d471e006d5accad0675d54b82e45d9aabc64e239ecfb117`
- AutoCAD document ID: `476310dc-0eb2-4b04-8b70-77e670dcf581`
- Disposable drawing: `lighting - Read Only 20260823-130247.dwg`
- AutoCAD target: AutoCAD 2021 / R24.0, English, Windows 11
- Provider egress: false
- Drawing mutation allowed/performed: false/false

## Bootstrap and exact request

The plugin published a fresh local `autocad.active_document_status.v1` record without
geometry. It reported the exact document ID, source SHA-256, `DBMOD=0`, and
`read_only_channel_ready=true`. No AutoCAD command or selection was entered by the engineer.

- Request ID: `7550656f5b68430685268d267192524b`
- Request SHA-256:
  `0bf2b4b7cba3782f41b018ffc30cf10f1edd3f61fd68a46c198f857936c81bb2`
- Requested action: `capture_drawing_index`
- Request mutation authority: false

## Accepted result

- Status: completed
- Result ID: `f81c2433dd1f448e93e65b7ed0d96ce6`
- Result file SHA-256:
  `d9daacf232615e6d67a4dd57ad14330431247d376508bbaeda6e475384f65b42`
- Index ID: `277d2df4fb664716a74dfba300014af3`
- Index manifest SHA-256:
  `fd26aa09be4f618780f4fe5bac2f483d9e4c7fb7812a66109bea631f666e0ded`
- Entity count: 4,848
- Adaptive context chunks: 76
- Unique entity handles after independent import: 4,848
- Plugin-reported duration: 1,118.8101 ms
- DBMOD: `0 -> 0`

The independent Python loader verified every manifest and context SHA-256, exact document
identity, units, DBMOD, complete/non-truncated state, chunk totals, and global handle
uniqueness. No entity was omitted or repeated. Observed top-level entity types were:

- `LINE`: 1,051
- `LWPOLYLINE`: 747
- `INSERT`: 968
- `TEXT`: 754
- `MTEXT`: 961
- `ARC`: 52
- `CIRCLE`: 103
- `UNSUPPORTED`: 212

The index observed 4,847 Model-layout objects and one object in layout `lightng`, across
45 referenced layers. These are structural observations only; they do not assign electrical
meaning to unknown geometry, nested definitions, or Xref contents.

## Follow-up verification and cleanup

A separately bound `get_document_status` request completed after the index and confirmed
`DBMOD=0 -> 0` with result SHA-256
`bb766e1c14e36e1cef1590bb75780b1b5ca8a3a22568ac08ea168b440f980a6b`.
The source drawing SHA-256 remained unchanged. The exact disposable drawing was then closed
without saving at `DBMOD=0`; its isolated loopback bridge stopped with AutoCAD.

## Superseded diagnostic

The first live attempt used a dense 512-entity index chunk and failed safely before manifest
publication because `JavaScriptSerializer` reached its internal JSON length. No mutation or
completed index was claimed. The implementation was changed to start index chunks at 64
entities and halve a chunk automatically when payload density requires it. Automated tests,
the x64 plugin build, and the accepted live retry then passed.

## Remaining boundary

This acceptance covers all top-level entities in Model and paper layouts. Nested block
definition contents and external-reference contents remain separate, explicit future scopes.
The index is local evidence; a drawing-wide semantic electrical model and incremental refresh
remain follow-on work.
