# AutoCAD daily install — current PC — 2026-08-24

## Scope

Prepare the tested r40 portable candidate as the operator's current-PC working copy while
leaving the separate experimental installation and the original project drawing untouched.
Second-profile and second-PC release acceptance remains deferred.

## Installed copy

- Install root: `D:\AI-Engineer`
- Working drawing: `D:\AI-Engineer\workspace\lighting-working.dwg`
- Original and working-copy SHA-256:
  `1d08ccb0557e740f7d471e006d5accad0675d54b82e45d9aabc64e239ecfb117`
- Product version: `0.18.0`
- AutoCAD readiness: passed for all 18 required product files.
- Installed AutoCAD 2021 x64 context bridge: compiled successfully.
- Local bridge: authenticated loopback on `127.0.0.1:8765`.

## Live no-mutation acceptance

AutoCAD opened a disposable read-only copy of `lighting-working.dwg`. The exact
SHA-bound operator channel displayed the palette and then captured the complete drawing
index without manual 500-entity selections.

- Document ID: `476310dc-0eb2-4b04-8b70-77e670dcf581`
- Full-index request ID: `9af470c1d5bc4e2e80ee01d9ad06a3b0`
- Result ID: `ab5fb30735534c7caa13d3b4d5e10e9c`
- Index ID: `bbc940194f1f407ab8231386d9319029`
- Index SHA-256: `51c7936e893ade85e7da5335e1d6f721afc48c18498bc015bae3e156e2cd8dc9`
- Captured entities: `4,848 / 4,848`
- Validated chunks: `76`
- Capture duration: `1,247.7822 ms`
- Complete: `true`
- DBMOD: `0 -> 0`
- Drawing mutation performed: `false`

## Operating boundary

This installation is ready for daily read-only whole-drawing inspection, inventory,
copilot review, deterministic calculations when their inputs are complete, and reviewed
preview workflows. AI-authored drawing output remains restricted to copied Scratch,
recovery evidence, verification, and separately reviewed handoff; this acceptance does
not authorize direct mutation of the active project drawing.

