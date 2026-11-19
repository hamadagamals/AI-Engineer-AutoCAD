# AutoCAD maximum Scratch live acceptance — candidate r40 — 2026-08-24

## Scope and safety boundary

The current-profile release gate required execution, verification, and recovery-evidence timing for
the fixed maximum 1,792-object fixture. Candidate r40 implements this as a non-production command;
it does not raise the existing bounded circuit, route, or symbol limits. The command accepts only a
workspace source path and its exact reviewed SHA-256. Geometry, layer, script, executable, count,
and output path are fixed in source code. The result is explicitly ineligible for drawing handoff.

The engineer separately approved the exact r40 1,792-object Scratch request. The first stored
approval expired before use and performed no execution. A new approval for the unchanged task and
payload was issued and consumed once:

- Task ID: `083e0d14fd8b4d7b959f5de2e1622626`
- Consumed approval ID: `371a6ea58504446fa64774341c9b114b`
- AutoCAD run ID: `b7ea2f39c5c9468ab930484c7e9773eb`
- Fixture profile: `AIENG_MAX_EXECUTION_V1`

## Package and environment

- Archive: `dist/ai-engineer-0.18.0-autocad-beta-candidate-20260824-r40.zip`
- Archive bytes: 658,351
- ZIP entries: 173
- Archive SHA-256: `2c0fa7098c06c7029c382e0a39229d58dd72645c9b465e8253db835ce0c27a5f`
- Clean short installation: `r40`
- Installed product check: version `0.18.0`, Python `3.13.14`, 18 AutoCAD product files
- Installed DLL SHA-256: `0fc4f14741b4a41c0bd1cacdd91856ab83fec5a990204eedbe8a521921dbe206`
- AutoCAD/Core Console: 2021 / `ACADVER=24.0`
- Platform: Windows 11 `10.0.22631`, 12 logical CPUs, 34,157,531,136 bytes memory

## Exact source and result

- Original `workspace/lighting.dwg` SHA-256:
  `1d08ccb0557e740f7d471e006d5accad0675d54b82e45d9aabc64e239ecfb117`
- Reviewed installed source-copy SHA-256: same
- Source-copy bytes: 3,323,467
- Entities before: 4,848
- Fixed created objects: 1,792 `LINE` entities
- Reserved layer: `AIENG-PERF-FIXTURE`, ACI 8, `CONTINUOUS`
- Layer pre-existing: false
- Entities after: 6,640
- Scratch output bytes: 3,401,167
- Scratch output SHA-256:
  `13283a1b15c676d66d998e807d631a0adb9dc62f655fe5ba34e42b31df5b825e`
- `DBMOD`: 0
- Source unchanged: true
- Scratch changed: true
- Provider egress: false

The exact console markers reported `OBJECTCOUNT=1792`, `VALIDCOUNT=1792`,
`LAYEROBJECTCOUNT=1792`, `BEFORECOUNT=4848`, `AFTERCOUNT=6640`, `DBMOD=0`, and `DONE=1`.

## Timing and recovery

- Core Console execution: `2.4976913 s`
- Recovery-evidence creation: `0.0120208 s`
- Total source-copy/execution/verification/recovery path: `2.5679253 s`
- Required maximum target: `<= 30 s`
- Target satisfied: true
- Recovery-evidence SHA-256:
  `05b8ad0b64d958e6fa3b4458f90e5f0638a1cd9135d625b3ec0a78dcf0461716`
- Recovery strategy: retain unchanged source and exact verified Scratch output
- `handoff_allowed`: false

## Automated verification

- Compile: exit 0
- Targeted tests: `86 passed`
- Full official suite: `545 passed, 1 skipped` (excluding the unowned
  `tests/test_project_config.py`)
- Installed five-run local-core result SHA-256:
  `2bf184c864c3d87a27d27081f862fe76678bc762c9309a03cae536bac0c0466a`
- Installed benchmark-file SHA-256:
  `416fcd2504a3801eaedd9ebbf32d0d3d0e946563ccd43862fe8c2b0179a9dba6`
- All six installed local-core targets satisfied: true

No AutoCAD or Core Console process remained after the run. The only remaining performance-release
gate is repetition on a second initialized Windows/AutoCAD 2021 user profile.
