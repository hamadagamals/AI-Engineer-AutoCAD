# Verified Operational Learning

AI-Engineer does not rewrite its own source code or silently promote model output into
engineering behavior. Operational learning is a bounded, reviewable registry of failure
invariants in `data/verified_lessons.json`.

## Lifecycle

1. Capture the failed and corrected artifacts without changing the source drawing.
2. Identify a concrete trigger, root cause, required actions, forbidden actions, and
   reproducible verification requirements.
3. Bind the lesson to immutable SHA-256 evidence and an engineer/date review identity.
4. Reproduce the incident as a regression test.
5. Load the self-hashed registry at application startup and fail closed on tampering.

Lessons never grant permissions, approve products, select manufacturer assets, change a
drawing, or update program code. Registration is append-only through a typed API; an active
lesson ID cannot be overwritten. Changes to code or lesson policy still require normal source
review and tests.

New failures may be accumulated automatically in `data/lesson_candidates.json`. Repeated
scope/trigger/action identities increment one bounded candidate with timestamps and evidence
hashes. Candidate status is always `candidate_only`; no automatic route exists from this journal
to the active registry or to source-code modification.

## CAD rules captured on 2026-08-24

- DXF must be generated with the pinned standards-aware writer and pass both independent
  audit and native AutoCAD reopen acceptance. Handwritten serialization is forbidden.
- STEP must use the fixed `IMPORT` command with `FILEDIA=0` through AutoCAD Core Console.
  ActiveX STEP import and interactive/modal automation are forbidden.
- Manufacturer CAD files match BOM commercial references exactly after normalization and are
  deduplicated by SHA-256. Similar filenames remain unbound rather than substituted.
- Drawing handoff is bound to the full path and hash, expected instance counts, `DBMOD=0`, and
  unchanged source identity; the tab name alone is insufficient.

The builder `python -m integrations.build_verified_lessons_seed .` deterministically recreates
the reviewed seed. Tests validate self-hashes, append-only behavior, exact CAD matching,
non-interactive STEP planning, completion evidence, and hybrid postchecks.
