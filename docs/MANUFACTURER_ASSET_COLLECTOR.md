# Manufacturer Asset Collector

The collector extends the existing reviewed product-discovery workflow. It accepts one
exact manufacturer product identity and an ordered list of requested asset kinds, searches
only approved official domains, returns candidates without downloading, then requires a
separate SHA-bound download approval.

## Supported asset kinds and formats

| Requested kind | Supported formats |
|---|---|
| `data_sheet` | PDF |
| `cad_2d` | DWG, DXF |
| `cad_3d` | STEP/STP, IGES/IGS |
| `image` | JPEG, PNG |

ZIP archives and executable content are intentionally rejected. CAD assets remain quarantined
until a later import/catalog review.

## Workflow

1. Use Product Discovery to establish the exact manufacturer, part number, official product
   page, official domains, and product-page evidence SHA-256.
2. Create a self-hashed `ManufacturerAssetSearchRequest` with requested kinds in the desired
   order. Search approval authorizes provider egress only.
3. POST it to `/v1/manufacturers/assets/discover`. The response contains direct official asset
   candidates and confirms that no selection or download occurred.
4. Review the URLs, types, and exact part number. POST the selected candidate IDs in the desired
   output order to `/v1/manufacturers/assets/collect` with `download_approved=true`.
5. Files are stored beneath:

   ```text
   workspace/manufacturer-assets/<manufacturer>/<part-number>/
   ├── 01-data_sheet/
   ├── 02-cad_2d/
   ├── 03-cad_3d/
   └── 04-image/
   ```

   Folder numbers follow the approved candidate order, not a hard-coded kind order.

Each download is limited to 100 MB, uses HTTPS without proxies or automatic redirects, rejects
private/local DNS destinations, validates content signatures, writes atomically without overwrite,
and records source URL, resolved public addresses, bytes, media type, SHA-256, and quarantine path.

## Interactive launcher

From the project root run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\START-AI-ENGINEER-MANUFACTURER-ASSETS.ps1
```

The console asks for manufacturer, exact part number, approved official domains, desired output
order, and reviewer name. It stops separately for `SEARCH` and `DOWNLOAD`. The OpenAI key is kept
only in the child process environment and is removed when the launcher exits.

## Safety and limitations

- The first version starts from an exact product identity already reviewed by Product Discovery;
  it does not infer an unknown manufacturer from a bare ambiguous part number.
- Sites requiring login, CAPTCHA, acceptance of new license terms, redirects, or ZIP-only packages
  stop for separate review. The collector does not bypass access controls.
- A result from a distributor or a different part number is rejected.
- Collection performs no catalog registration, STEP import, block generation, or drawing mutation.
- Official file availability differs by manufacturer. Missing requested kinds remain missing; the
  system does not fabricate a manufacturer asset.

## Recommended live acceptance set

Use three exact products after reviewing their official domains:

1. One Schneider breaker with PDF + STEP.
2. Legrand `004280` with PDF + DWG, already known from earlier evidence.
3. One product where an image exists but no official 3D model, to verify honest missing-file output.

For each product, compare the downloaded hash and filename with the official page before any CAD
intake. Test source candidates only; no AutoCAD drawing change is part of this acceptance.
