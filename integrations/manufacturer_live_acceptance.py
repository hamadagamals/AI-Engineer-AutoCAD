"""One bounded live M23 acceptance using exact official Legrand public files."""

from __future__ import annotations

import json
import os
import tempfile
from pathlib import Path

from core.manufacturer_evidence import (
    ManufacturerAccessBasis,
    ManufacturerArtifact,
    ManufacturerArtifactKind,
    ManufacturerIdentity,
    ManufacturerProductIdentity,
    ManufacturerPropertyClaim,
    create_manufacturer_evidence_bundle,
)
from integrations.manufacturer_mapping import (
    ManufacturerReviewScope,
    review_manufacturer_package,
)
from integrations.manufacturer_retrieval import (
    create_retrieval_request,
    retrieve_to_quarantine,
)


REVIEW_DATE = "2026-08-12"
REVIEWER = "engineer:codex-functional-acceptance"
PRODUCT_ID = "product:legrand-004280"
MANUFACTURER_ID = "manufacturer:legrand"
PDF_URL = "https://assets.legrand.com/pim/NP-FT-GT/F02438EN-00.pdf"
DWG_URL = "https://assets.legrand.com/pim/AUTRE/004280.dwg"


def run(workspace_root: Path) -> Path:
    """Retrieve two exact files into quarantine and persist a palette review package."""
    root = workspace_root.resolve()
    acceptance = root / ".ai-engineer" / "manufacturer" / "live-acceptance" / "legrand-004280"
    quarantine_root = acceptance / "quarantine"
    output = acceptance / "manufacturer-review-request.json"
    if output.exists():
        raise RuntimeError(f"Live acceptance output already exists: {output}")

    pdf_request = create_retrieval_request(
        request_id="retrieval:legrand-004280-datasheet",
        manufacturer_id=MANUFACTURER_ID,
        product_id=PRODUCT_ID,
        artifact_id="artifact:legrand-004280-datasheet",
        locator=PDF_URL,
        approved_host="assets.legrand.com",
        expected_media_types=("application/pdf",),
        maximum_bytes=1_000_000,
        expected_content_sha256=None,
        approved_by=REVIEWER,
        approved_on=REVIEW_DATE,
    )
    cad_request = create_retrieval_request(
        request_id="retrieval:legrand-004280-cad",
        manufacturer_id=MANUFACTURER_ID,
        product_id=PRODUCT_ID,
        artifact_id="artifact:legrand-004280-cad",
        locator=DWG_URL,
        approved_host="assets.legrand.com",
        expected_media_types=("application/acad",),
        maximum_bytes=1_000_000,
        expected_content_sha256=None,
        approved_by=REVIEWER,
        approved_on=REVIEW_DATE,
    )
    pdf_record = retrieve_to_quarantine(
        pdf_request, quarantine_root, retrieved_on=REVIEW_DATE
    )
    cad_record = retrieve_to_quarantine(
        cad_request, quarantine_root, retrieved_on=REVIEW_DATE
    )

    manufacturer = ManufacturerIdentity(
        manufacturer_id=MANUFACTURER_ID,
        legal_name="Legrand SA",
        brand_name="Legrand",
        official_domains=("www.legrand.com", "assets.legrand.com"),
    )
    product = ManufacturerProductIdentity(
        product_id=PRODUCT_ID,
        manufacturer_id=MANUFACTURER_ID,
        family_name="Lexic modular socket outlet",
        model="Socket outlet 2P+E shuttered French standard",
        part_number="004280",
        variant="10/16 A 250 V AC grey DIN rail",
    )
    artifacts = (
        ManufacturerArtifact(
            artifact_id=pdf_request.artifact_id,
            product_id=PRODUCT_ID,
            kind=ManufacturerArtifactKind.DATA_SHEET,
            title="Technical data sheet F02438EN-00",
            revision="F02438EN-00",
            published_on="2021-04-21",
            retrieved_on=REVIEW_DATE,
            access_basis=ManufacturerAccessBasis.OFFICIAL_PUBLIC,
            locator=PDF_URL,
            media_type=pdf_record.media_type,
            byte_count=pdf_record.byte_count,
            content_sha256=pdf_record.content_sha256,
            quarantined=True,
            executable_content_detected=False,
            license_reference="Legrand official public product page and published download.",
        ),
        ManufacturerArtifact(
            artifact_id=cad_request.artifact_id,
            product_id=PRODUCT_ID,
            kind=ManufacturerArtifactKind.CAD_ASSET,
            title="Official 3D AutoCAD asset 004280.dwg",
            revision="004280",
            published_on=None,
            retrieved_on=REVIEW_DATE,
            access_basis=ManufacturerAccessBasis.OFFICIAL_PUBLIC,
            locator=DWG_URL,
            media_type=cad_record.media_type,
            byte_count=cad_record.byte_count,
            content_sha256=cad_record.content_sha256,
            quarantined=True,
            executable_content_detected=False,
            license_reference="Legrand official public product page and published download.",
        ),
    )
    claims = (
        _claim("rated-current", "Rated current", "16", "A", "page 1 section 3"),
        _claim("rated-voltage", "Rated voltage", "250", "V AC", "page 1 section 3"),
        _claim("poles", "Pole configuration", "2P+E shuttered", None, "page 1 section 1"),
    )
    bundle = create_manufacturer_evidence_bundle(
        bundle_id="manufacturer-bundle:legrand-004280-live",
        project_id="project:functional-live-acceptance",
        manufacturer=manufacturer,
        product=product,
        artifacts=artifacts,
        property_claims=claims,
        reviewed_by=REVIEWER,
        reviewed_on=REVIEW_DATE,
        assumptions=(
            "Functional acceptance only; no project suitability or procurement approval is implied.",
            "CAD remains quarantined and has not been approved as electrical plan-symbol geometry.",
        ),
    )
    result = review_manufacturer_package(
        bundle,
        review_id="manufacturer-review:legrand-004280-live",
        scope=ManufacturerReviewScope.QUARANTINED_ARTIFACT,
        retrieval_request=cad_request,
        quarantine=cad_record,
        model_binding=None,
        symbol_candidate=None,
        catalog_registration=None,
        require_model_binding=False,
        reviewed_by=REVIEWER,
        reviewed_on=REVIEW_DATE,
        assumptions=("Exact official data sheet and separately quarantined CAD acceptance.",),
    )
    package = {
        "review_id": result.review_id,
        "scope": result.scope.value,
        "bundle": bundle.to_dict(),
        "retrieval_request": cad_request.to_dict(),
        "quarantine": cad_record.to_dict(),
        "model_binding": None,
        "symbol_candidate": None,
        "catalog_registration": None,
        "require_model_binding": False,
        "reviewed_by": REVIEWER,
        "reviewed_on": REVIEW_DATE,
        "assumptions": ["Exact official data sheet and separately quarantined CAD acceptance."],
    }
    _write_new_json(output, package)
    print(f"Manufacturer: {manufacturer.manufacturer_id} | {manufacturer.brand_name}")
    print(f"Product: {product.product_id} | {product.part_number}")
    print(f"Data sheet SHA-256: {pdf_record.content_sha256}")
    print(f"CAD SHA-256: {cad_record.content_sha256}")
    print(f"Bundle SHA-256: {bundle.bundle_sha256}")
    print(f"Review SHA-256: {result.result_sha256}")
    print(f"Complete: {str(result.complete).lower()}")
    print("provider_egress_performed=false")
    print("catalog_registration_performed_by_review=false")
    print("drawing_mutation_performed=false")
    for finding in result.findings:
        print(
            f"{finding.outcome.value.upper()} | {finding.subject_id} | {finding.message}"
        )
    print(f"Palette request: {output}")
    return output


