# AutoCAD local-core performance evidence — 2026-08-13

## Scope and identity

This is a five-run synthetic measurement of deterministic local parsing, preview hashing, and
low-voltage calculation. It starts no provider or AutoCAD process, writes no drawing, and does not
claim UI, context-capture, scratch-execution, or recovery performance.

Command:

```powershell
python launcher.py autocad-benchmark --runs 5 --output performance\autocad-core-20260813-v1.json
```

- Report: `workspace/performance/autocad-core-20260813-v1.json`
- Report file SHA-256: `48853853e133e4b016d4551b7cee51893bb6c0087beef11c6adfde1744fe7e15`
- Result SHA-256: `b2a2be7159c38bff508cc06e10622a4dcee2c050050f3e7e9170db15bebabf97`
- Windows: `Windows-11-10.0.22631-SP0`
- Python: `3.13.14`
- CPU: `Intel64 Family 6 Model 158 Stepping 13, GenuineIntel`, 12 logical CPUs
- Memory: `34157531136` bytes
- `provider_egress_performed=false`
- `drawing_mutation_performed=false`

## Results

| Operation | Fixture | Median | Maximum | Target | Local result |
| --- | --- | ---: | ---: | ---: | --- |
| Context parse/import | 50 entities, 8 layers | 0.002217 s | 0.002546 s | 2 s | PASS |
| Context parse/import | 512 entities, 128 layers | 0.021062 s | 0.021759 s | 8 s | PASS |
| Preview creation/hash | 10 objects | 0.000384 s | 0.000399 s | 1 s | PASS |
| Preview creation/hash | 1,792 objects | 0.043177 s | 0.074646 s | 5 s | PASS |
| LV calculation/hash | 1 load object, 1 route | 0.000619 s | 0.000644 s | 0.5 s | PASS |
| LV calculation/hash | 256 load objects, 256 routes | 0.014420 s | 0.020145 s | 3 s | PASS |

Every repeated operation produced the same output identity. The report records
`all_local_targets_satisfied=true` but deliberately records
`release_performance_gate_complete=false`.

## Remaining live measurements

- AutoCAD context capture before local parsing, including source drawing SHA-256 and `DBMOD`.
- Rendering and interaction time in the AutoCAD preview palette.
- Approved copied-scratch execution, verification, recovery-evidence creation, and cancellation.
- Storage type, exact package SHA-256, and exact drawing SHA-256 for the representative live run.

Those live results must use the packaged build and five-run small/maximum fixtures before the
release performance queue item can close.
