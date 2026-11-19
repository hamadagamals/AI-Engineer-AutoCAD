# AutoCAD automatic inventory-refresh live acceptance — 2026-08-23

## Scope

This acceptance exercised the source-tree automatic refresh coordinator against an isolated,
read-only copy of `workspace/lighting.dwg` in AutoCAD 2021. The coordinator discovered the fresh
local document beacon, submitted the exact allowlisted `capture_drawing_index` request, validated
the returned index, built and stored the source-bound inventory, compared the accepted baseline,
and wrote an immutable refresh report. No AutoCAD selection or command entry was required.

## Source and channel identity

- Source drawing SHA-256:
  `1d08ccb0557e740f7d471e006d5accad0675d54b82e45d9aabc64e239ecfb117`
- Document ID: `476310dc-0eb2-4b04-8b70-77e670dcf581`
- Isolated drawing: `lighting - Read Only 20260823-140437.dwg`
- Operator request ID: `a34e3d78828f4cf1975c6d0bcf5fba8d`
- Operator result ID: `e612640a92cb4bb4b6616a07234dee23`
- Operator result SHA-256:
  `9865bf22a6c6bd98288d9c2dc05b370739703f40d427e6e75b2e8987aa333903`
- Requested action: `capture_drawing_index`
- Result status: `completed`

## Accepted refresh

- Index ID: `8bc46d5fe42d4e70b7c7ca39ec008b5b`
- Index manifest SHA-256:
  `c3c8ace17183f5bb9f0362685ff5746e89da6168e7f4275119cb434a1cc2b590`
- Entity count: 4,848
- Index duration: 1,092.7098 ms
- DBMOD: `0 -> 0`
- Inventory ID: `3601e7a6409eb3da1e375e8acb9f706a`
- Inventory SHA-256:
  `f0360637f303a32bdb7312006a8b8d9e7e8d4f8a3ba09782508f82590a8e16d9`
- Baseline diff SHA-256:
  `ab4f33d4791767d0d07fe1c353db0d67d739b92aad6560df6933a3be24e8fa81`
- Added: 0
- Removed: 0
- Geometry/content changed: 0
- Semantically reclassified: 0
- Unchanged: 4,848
- Reviewed semantics applied: true
- Semantic review required: false
- Provider egress performed: false
- Drawing mutation performed: false

## Refresh report and cleanup

- Refresh report ID: `42e52d3e3d40f47f528c84ae943f9a7e`
- Refresh report file SHA-256:
  `660c8b4f1068e01c4591f2a21ea0fb39bac4952d8659fc06f975dca0824b6036`
- Embedded report SHA-256 validation: passed
- Runtime report:
  `workspace/.ai-engineer/autocad/inventory-refresh/42e52d3e3d40f47f528c84ae943f9a7e.json`

Independent verification confirmed that the original `workspace/lighting.dwg` retained its exact
source SHA-256. The exact isolated drawing was closed without saving after successful acceptance.

## Remaining boundary

This acceptance proves automatic orchestration for an unchanged source drawing. A deliberately
changed and saved isolated DWG must separately prove that stale reviewed semantics are withheld and
`semantic_review_required=true`. The current AutoCAD action still performs a fast complete index;
an event-backed selective recapture path remains future work.
