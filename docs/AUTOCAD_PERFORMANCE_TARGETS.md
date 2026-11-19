# AutoCAD beta performance targets

## Scope

These are release gates, not marketing claims. Measurements must use the destination
Windows profile, AutoCAD 2021, the packaged build, a warm second run, and the exact
fixture/result hashes recorded in evidence. Provider and manufacturer network latency is
excluded because it is not deterministic and cannot block local drafting work.

## Required measurements

| Operation | Small fixture | Maximum supported fixture | Target |
| --- | ---: | ---: | ---: |
| Read-only context capture and local import | 50 entities | 512 entities, 128 layers | <= 2 s / <= 8 s |
| Deterministic proposal validation and preview display | 10 objects | 1,792 ordered preview items | <= 1 s / <= 5 s |
| LV calculation and result hashing | 1 load, 1 route | 256 reviewed loads and 256 routes | <= 0.5 s / <= 3 s |
| Scratch execution verification and recovery-evidence creation | 10 objects | 1,792 objects | <= 5 s / <= 30 s |

The existing 30-second context-capture limit and payload/entity limits remain hard safety
bounds. A target miss must report the measured duration and remain a release finding; it
must not silently increase limits or omit unsupported objects.

## Measurement rules

- Record PC CPU, memory, storage type, Windows build, AutoCAD build, Python version, package
  SHA-256, drawing SHA-256, fixture size, warm/cold state, and five-run median plus maximum.
- Confirm source drawing SHA-256 and `DBMOD` remain unchanged for read-only context/preview.
- Execute only against a copied scratch drawing after exact preview and one-use approval.
- Verify output, recovery evidence, and source identities after every execution measurement.
- Treat cancellation responsiveness separately: a visible cancel request must be acknowledged
  within two seconds, while safe process termination may take longer.

## Current status

Targets are defined and automated contract coverage preserves their numbers. The reproducible
`autocad-benchmark` local-core trial passed five-run small and maximum parsing, preview-hashing,
and LV-calculation targets on the destination PC; exact results are in
`docs/evidence/autocad-core-performance-2026-08-13.md`. One live `lighting.dwg` copied-Scratch line
execution/recovery passed in 2.1374 seconds with exact unchanged-source and changed-output hashes.
Packaged context/palette timing and live maximum-fixture Scratch timing now pass on the current
profile; the second-profile matrix remains required before beta freeze. The complete release gate is not
yet claimed.

A current-profile live operator-channel capture on 2026-08-16 reached the 512-entity limit in
`26.5282 ms`, observed nine layers, reported `DBMOD=0 -> 0`, and retained the source SHA-256. The
separate maximum local import trial remains approximately 21 ms, so capture plus import is within
the 8-second target. Status and palette-display actions also completed automatically, and the
engineer visually confirmed the AI-Engineer tab and click-to-open palette behavior.

Current-profile cooperative cancellation also passed live. Capture request
`17a946ac5a994e20a67f4a38e31b2756` was bound to the same drawing fingerprint and source hash;
its exact cancellation was acknowledged with `status=cancelled` in `619.1593 ms`, below the
two-second target. No context or partial payload was published, mutation remained false, and the
follow-up status result SHA-256
`77a0ae7015bea62b70e7ff40aff733e1cb8522c421d3b109c5274bf6666b4220` confirmed
`DBMOD=0 -> 0`. The project drive is on a healthy Micron 2200S NVMe SSD; the second detected
physical drive is also a healthy NVMe SSD.

The source-tree chunked full-drawing index additionally passed live on 2026-08-23. The plugin
discovered `lighting.dwg` locally and captured all 4,848 top-level entities into 76 adaptive
hash-bound chunks in `1,118.8101 ms`, after which independent import verified 4,848 unique
handles and `DBMOD=0 -> 0`. This exceeds the former 512-entity interaction scope without
raising its per-context output bound. It is useful real-project evidence but does not replace
the required packaged-build and second-profile performance gates.