def _claim(
    suffix: str, name: str, value: str, unit: str | None, locator: str
) -> ManufacturerPropertyClaim:
    return ManufacturerPropertyClaim(
        claim_id=f"claim:legrand-004280-{suffix}",
        product_id=PRODUCT_ID,
        artifact_id="artifact:legrand-004280-datasheet",
        property_name=name,
        value=value,
        unit=unit,
        source_locator=f"F02438EN-00.pdf {locator}",
        applicability="Legrand part number 004280 only.",
        reviewed_by=REVIEWER,
        reviewed_on=REVIEW_DATE,
        assumptions=("Transcribed from the exact official data-sheet location.",),
    )


def _write_new_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    content = (
        json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")) + "\n"
    ).encode("utf-8")
    descriptor, temporary_name = tempfile.mkstemp(prefix=f".{path.name}.", dir=path.parent)
    temporary = Path(temporary_name)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
        try:
            os.link(temporary, path)
        except FileExistsError as exc:
            raise RuntimeError(f"Live acceptance output already exists: {path}") from exc
        temporary.unlink()
    finally:
        if temporary.exists():
            temporary.unlink()


if __name__ == "__main__":
    import argparse

    parser = argparse.ArgumentParser()
    parser.add_argument("workspace_root", type=Path)
    arguments = parser.parse_args()
    run(arguments.workspace_root)
