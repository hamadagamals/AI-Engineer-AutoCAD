# AutoCAD selective-delta live acceptance — 2026-08-23

## Boundary

The engineer separately approved one exact live add/modify/delete trial. All persistent changes were
limited to a disposable writable Scratch acceptance copy derived from managed run
`88d4636c82054ba1b27cf70542c096f9`. The project source `workspace/lighting.dwg` was never opened for
write and retained SHA-256
`1d08ccb0557e740f7d471e006d5accad0675d54b82e45d9aabc64e239ecfb117`.

The launcher accepts writable mode only for an exact `source.dwg` below the managed
`.ai-engineer/autocad/runs` directory. Its default Preview behavior remains read-only.

## Baseline

- Scratch fixture SHA-256: `f9d4dce27047057683a668ea2ff06f1f399392419d2fc2de5fac52c4cdae02b7`
- Full index ID: `5c674aca8eff4b699cd219d8dd1a0030`
- Full index SHA-256: `26b641cc9f62fb38726fe74f1cb27f98df830ddc6773e2144c2b02a3119a564e`
- Baseline entity count: 4,851
- Full-index duration: 1,020.8036 ms
- Baseline DBMOD: `0 -> 0`
- Inventory-refresh report: `14c10d63f400727ca8b2378ab7d56c55`

The fixture contained three deliberately isolated `LINE` entities. Exact handles and coordinates
were validated before mutation.

## Approved Scratch operation and result

- Modified handle: `1A1C51F4`
- Removed handle: `1A1C520C`
- Added handle: `1A1C523C`
- Unchanged control handle: `1A1C5224`
- Saved current Scratch SHA-256: `76cc157239f07c354c8cbab5cb6c63f7afd416d6395a7db43b425d3d561789f0`
- Saved DBMOD before delta capture: `0`

Delta `533ca85d68a04501b1545146ce8dc8f8` completed with SHA-256
`6799abd00f538631463a6169211b160fbfd34ca06e45b1b51830a72660bbce1a`.
It reported exactly:

- `added_object_ids = [1A1C523C]`
- `modified_object_ids = [1A1C51F4]`
- `removed_object_ids = [1A1C520C]`
- candidate count: 3
- exported live context entities: 2 in one hash-bound shard
- unchanged control reported: false
- delta duration: 25.6422 ms
- delta DBMOD: `0 -> 0`
- drawing mutation performed by capture: false
- operator-result SHA-256: `01651b51f616ab786a1a5907014c4030b04a237862fc9f898b9e0c6413635560`

## Retained diagnostic and correction

The first retained attempt correctly failed closed with `No in-session drawing-index baseline is
armed`. Diagnosis showed that the baseline stored a managed `Database` wrapper and later used
reference equality; AutoCAD can expose another wrapper for the same native database. The tracker now
stores and compares `Database.UnmanagedObject`, while retaining the managed database only for event
subscription cleanup. The rebuilt AutoCAD 2021 x64 DLL and targeted tests passed before the clean
repeat.

## Outcome

The event journal, one-use delta command, strict loader, exact changed-handle partition, no-save-noise
behavior, and clean DBMOD boundary are live accepted.

## Inventory v2 composition

The accepted delta was composed locally against baseline inventory
`53647b671c70678177eb73b74709fc3b`, SHA-256
`2ed048f95b47b413d586e764a633d1f65b7a8bc91733df0c1da1fc9e1d382c23`. The independently reloaded
result was:

- inventory ID: `af3ca94266f22a7ce8ea02ac5c4d9d59`
- inventory content SHA-256: `7d956d2d5585ce226755e015db49a0524e45e856ec7df4187223b07b7e8ef4d2`
- schema/version: `2 / autocad.drawing_inventory.v2`
- entity count: 4,851
- exact comparison: added `1A1C523C`, removed `1A1C520C`, changed `1A1C51F4`
- reclassified count: 0
- unchanged count: 4,849
- semantic review required: true
- reviewed convention/profile SHA-256 retained: no
- reviewed topology/membership SHA-256 retained: no
- drawing mutation performed: false

The v2 lineage contains the exact baseline inventory SHA-256 and delta ID/SHA-256. Only catalog-stable
meaning is recalculated; source-bound reviewed meaning is never silently copied to the changed source.