Portable candidate r28 also passed the installed five-run local benchmark. Maximum observed times
were `0.0208095 s` for importing 512 context entities, `0.0419501 s` for hashing a 1,792-item
preview, and `0.0133074 s` for 256 deterministic LV calculations. Result SHA-256 is
`199389cb0af1d0b3a74dd7ed422667df65e96722a67820dcfc946e06ab6e730a`; the benchmark-file
SHA-256 is `dcf179f239400ba78b0233fdf981e81459319289811a7334070e74ad3b2b7a08`.
These installed local-core results pass their six targets, but the complete release performance
gate remains open pending packaged live AutoCAD context/palette timing, maximum Scratch execution,
and the second initialized Windows/AutoCAD profile.

Candidate r29 repeated the clean-installed five-run benchmark after correcting runtime-directory
creation. Its maximum observations were `0.0204777 s` for 512 context entities, `0.0405603 s` for
1,792 preview items, and `0.0134368 s` for 256 LV calculations. Result SHA-256 is
`5869583df19e1a5a665a86cd4ba9ca5808fae2bee07554f6751bdd22e84e223e`; benchmark-file SHA-256
is `4e22e118e91f16a63567251d649bf0bda09ded6dd6040ba229728a2ab1782068`. The remaining live
release gates are unchanged.

Candidate r39 closes the current-profile packaged context and palette gates. From the clean,
short-path installation, AutoCAD captured all 4,848 top-level `lighting.dwg` entities in 76
validated chunks in `1.2566212 s`, below the eight-second maximum-context target. The exact
`show_palette` operator round trip completed in approximately `0.20 s`, the engineer visually
confirmed the palette, and status/capture/palette results all retained `DBMOD=0 -> 0` with drawing
mutation false. The copied read-only acceptance drawing closed without saving and the source SHA-256
remained `1d08ccb0557e740f7d471e006d5accad0675d54b82e45d9aabc64e239ecfb117`.

r39's installed five-run local-core benchmark also passed all six targets. Maximum observations
were `0.0266743 s` for 512 context entities, `0.0610043 s` for 1,792 preview items, and
`0.0134119 s` for 256 deterministic LV calculations. Result SHA-256 is
`b2b8c5de0c8b4ca93fb5815f7060e9ed8c30a00db697df1c3e2388dc1f08e9b7`; benchmark-file SHA-256
is `49dbbb74f9670320072c3bbba2bf8a468a22627b4ff5eeb0f2d932be5e912e56`. The complete release
gate remains open only for the second initialized Windows/AutoCAD profile.

Candidate r40 closes the current-profile maximum Scratch execution/recovery gate without increasing
any production drafting bound. Its separately approved, fixed `AIENG_MAX_EXECUTION_V1` operation
copied the exact 4,848-entity `lighting.dwg` source, created exactly 1,792 `LINE` entities on a
previously absent reserved layer, and saved a 6,640-entity Scratch output at `DBMOD=0`. Core Console
execution took `2.4976913 s`; source copy, execution, verification, and self-hashed recovery evidence
took `2.5679253 s`, below the 30-second maximum target. Source SHA-256 remained
`1d08ccb0557e740f7d471e006d5accad0675d54b82e45d9aabc64e239ecfb117`; output SHA-256 is
`13283a1b15c676d66d998e807d631a0adb9dc62f655fe5ba34e42b31df5b825e`; recovery-evidence SHA-256
is `05b8ad0b64d958e6fa3b4458f90e5f0638a1cd9135d625b3ec0a78dcf0461716`. The evidence sets
`handoff_allowed=false`, so this release fixture cannot be delivered to a project.

r40 archive SHA-256 is `2c0fa7098c06c7029c382e0a39229d58dd72645c9b465e8253db835ce0c27a5f`.
Its installed five-run local-core benchmark again passed all six targets with result SHA-256
`2bf184c864c3d87a27d27081f862fe76678bc762c9309a03cae536bac0c0466a` and benchmark-file SHA-256
`416fcd2504a3801eaedd9ebbf32d0d3d0e946563ccd43862fe8c2b0179a9dba6`. The only remaining
release-performance gate is the second initialized Windows/AutoCAD profile.
