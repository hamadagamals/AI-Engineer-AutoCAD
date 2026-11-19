"""Tests for immutable manufacturer evidence and quarantined CAD assets."""

from __future__ import annotations

import hashlib
from dataclasses import replace

import pytest

from agent.exceptions import ManufacturerEvidenceError
from core.manufacturer_evidence import (
    ManufacturerAccessBasis,
    ManufacturerArtifact,
    ManufacturerArtifactKind,
    ManufacturerIdentity,
    ManufacturerProductIdentity,
    ManufacturerPropertyClaim,
    create_manufacturer_evidence_bundle,
)


def _manufacturer() -> ManufacturerIdentity:
    return ManufacturerIdentity(
        manufacturer_id="manufacturer:test",
        legal_name="Reviewed Manufacturer Corporation",
        brand_name="Reviewed Brand",
        official_domains=("products.example.test",),
    )


def _product() -> ManufacturerProductIdentity:
    return ManufacturerProductIdentity(
        product_id="product:breaker-abc",
        manufacturer_id="manufacturer:test",
        family_name="Breaker family",
        model="ABC",
        part_number="ABC-16-6K",
        variant="16 A / 6 kA",
    )


def _artifact(*, kind=ManufacturerArtifactKind.DATA_SHEET, quarantined=False, host="products.example.test"):
    return ManufacturerArtifact(
        artifact_id="artifact:breaker-datasheet" if kind is ManufacturerArtifactKind.DATA_SHEET else "artifact:breaker-cad",
        product_id="product:breaker-abc",
        kind=kind,
        title="Reviewed product artifact",
        revision="rev-2026-01",
        published_on="2026-01-15",
        retrieved_on="2026-08-12",
        access_basis=ManufacturerAccessBasis.OFFICIAL_PUBLIC,
        locator=f"https://{host}/files/abc.pdf",
        media_type="application/pdf" if kind is ManufacturerArtifactKind.DATA_SHEET else "application/dxf",
        byte_count=1234,
        content_sha256=hashlib.sha256(kind.value.encode()).hexdigest(),
        quarantined=quarantined,
        executable_content_detected=False,
        license_reference="Official public product download; redistribution not inferred.",
    )


def _claim() -> ManufacturerPropertyClaim:
    return ManufacturerPropertyClaim(
        claim_id="claim:rated-current",
        product_id="product:breaker-abc",
        artifact_id="artifact:breaker-datasheet",
        property_name="rated_current",
        value="16",
        unit="ampere",
        source_locator="Page 2, reviewed product table row ABC-16-6K",
        applicability="Exact part number ABC-16-6K only.",
        reviewed_by="engineer:gamal",
        reviewed_on="2026-08-12",
        assumptions=("No value is inferred for another family member.",),
    )


def _bundle(*, artifacts=None, claims=None):
    return create_manufacturer_evidence_bundle(
        bundle_id="manufacturer-evidence:test",
        project_id="project:test",
        manufacturer=_manufacturer(),
        product=_product(),
        artifacts=(_artifact(),) if artifacts is None else artifacts,
        property_claims=(_claim(),) if claims is None else claims,
        reviewed_by="engineer:gamal",
        reviewed_on="2026-08-12",
        assumptions=("Synthetic contract fixture; no production product selection is made.",),
    )


def test_bundle_is_self_hashed_exact_and_grants_no_external_authority() -> None:
    bundle = _bundle()
    assert len(bundle.bundle_sha256) == 64
    assert bundle.product.part_number == "ABC-16-6K"
    assert bundle.property_claims[0].applicability.endswith("only.")
    assert bundle.retrieval_performed_by_contract is False
    assert bundle.provider_egress_performed is False
    assert bundle.catalog_registration_performed is False
    assert bundle.drawing_mutation_performed is False
    with pytest.raises(ManufacturerEvidenceError, match="SHA-256"):
        replace(bundle, bundle_sha256="f" * 64)


def test_cad_assets_must_be_quarantined_and_executables_are_forbidden() -> None:
    with pytest.raises(ManufacturerEvidenceError, match="quarantined"):
        _artifact(kind=ManufacturerArtifactKind.CAD_ASSET, quarantined=False)
    cad = _artifact(kind=ManufacturerArtifactKind.CAD_ASSET, quarantined=True)
    bundle = _bundle(artifacts=(_artifact(), cad))
    assert bundle.artifacts[1].quarantined is True
    with pytest.raises(ManufacturerEvidenceError, match="Executable"):
        replace(cad, executable_content_detected=True)


def test_official_artifact_requires_exact_approved_https_host() -> None:
    with pytest.raises(ManufacturerEvidenceError, match="approved"):
        _bundle(artifacts=(_artifact(host="cdn.example.test"),), claims=())
    with pytest.raises(ManufacturerEvidenceError, match="HTTPS"):
        replace(_artifact(), locator="http://products.example.test/abc.pdf")


def test_claims_cannot_cross_product_or_unknown_artifact_boundaries() -> None:
    with pytest.raises(ManufacturerEvidenceError, match="claim binding"):
        _bundle(claims=(replace(_claim(), artifact_id="artifact:missing"),))
    with pytest.raises(ManufacturerEvidenceError, match="different product"):
        _bundle(artifacts=(replace(_artifact(), product_id="product:other"),), claims=())


def test_bundle_rejects_catalog_or_drawing_success_claims() -> None:
    bundle = _bundle()
    with pytest.raises(ManufacturerEvidenceError, match="cannot claim"):
        replace(bundle, catalog_registration_performed=True)
    with pytest.raises(ManufacturerEvidenceError, match="cannot claim"):
        replace(bundle, drawing_mutation_performed=True)
