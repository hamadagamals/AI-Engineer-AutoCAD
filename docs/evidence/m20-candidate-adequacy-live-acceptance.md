# M20 Candidate Adequacy Live Acceptance

Date: 2026-08-11

AutoCAD 2021 completed the read-only `m20.candidate_adequacy.v1` acceptance against
the recognized four-device/four-route context. The reviewed candidate was a 10 A,
6 kA protective device on route 1.

- Result SHA-256: `4695740c2634ba899ab77a3fc70e7aad9a0542288fedb251682808ed8d8eca42`
- Low-voltage result SHA-256: `31425925efbb6447b1cc417808872b9c81d3017adfd5149d1faafacb60f059c5`
- Ampacity result SHA-256: `91ee080df25bb3ee7ba8edaafbd62fc3331a255c7d1f6023d70510a4ba7bb858`
- Short-circuit result SHA-256: `8ea4f5ec79e2192996e4ff2bb2dbf26b204ccd1de437dbfcb951344cfc669d39`
- Source and disposable-copy SHA-256:
  `4b9b3af7f0091c5392896087a196332c3bf5f048487720d9b2ab5c2f90cd8535`
- Source modification: false
- Disposable-copy modification: false
- `automatic_selection_performed=false`
- `compliance_evaluation_performed=false`
- `drawing_mutation_performed=false`

All five explicit reviewed-project findings passed:

1. Derated ampacity 15.04 A was at least the required 6.356471 A.
2. Voltage drop 0.059288 percent was at most the reviewed 5 percent limit.
3. Device rating 10 A was not below design current 5.085177 A.
4. Device rating 10 A was not above derated ampacity 15.04 A.
5. Reviewed breaking capacity 6000 A was not below 2434.322478 A RMS fault current.

PASS applies only to this candidate and these reviewed inputs. It is not automatic
selection, manufacturer verification, or a standards-compliance approval.
