# Milestone 23 official discovery live acceptance

## Scope

This is a functional AutoCAD/API acceptance fixture for the production manufacturer-search
boundary. It is not an active-project design, product approval, procurement decision, stock
claim, catalog registration, or authorization to modify a drawing.

The fixed request asks for at least two distinct official product candidates for a DIN-rail
modular socket outlet with reviewed functional inputs of at least 16 A, at least 230 V, and
exact `2P+E`. Hosted search is restricted to `www.legrand.com` and `www.se.com`. A candidate
is accepted either after AI-Engineer independently re-fetches the exact official page or,
when transparent retrieval is refused, after the engineer saves the exact page in the bounded
workspace. The verifier locates the part number and quoted property evidence in the exact bytes
and computes the source SHA-256 locally. The saved-page path is explicitly identified as
engineer-supplied evidence rather than provider retrieval.

## Reproducible artifacts

Run:

```powershell
python -m integrations.build_m23_discovery_live_acceptance .
```

Generated under `.ai-engineer/manufacturer/live-acceptance/discovery/<reviewed-date>/`.
Date-separated evidence prevents a new reviewed request from overwriting or conflicting with a
prior day's immutable acceptance artifacts:

- `source-evidence.json`
- `manufacturer-discovery-request.json`
- `manifest.json`

Current fixed identities:

- Discovery request SHA-256:
  `e50114bf6d655f1e12312508eb2392f4643baf81f329763560c13dff63e77500`
- Source-evidence file SHA-256:
  `83876076552d7c0b062e2c5a6a1bb5534252400322ee539cde54d8abf4d1ab36`
- Request file SHA-256:
  `9e44f48628f5e619272f577e04d6d901d9673f1a3a02f95d41f49970cdd8a4a4`
- Manifest SHA-256:
  `2a9d9c71623616c836f452de4ed32e97f7e7970acfd8fccb364e964bd541ac6b`

## Interactive acceptance

Run `START-AI-ENGINEER-AUTOCAD-DISCOVERY.ps1` and choose `FREE`, `ECONOMY`, or `QUALITY`.
For a paid run, the launcher:

1. requires the operator to type explicit `YES` (case-insensitive) or `نعم` before any
   provider egress;
2. reads the OpenAI API key as a secure prompt and never stores it in the repository;
3. starts the authenticated API with developer/copilot provider mode `none` and enables only
   the separate manufacturer-discovery adapter;
4. removes the key from the parent environment before AutoCAD starts, so AutoCAD and the
   palette never receive it;
5. preloads the exact reviewed request into `Product Discovery`;
6. stops the temporary API when AutoCAD closes.

`ECONOMY` selects `gpt-5.6-luna` with low reasoning. `QUALITY` selects `gpt-5.6` with
high reasoning. The exact model is embedded in the signed result's `provider_name`. Successfully
validated candidates are cached by the exact approved request SHA-256 for the API session, so a
repeated click on the same request does not issue another paid provider call. A changed request
hash is a separate call, and failures are not cached.

Acceptance passes only when the palette visibly shows at least two independently verified
candidates with distinct part numbers, exact official URLs and local source hashes; displays
the requirement findings; and records one engineer choice for separate follow-on review.
The selection must show:

```text
product_approval_performed=false
automatic_download_performed=false
catalog_registration_performed=false
drawing_mutation_performed=false
```

## Browser-assisted Free acceptance — passed 2026-08-12

Transparent requests were not bypassed after Legrand returned HTTP 403 and Schneider returned
HTTP 503. The operator saved both official product pages through the normal browser into the
configured project workspace and selected `free-candidates.template.json` in AutoCAD 2021.
The palette visibly displayed two candidates, and the operator recorded Legrand `004280` for
separate follow-on review. The palette visibly confirmed that the product was not approved or
downloaded.

- Request SHA-256: `e50114bf6d655f1e12312508eb2392f4643baf81f329763560c13dff63e77500`
- Result SHA-256: `b85eb91982c9554f79bfb4ba39056a02be054fe90ef0fdf209033c045627f107`
- Legrand `004280` exact saved HTML SHA-256:
  `68df410a0f8ac7c15f201873712654e65af8988ee546a416fe9916b9b4017298`
- Schneider `A9A15306` exact saved HTML SHA-256:
  `3150c9a76afbf15e456abd07ef75f25043fc28ded88ee1d91cc92d3a9bf43098`
- Final Legrand candidate ID: `candidate:f1da9034159f5358a8f1e59c`
- Final selection SHA-256: `44cfee7f0c5029ccd263e6abb652f325a5abfb18d2fdbc4ebdd9985cbeaeefa4`
- Both candidates matched three properties: rated current, rated voltage, and pole configuration.

The authenticated logs recorded `candidate_count=2`, `engineer_saved_evidence_read=true`, and:

