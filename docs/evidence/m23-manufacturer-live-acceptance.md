# Milestone 23 manufacturer live acceptance — 2026-08-12

## Scope and product-selection boundary

This is a functional API/AutoCAD acceptance using public official manufacturer files. It
does not approve the product for an engineering project, procurement, regulatory
compliance, or drawing placement. Product selection still requires project calculations,
applicable-code review, availability, and engineer approval.

- Official product page: `https://www.legrand.com/ecatalogue/en/catalog/products/socket-outlet-1016-250-v-2pe-shuttered-french-standard-004280`
- Manufacturer: Legrand SA; brand: Legrand
- Product family: Lexic modular socket outlet
- Part number: `004280`
- Official data sheet: `https://assets.legrand.com/pim/NP-FT-GT/F02438EN-00.pdf`
- Official CAD: `https://assets.legrand.com/pim/AUTRE/004280.dwg`

## Retrieval and quarantine

Both exact HTTPS locators were separately approved and downloaded with proxies and
redirects disabled into the local immutable quarantine. The PDF response declared its
typed media normally. Legrand's official asset host declared the `.dwg` as
`application/octet-stream`; acceptance exposed this legitimate server behavior. The
retriever now permits that declaration only when the exact reviewed URL path ends in
`.dwg` or `.dxf`, the matching typed CAD media is explicitly approved, and the file
passes the required DWG/DXF signature and executable-content checks. Generic binaries,
missing extensions, unexpected types, and invalid signatures remain rejected.

- Data sheet: 182,314 bytes; SHA-256
  `413bf9fbb4dd1186721f35360d6cf849919f9046dd66bf13539b87077e6c085d`
- CAD: 95,300 bytes; SHA-256
  `08b5fd966ee2df8eac93668f0d9317b2d0fc8d3ccc59f0ddc337907e471ab430`
- Quarantine record SHA-256:
  `5d4bd866a996ea05ed2dabc9f76e4c0faedeb6c19a5878e380d6642130b88aef`

The CAD remains quarantined. It was not accepted as plan-symbol geometry, registered in
the symbol catalog, inserted, opened for write, or used to mutate a drawing.

## API and AutoCAD result

The authenticated `POST /v1/manufacturers/review` route reconstructed and revalidated all
nested records from the persisted palette request. It returned HTTP 201, `complete=true`,
and three `PASS` findings for evidence, exact approved retrieval, and matching quarantine.

- Evidence bundle SHA-256:
  `b7d8704620a60f64a9fb2ff94ac5d41361bf0da4ea87690d79d47a2f954b7743`
- Review result SHA-256:
  `e18ced57a6b574b1f857475e61e475705635f121577686df6220d113ff37d6fd`
- Persisted palette request:
  `.ai-engineer/manufacturer/live-acceptance/legrand-004280/manufacturer-review-request.json`

The AutoCAD 2021 x64 bridge rebuilt successfully. The authenticated loopback-only API
reported version `0.18.0`. The operator visually confirmed the exact manufacturer,
product, part number, bundle/result hashes, both artifact identities and hashes,
quarantine hash, `Complete: true`, and the three `PASS` findings. The display retained:

- `provider_egress_performed=false`
- `retrieval_performed_by_review=false`
- `catalog_registration_performed_by_review=false`
- `drawing_mutation_performed=false`

The API replay independently reproduced the displayed bundle and result hashes exactly.
