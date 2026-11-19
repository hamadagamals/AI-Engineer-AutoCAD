# AutoCAD 2023 destination live acceptance - 2026-09-25

## Scope

This acceptance covers the second initialized Windows profile required by the portable
AutoCAD 2023 candidate: local readiness, a native plugin build against installed Autodesk
assemblies, exact read-only document status, and one bounded full-drawing context capture.
It does not authorize drawing mutation, component moves, provider egress, or performance claims.

## Readiness

- Product: AutoCAD 2023 English `24.2.53.0`; expected `ACADVER` family `24.2`.
- Python `3.13.15`; .NET Framework references `v4.8`; Autodesk files `6/6`.
- Readiness report: `workspace/reports/autocad-2023-readiness-20260925-231015.json`.
- Report SHA-256: `34f69902b2a2027b65fa34861e6824246ca8813671cc016fec9f78a99c67a9f6`.
- Destination DLL SHA-256: `51246b09713476af7a826531ca98365dd13f3f44761a0da90a8ad191efa06ca7`.
- Focused verification: `69 passed` for the AutoCAD and release-readiness test modules.

The automatic `/b` launch failed closed because AutoCAD did not expose the reviewed
document before timeout. The launcher stopped its processes. The documented
`-ManualPluginLoad` fallback then opened the visible read-only copy and loaded the plugin.

## Live evidence

- Source: `workspace/review-drawings/HUTCHISON_PANEL_GA_PREVIEW_R03.dwg`.
- Disposable copy: `workspace/autocad-preview/HUTCHISON_PANEL_GA_PREVIEW_R03 - Read Only 20260925-231320.dwg`.
- Source/copy SHA-256: `e0bf950ad6e204b9760c0a42bfa0da289ccb7ee730239c1590e6901bdec4bb04`.
- The disposable file had the Windows read-only attribute set.
- Document ID: `b9075b7b-23b8-1e43-965f-07b427045bca`.
- Status: `database_modified=0`, `read_only_channel_ready=true`,
  and `drawing_mutation_performed=false`.
- Request ID/SHA-256: `fcb6ffad4f424e488c492e7a3fa6e574` /
  `26cc417b2bb78cab5b04a4f2dee8b744c3267471ca07e6edcd956ee77e858b5e`.
- Result ID/SHA-256: `09eb87ebbb204a6689e307bda4cbd61c` /
  `c7afc95abd0ecfb7a3c3ca60d7ecf165a8ea90b66e992844a07af76bb6e436c7`.
- Context ID/SHA-256: `d0f5c6f349bb465a931a7cb5b9acb081` /
  `5c0055558b5c4c7edd272f4a0fa6b6551d70f3b5fdf7def24dd20d6f49bc5a1b`.
- Capture: 410 entities in 86.05 ms; `DBMOD=0 -> 0`; mutation `false`.

## Result

The destination AutoCAD 2023 read-only status/context gate passes. AutoCAD 2021 remains
unchanged. The second-profile performance matrix, visual palette review, provider acceptance,
Scratch mutation/recovery, and component-move execution remain separate open gates.