```text
official_source_retrieval_performed=false
provider_egress_performed=false
product_approval_performed=false
automatic_selection_performed=false
automatic_download_performed=false
catalog_registration_performed=false
drawing_mutation_performed=false
```

This passes the browser-assisted Free path only. Paid hosted-search live acceptance remains a
separate optional queue item when funded API credit is available.

For the first funded run, use Economy:

```powershell
.\START-AI-ENGINEER-AUTOCAD-DISCOVERY.ps1 -SearchMode Economy
```

Confirm the copied result reports `provider_name=openai-official-search:gpt-5.6-luna`, at least
two candidates, and all automatic follow-on flags false. Use Quality only if Economy evidence is
insufficient; it is not required merely to repeat an already successful identical request.

### First funded Economy attempt — access refusal, safely stopped 2026-08-13

The OpenAI request completed, but transparent local retrieval of a returned official product page
was refused with HTTP 403. AI-Engineer returned HTTP 400 and displayed no unverified candidate;
no product, download, catalog, or drawing action followed. Browser-like retry or access-control
bypass was not attempted.

The reviewed retry path now permits only identities actually returned by paid discovery to be
matched against the already engineer-saved official HTML evidence. Those bytes are revalidated and
rehashed locally. Its provider identity ends `:saved-evidence`, honestly distinguishing this route
from fresh automated retrieval. This fallback does not accept an unmatched product or weaken any
property check. A second paid call is required because the failed provider response was deliberately
not persisted.

### Funded Economy saved-evidence retry — valid but insufficient 2026-08-13

The safe fallback completed with one locally revalidated candidate, so it did not meet the fixed
minimum of two candidates and is not the final paid acceptance result.

- Request SHA-256: `a9abff610c2bfd6bfc7edc838c825851511ec5f9c2527b804e4eb118e455823f`
- Result SHA-256: `61d66cf3c7c35d1fed3407d82a34b6da136f9de4def93f1a6bdd7cedfe1f24e2`
- Provider: `openai-official-search:gpt-5.6-luna:saved-evidence`
- Candidate: Legrand `004280`; all three mandatory requirements `PASS`
- `provider_egress_performed=true`
- `automatic_selection_performed=false`
- `automatic_download_performed=false`
- `catalog_registration_performed=false`
- `drawing_mutation_performed=false`

No engineer choice was recorded from this incomplete candidate set. A separately approved Quality
run is required; this result must not be described as satisfying the two-candidate gate.

### Funded Quality acceptance — passed 2026-08-13

The separately approved Quality run completed with two distinct paid-discovery identities matched
to, revalidated against, and rehashed from the already saved official HTML evidence. The initial
palette request timed out after 45 seconds while the bounded server operation continued; the server
completed in approximately 112 seconds. Repeating the exact request returned the completed in-process
cache immediately, and the log confirmed `manufacturer_provider_cache_hit`, so no duplicate paid
call was issued. The palette timeout is now 210 seconds for subsequent builds.

- Request SHA-256: `a9abff610c2bfd6bfc7edc838c825851511ec5f9c2527b804e4eb118e455823f`
- Result SHA-256: `e0edddc0df3169e3c08432ac62e25a1e084cf333f145211718d03a7594c35045`
- Provider: `openai-official-search:gpt-5.6:saved-evidence`
- Candidates: Legrand `004280` and Schneider Electric `A9A15306`
- Both candidates: all three mandatory requirements `PASS`
- Selected for separate review: Legrand `004280`
- Selection SHA-256: `26c5e85d55d60b8ac24baa2e2b8fa21756a69b1a4f5ffb9336b2e95f7dc55d55`
- `provider_egress_performed=true`
- `automatic_selection_performed=false`
- `product_approval_performed=false`
- `automatic_download_performed=false`
- `catalog_registration_performed=false`
- `drawing_mutation_performed=false`

This completes the paid discovery live-acceptance gate. The selection is only a hash-bound engineer
choice for separate datasheet/product-approval review; it is not approval, procurement, download,
catalog registration, or drawing insertion authority.

## Product Discovery layout acceptance — passed 2026-08-12

After the Free acceptance exposed a compressed one-line output area and truncated button labels,
the revised palette was rebuilt and opened in AutoCAD 2021. The operator visually confirmed:

- full labels for `Select approved request`, `Verify free candidate file`, `Search official
  sources`, `Copy complete result`, and `Choose for separate review`;
- a large vertically scrollable, wrapped result area;
- visible Legrand `004280` identity, official URL, mandatory-satisfied state, and three `PASS`
  requirement findings;
- vertically stacked candidate, rationale, and review-choice controls; and
- status `Candidates displayed for engineer choice; no follow-on action performed.`

No selection was recorded during this layout-only rerun, and no download, catalog, or drawing
operation was invoked.
