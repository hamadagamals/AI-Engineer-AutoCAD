# Milestone 22 compliance live acceptance — 2026-08-12

## Scope and authority boundary

This is a functional AutoCAD/API acceptance fixture, not an Egyptian-project compliance
decision. The public US OSHA source is used only to prove exact source/profile/clause/fact
hash binding and visible `PASS`/`FAIL`/`UNKNOWN`/`CONFLICT` behavior. It is not activated
as authority for an Egyptian project.

- Official source: `https://www.osha.gov/laws-regs/regulations/standardnumber/1910/1910.305`
- Final-rule identity/effective-date source:
  `https://www.osha.gov/laws-regs/federalregister/2007-02-14`
- Downloaded official page: 190,785 bytes
- Source SHA-256: `122f15b739dd0e6c015563aa2552e4b9c741da51155b303ae086abdd7f50cda1`
- Reviewed locator: `29 CFR 1910.305(a)(2)(v)(B)`
- Profile SHA-256: `c1391a96bc95c71ccfd9cf28cab4638b446c45fd07fb4f414bc5c7232a1f9629`
- Clause-evidence SHA-256: `6497d0e5b38705f1dccacacc7e488f3ece1f376a1e11ce31e43ad2991dd59335`
- Rule SHA-256: `f1f109e849de0d734921dc3350936be91d7bc72c3d0ec3cde2f8e3b3b66d347e`

The compliance record contains a bounded reviewed paraphrase, not the publication or full
clause text. The downloaded public page stays in the local workspace evidence area.

## Deterministic fixture cases

`integrations.build_m22_live_acceptance` created a canonical manifest and four exact
requests under `workspace/.ai-engineer/m22-live-acceptance/`:

- `PASS` request SHA-256: `08335e1723386da2b8d0b2e48f081a71a5d0dfd6b342596df35c65348909a6d6`
- `FAIL` request SHA-256: `7b307c1e386140c8155665064194e0801f8e02e92e2662bbd7a335036ec9c985`
- `UNKNOWN` request SHA-256: `390dfb85ee60d1cbc191001dd9c75e41fe1fc74936763503e210048ff5e3b405`
- `CONFLICT` request SHA-256: `cafc517f407742c8222a1886c4f9adf8439a859ec8445e7b1b16d07a04de8adf`

The automated acceptance reconstructs every nested typed record from JSON, verifies its
self hash and upstream observation hash, and proves the four expected outcomes. All results
retain `provider_retrieval_performed=false`, `regulatory_approval_performed=false`, and
`drawing_mutation_performed=false`.

## Live status

The AutoCAD 2021 x64 bridge built successfully. The dedicated launcher started the
authenticated API on `127.0.0.1:8765`; `/health` returned version `0.18.0`; AutoCAD opened
with the `Code Compliance` palette and the exact PASS request preloaded for automatic local
evaluation. The operator visually confirmed the exact profile, profile/source/evidence
hashes, edition and clause locator, `Complete: true`, `All requirements satisfied: true`,
the `PASS` finding, and all three negative action flags. The displayed result SHA-256 was
`8d935a9dffd2fbd3c5ef356f6593351c763e35072b45850fd56430c59cc928ab`.
An independent local reconstruction from the unchanged request reproduced that result hash
exactly. Automated reconstruction proves the same display contract for `FAIL`, `UNKNOWN`,
and `CONFLICT`. This evidence is complete. Closing AutoCAD stops the temporary API.

Compileall passed. The full automated baseline is `434 passed, 1 skipped`, with only the
known Starlette deprecation and pytest-cache permission warnings.
