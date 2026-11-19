# AutoCAD changed-source inventory-refresh live acceptance — 2026-08-23

## Scope and approval

The engineer separately approved one bounded drawing mutation for this acceptance. The existing
supported `autocad.create_line_scratch` path copied `workspace/lighting.dwg` into a UUID run
directory and created exactly one LINE outside the project area. The original drawing was never
opened for write or replaced. AutoCAD Core Console saved only the Scratch copy, which was then
opened through the isolated read-only launcher for automatic inventory refresh.

## Source and Scratch mutation evidence

- Original source SHA-256:
  `1d08ccb0557e740f7d471e006d5accad0675d54b82e45d9aabc64e239ecfb117`
- Scratch execution run ID: `77f52514dcbe4c24b29bef0adf63e0e7`
- Scratch output SHA-256:
  `2ea2913f1ee2538dffe4e68327b2ec34e2c7d2329050f4267caf1f718acc44c9`
- Created type: `LINE`
- Fixed endpoints: `(-1000000,-1000000,0)` to `(-999000,-1000000,0)`
- Entity count: `4,848 -> 4,849`
- Scratch execution duration: 2.3136359 s
- Scratch saved DBMOD: 0
- Source unchanged: true
- Scratch changed: true

The output and recovery identity are retained under
`workspace/.ai-engineer/autocad/runs/77f52514dcbe4c24b29bef0adf63e0e7`.

## Automatic refresh evidence

- Isolated drawing: `source - Read Only 20260823-145824.dwg`
- Document ID: `476310dc-0eb2-4b04-8b70-77e670dcf581`
- Operator request ID: `127fdc153a004f73a95f40112b5da3c9`
- Operator result ID: `19f5f0e9bc5d408293c39f17688c0114`
- Operator result SHA-256:
  `4b1f43622f89817432095de552373784a42d50f9962ef884e476e8c589043256`
- Index ID: `9a8bcdc7e77841309c1e76f0a5a07a21`
- Index SHA-256:
  `32f48bcd9b1b3d54ec0cc70abcf488fc784345cb8a372bd7c0c9a6a6e992d1b5`
- Indexed entities: 4,849
- Index duration: 1,021.6587 ms
- DBMOD: `0 -> 0`
- Drawing mutation during refresh: false

## Accepted comparison and semantic invalidation

- Refresh report ID: `33d7ac21aad053930d476b87691098d7`
- Refresh report file SHA-256:
  `102fd8fbcfa14c204b93d5059ad8dafc290c60f83676bb0f4fcc7528a15f5162`
- Embedded report hash validation: passed
- Inventory ID: `e9fdc09143d859a113168edb49bd9ec1`
- Inventory SHA-256:
  `a365644d302b1000fb45a30b383644e85c97b60148deadbfa940d50decccf914`
- Diff SHA-256:
  `bf3db1672a239668efb09d18cc3dd00cd5c13bb5f97462aec940646fb7fe9488`
- Added: 1 — exact handle `1A1C51F4`
- Removed: 0
- Geometry/content changed: 0
- Reclassified/withheld source-bound semantics: 991
- Structurally unchanged: 3,857
- Reviewed semantics applied: false
- Semantic review required: true
- Provider egress performed: false
- Drawing mutation performed by refresh: false

The 991 invalidated semantic observations exactly cover the previous 336 reviewed devices, seven
reviewed routes, two reviewed annotations, and 646 reviewed route candidates. The coordinator did
not silently rebind any of them to the changed source SHA-256.

## Cleanup and remaining boundary

The read-only isolated AutoCAD copy closed without saving, AutoCAD exited, and port 8766 stopped.
Independent final hashing confirmed both the original and Scratch hashes remained exact. This
acceptance proves changed-source detection and safe semantic invalidation. The current refresh still
captures a complete index; event-backed selective recapture remains the next performance feature.
