# M20 Protective-Conductor Live Acceptance

Date: 2026-08-11

AutoCAD 2021 completed the read-only
`m20.protective_conductor_adiabatic.v1` acceptance on route 1. The calculation
used the exact server-recalculated short-circuit result and explicit reviewed
project inputs; it did not use an embedded jurisdiction-specific table.

- Result SHA-256: `b69287cd88b700b520e19b2daa5cec0646018f954f32ed4fb354fc8cc36abb3b`
- Short-circuit result SHA-256:
  `4d0dde8483b95e387e8e186ae9bcb97a4b9423579997a2ca482803283b41f30d`
- Rule-source SHA-256:
  `bccee1183336a9d06cc142d74820c337851643b6d77b33f9ff2be2f532fe3a20`
- Source and disposable-copy SHA-256:
  `4b9b3af7f0091c5392896087a196332c3bf5f048487720d9b2ab5c2f90cd8535`
- Outcome: `CALCULATED`
- Fault current: 2434.322478 A
- Disconnection time: 0.2 s
- Reviewed conductor factor: 115 A*sqrt(s)/mm2
- Required adiabatic cross-section: 9.466627 mm2
- `conductor_selection_performed=false`
- `compliance_evaluation_performed=false`
- `drawing_mutation_performed=false`

The rule source, applicability, time, and conductor factor remain reviewed project
inputs whose external authority is not verified here. The result does not select a
standard conductor or evaluate minimum size, temperatures, mechanical constraints,
touch voltage, loop impedance, coordination, or standards compliance.
