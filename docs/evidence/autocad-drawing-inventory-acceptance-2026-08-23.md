# AutoCAD drawing-wide inventory acceptance — 2026-08-23

## Scope

This acceptance aggregated the previously live-accepted, complete top-level
`lighting.dwg` index into one immutable local inventory. It exercised exact source and
document binding, reviewed convention and route-membership binding, conservative semantic
classification, atomic persistence, independent reload, and deterministic handle-level
comparison. It did not reopen AutoCAD, transmit provider data, or mutate a drawing.

## Bound evidence

- Source drawing SHA-256:
  `1d08ccb0557e740f7d471e006d5accad0675d54b82e45d9aabc64e239ecfb117`
- AutoCAD document ID: `476310dc-0eb2-4b04-8b70-77e670dcf581`
- Drawing index ID: `277d2df4fb664716a74dfba300014af3`
- Drawing index SHA-256:
  `fd26aa09be4f618780f4fe5bac2f483d9e4c7fb7812a66109bea631f666e0ded`
- Reviewed route-membership SHA-256:
  `04d99bcd581a696fac2d9a0a7596f5a9b1fe965d176ccd565cd910b908f8d2e2`
- Inventory ID: `765eab778e9eb39955175663bf686967`
- Inventory SHA-256:
  `ee801a46f0282e14e4456332177557ada04c93723e0b29405a01b4e65bba7159`

The immutable runtime artifact is
`workspace/.ai-engineer/autocad/drawing-inventory/765eab778e9eb39955175663bf686967.json`.
Independent loading revalidated its exact schema, internal hash, document ID, source SHA-256,
entity identities, unique handles, counts, and safety flags.

## Accepted inventory

- Total entities: 4,848
- Supported observations: 4,636
- Unsupported observations retained explicitly: 212
- Raw block references: 968
- Raw `LINE`/`LWPOLYLINE` primitives: 1,798
- Reviewed devices: 336
- Exact reviewed electrical routes: 7
- Explicit reviewed annotation polylines: 2
- Route candidates: 647
- Unclassified entities: 3,856
- Provider egress performed: false
- Drawing mutation performed: false

The seven confirmed routes are exact handles from reviewed membership evidence. The two
reviewed circuit-number arrow/leader handles are annotations, not cable. The remaining 646
same-convention-layer primitives and one `E-WIRE` primitive remain route candidates; the
inventory does not infer electrical connectivity or installed cable length from their layer.

## Incremental comparison baseline

The deterministic comparison of the independently reloaded accepted inventory against itself
returned:

- Added: 0
- Removed: 0
- Geometry/content changed: 0
- Semantically reclassified: 0
- Unchanged: 4,848
- Diff SHA-256:
  `8f705f313c1995ec902b6d6dae99bfc6e5b8373efb4142c0620c6c27f5b5f133`

Automated fixtures separately exercise all added, removed, geometry-changed, and reclassified
partitions. This is deterministic incremental comparison after two complete inventories. A
change-triggered selective AutoCAD recapture path is still pending and is not claimed here.

## Superseded diagnostic

An earlier local aggregate (`636484b30dd9feb64c8091bf2fd6626b`, SHA-256
`072f11e1287c4451966a24600564e84be7437f5d7d8553885f6aeb079222369e`) used layer
membership alone and reported 655 reviewed routes. That classification is superseded and must
not be used: the project review already proves that the
same layer can contain a circuit-number arrow/leader. The corrected inventory uses exact reviewed
handles and retains all other layer matches as candidates.
