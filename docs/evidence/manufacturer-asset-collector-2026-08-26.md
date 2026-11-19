# Manufacturer Asset Collector verification — 2026-08-26

## Scope

Implemented a standalone reviewed workflow for an exact manufacturer part number and an ordered
request of data sheet, 2D CAD, 3D CAD, and image assets. Search is limited to engineer-approved
official domains. Search does not select or download. A second exact approval selects candidate
IDs in output order before HTTPS retrieval into quarantine.

## Verified controls

- exact part-number, approved-domain, requested-kind, and SHA-lineage validation;
- distinct approved official hosts for product pages and manufacturer CDN files;
- PDF, DWG, DXF, STEP/STP, IGES/IGS, JPEG, and PNG media/signature handling;
- HTTPS-only public-address retrieval without proxy or automatic redirect;
- bounded size, atomic no-overwrite quarantine, SHA-256, and ordered folders;
- no automatic product selection, catalog registration, CAD import, or drawing mutation;
- process-only OpenAI credential handling and separate `SEARCH` / `DOWNLOAD` confirmations.

## Automated result

`44 passed` for the focused manufacturer discovery, asset collection, retrieval, API, mapping,
and interactive-console suites. The PowerShell launcher parser passed. Warnings were limited to
the pre-existing TestClient deprecation and a denied optional pytest-cache directory.

Portable candidate r47 was then built and installed into a new acceptance directory. Installed
`launcher.py check` returned `status: ok`; the console imported successfully and preserved the
requested `data_sheet, cad_3d, cad_2d, image` order. The 795,360-byte archive contains 216 manifest
entries before the manifest itself and has SHA-256
`1C06AF21ADB09229B73711E2065B995273A594BE84B418F281771FBC0C837D6A`.

## Remaining live acceptance

Run the three-product matrix in `docs/MANUFACTURER_ASSET_COLLECTOR.md`. Compare each downloaded
file with its official source before any later CAD/catalog intake. Missing official formats must
remain explicitly missing rather than being synthesized or substituted.
