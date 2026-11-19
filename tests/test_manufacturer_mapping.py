"""Tests for reviewed manufacturer model sidecars and symbol candidates."""

from __future__ import annotations

import hashlib
import json
from dataclasses import replace
from datetime import UTC, datetime
from io import BytesIO
from pathlib import Path

import pytest

from agent.exceptions import ConfirmationRequiredError, ManufacturerEvidenceError
from core.electrical_model import (
    Device,
    PrivacyClassification,
    SourceReference,
)
from core.manufacturer_evidence import (
    ManufacturerArtifactKind,
    create_manufacturer_evidence_bundle,
    parse_manufacturer_evidence_bundle,
)
from integrations.electrical_symbols import calculate_definition_sha256
from integrations.manufacturer_mapping import (
    ManufacturerTargetKind,
    ManufacturerReviewOutcome,
    ManufacturerReviewScope,
    create_model_binding,
    create_symbol_candidate,
    parse_catalog_registration,
    parse_model_binding,
    parse_symbol_candidate,
    register_manufacturer_symbol_candidate,
    review_manufacturer_package,
)
import integrations.manufacturer_mapping as mapping_module
from integrations.electrical_symbols import ElectricalSymbolCatalog
from integrations.manufacturer_retrieval import (
    create_retrieval_request,
    parse_quarantine_record,
    parse_retrieval_request,
    retrieve_to_quarantine,
)
from tests.test_electrical_model import _empty_model
from tests.test_manufacturer_evidence import _artifact, _bundle


CATALOG = Path(__file__).resolve().parents[1] / "plugins" / "electrical_symbols" / "catalog.json"
DXF = b"0\nSECTION\n2\nENTITIES\n0\nENDSEC\n0\nEOF\n"


def _model():
    base = _empty_model()
    device = Device(
        device_id="device:breaker-1",
        symbol_id="socket_single",
        block_name="AIENG_E_SOCKET_SINGLE_V1",
        layer_id="layer:devices",
        insertion_point=(0.0, 0.0, 0.0),
        rotation_degrees=0.0,
        scale=(1.0, 1.0, 1.0),
        attributes=(),
        terminals=(),
        source=SourceReference("autocad", "document:test", "A1", "a" * 32),
    )
    return replace(base, devices=(device,))


def _cad_bundle():
    data_sheet = _artifact()
    cad = replace(
        _artifact(kind=ManufacturerArtifactKind.CAD_ASSET, quarantined=True),
        locator="https://products.example.test/files/abc.dxf",
        byte_count=len(DXF),
        content_sha256=hashlib.sha256(DXF).hexdigest(),
        media_type="application/dxf",
    )
    return _bundle(artifacts=(data_sheet, cad))


def _quarantine(tmp_path, *, content=DXF, suffix=""):
    request = create_retrieval_request(
        request_id=f"retrieval:breaker-cad{suffix}",
        manufacturer_id="manufacturer:test",
        product_id="product:breaker-abc",
        artifact_id="artifact:breaker-cad",
        locator="https://products.example.test/files/abc.dxf",
        approved_host="products.example.test",
        expected_media_types=("application/dxf",),
        maximum_bytes=4096,
        expected_content_sha256=hashlib.sha256(content).hexdigest(),
        approved_by="engineer:gamal",
        approved_on="2026-08-12",
    )
    record = retrieve_to_quarantine(
        request,
        tmp_path / f"quarantine{suffix}",
        resolver=lambda host: ("93.184.216.34",),
        transport=lambda url, maximum: ("application/dxf", BytesIO(content)),
        retrieved_on="2026-08-12",
    )
    return request, record


def _proposal():
    value = json.loads(CATALOG.read_text(encoding="utf-8"))["symbols"][0]
    value.update(
        {
            "id": "breaker_abc",
            "block_name": "AIENG_E_BREAKER_ABC_V1",
            "description": "Engineer-reviewed symbol derived from exact quarantined manufacturer CAD.",
        }
    )
    value.pop("definition_sha256")
    value["definition_sha256"] = calculate_definition_sha256(value)
    return value


def test_reviewed_claims_map_to_exact_model_object_without_mutation() -> None:
    model = _model()
    before = model.to_dict()
    bundle = _bundle()
    binding = create_model_binding(
        model,
        bundle,
        binding_id="manufacturer-binding:breaker-1",
        target_kind=ManufacturerTargetKind.DEVICE,
        target_id="device:breaker-1",
        claim_ids=("claim:rated-current",),
        captured_at="2026-08-12T11:00:00Z",
        privacy=PrivacyClassification.PROJECT_CONFIDENTIAL,
        reviewed_by="engineer:gamal",
        reviewed_on="2026-08-12",
        assumptions=("Exact part number applicability was reviewed.",),
    )

    assert binding.target_id == "device:breaker-1"
    assert binding.part_number == "ABC-16-6K"
    assert binding.properties[0].value == "16"
    assert binding.properties[0].artifact_content_sha256 == bundle.artifacts[0].content_sha256
    assert binding.provenance.evidence_sha256 == bundle.bundle_sha256
    assert binding.sidecar_mapping_performed is True
    assert binding.shared_model_mutation_performed is False
    assert binding.catalog_registration_performed is False
    assert binding.drawing_mutation_performed is False
    assert model.to_dict() == before


def test_model_binding_rejects_wrong_project_target_and_claim() -> None:
    model = _model()
    common = dict(
        binding_id="manufacturer-binding:test",
        target_kind=ManufacturerTargetKind.DEVICE,
        target_id="device:breaker-1",
        claim_ids=("claim:rated-current",),
        captured_at="2026-08-12T11:00:00Z",
        privacy=PrivacyClassification.INTERNAL,
        reviewed_by="engineer:gamal",
        reviewed_on="2026-08-12",
        assumptions=("Reviewed.",),
    )
    original = _bundle()
    other_project = create_manufacturer_evidence_bundle(
        bundle_id=original.bundle_id,
        project_id="project:other",
        manufacturer=original.manufacturer,
        product=original.product,
        artifacts=original.artifacts,
        property_claims=original.property_claims,
        reviewed_by=original.reviewed_by,
        reviewed_on=original.reviewed_on,
        assumptions=original.assumptions,
    )
    with pytest.raises(ManufacturerEvidenceError, match="different project"):
        create_model_binding(model, other_project, **common)
    with pytest.raises(ManufacturerEvidenceError, match="does not exist"):
        create_model_binding(model, _bundle(), **{**common, "target_id": "device:missing"})
    with pytest.raises(ManufacturerEvidenceError, match="unknown property"):
        create_model_binding(model, _bundle(), **{**common, "claim_ids": ("claim:missing",)})


def test_symbol_candidate_binds_exact_cad_and_remains_unregistered(tmp_path) -> None:
    bundle = _cad_bundle()
    request, quarantine = _quarantine(tmp_path)
    proposal = _proposal()
    candidate = create_symbol_candidate(
        bundle,
        request,
        quarantine,
        candidate_id="manufacturer-symbol:breaker-abc",
        artifact_id="artifact:breaker-cad",
        symbol_proposal=proposal,
        reviewed_by="engineer:gamal",
        reviewed_on="2026-08-12",
        assumptions=("Geometry was manually reviewed against the quarantined DXF.",),
    )

    assert candidate.artifact_content_sha256 == hashlib.sha256(DXF).hexdigest()
    assert candidate.symbol_definition_sha256 == proposal["definition_sha256"]
    assert candidate.catalog_proposal() == proposal
    assert candidate.geometry_derivation_automated is False
    assert candidate.catalog_registration_performed is False
    assert candidate.drawing_mutation_performed is False


def test_symbol_candidate_rejects_mismatched_quarantine_and_tampering(tmp_path) -> None:
    bundle = _cad_bundle()
    request, quarantine = _quarantine(tmp_path)
    other_dxf = b"0\nSECTION\n2\nBLOCKS\n0\nENDSEC\n0\nEOF\n"
    other_request, other_quarantine = _quarantine(
        tmp_path, content=other_dxf, suffix="-other"
    )
    with pytest.raises(ManufacturerEvidenceError, match="exact CAD artifact"):
        create_symbol_candidate(
            bundle,
            other_request,
            other_quarantine,
            candidate_id="manufacturer-symbol:test",
            artifact_id="artifact:breaker-cad",
            symbol_proposal=_proposal(),
            reviewed_by="engineer:gamal",
            reviewed_on="2026-08-12",
            assumptions=("Reviewed.",),
        )
    candidate = create_symbol_candidate(
        bundle,
        request,
        quarantine,
        candidate_id="manufacturer-symbol:test",
        artifact_id="artifact:breaker-cad",
        symbol_proposal=_proposal(),
        reviewed_by="engineer:gamal",
        reviewed_on="2026-08-12",
        assumptions=("Reviewed.",),
    )
    with pytest.raises(ManufacturerEvidenceError, match="SHA-256"):
        replace(candidate, part_number="CHANGED")


def test_symbol_candidate_cannot_claim_automatic_geometry_or_registration(tmp_path) -> None:
    request, quarantine = _quarantine(tmp_path)
    candidate = create_symbol_candidate(
        _cad_bundle(), request, quarantine, candidate_id="manufacturer-symbol:test",
        artifact_id="artifact:breaker-cad", symbol_proposal=_proposal(),
        reviewed_by="engineer:gamal", reviewed_on="2026-08-12",
        assumptions=("Reviewed.",),
    )
    with pytest.raises(ManufacturerEvidenceError, match="auto-approved"):
        replace(candidate, geometry_derivation_automated=True)
    with pytest.raises(ManufacturerEvidenceError, match="cannot claim"):
        replace(candidate, catalog_registration_performed=True)


def _candidate(tmp_path):
    request, quarantine = _quarantine(tmp_path)
    return create_symbol_candidate(
        _cad_bundle(), request, quarantine,
        candidate_id="manufacturer-symbol:breaker-abc",
        artifact_id="artifact:breaker-cad", symbol_proposal=_proposal(),
        reviewed_by="engineer:gamal", reviewed_on="2026-08-12",
        assumptions=("Geometry manually reviewed against exact CAD.",),
    )


def test_confirmed_candidate_registration_backs_up_and_persists_provenance(tmp_path) -> None:
    catalog = tmp_path / "catalog.json"
    original = CATALOG.read_bytes()
    catalog.write_bytes(original)
    candidate = _candidate(tmp_path)
    registration = register_manufacturer_symbol_candidate(
        candidate,
        catalog,
        tmp_path / "backups",
        tmp_path / "provenance",
        confirmed=True,
        registered_by="engineer:gamal",
        clock=lambda: datetime(2026, 8, 12, 12, 0, tzinfo=UTC),
    )

    assert ElectricalSymbolCatalog.load(catalog).get("breaker_abc").definition_sha256 == candidate.symbol_definition_sha256
    assert registration.catalog_sha256_before == hashlib.sha256(original).hexdigest()
    assert registration.catalog_version_after == registration.catalog_version_before + 1
    assert registration.backup_sha256 == registration.catalog_sha256_before
    assert registration.catalog_registration_performed is True
    assert registration.provenance_write_performed is True
    assert registration.drawing_placement_performed is False
    assert registration.drawing_mutation_performed is False
    saved = json.loads(
        (tmp_path / "provenance" / registration.provenance_relative_path).read_text(encoding="utf-8")
    )
    assert saved == registration.to_dict()
    backups = list((tmp_path / "backups").glob("catalog-*.json"))
    assert len(backups) == 1
    assert backups[0].read_bytes() == original


def test_candidate_registration_requires_confirmation_without_catalog_change(tmp_path) -> None:
    catalog = tmp_path / "catalog.json"
    original = CATALOG.read_bytes()
    catalog.write_bytes(original)
    with pytest.raises(ConfirmationRequiredError, match="confirmation"):
        register_manufacturer_symbol_candidate(
            _candidate(tmp_path), catalog, tmp_path / "backups", tmp_path / "provenance",
            registered_by="engineer:gamal",
        )
    assert catalog.read_bytes() == original
    assert not (tmp_path / "backups").exists()
    assert not (tmp_path / "provenance").exists()


def test_provenance_write_failure_restores_exact_catalog(
    tmp_path, monkeypatch: pytest.MonkeyPatch
) -> None:
    catalog = tmp_path / "catalog.json"
    original = CATALOG.read_bytes()
    catalog.write_bytes(original)

    def fail_write(path, value):
        raise OSError("simulated provenance storage failure")

    monkeypatch.setattr(mapping_module, "_write_new_json", fail_write)
    with pytest.raises(ManufacturerEvidenceError, match="prior catalog was restored"):
        register_manufacturer_symbol_candidate(
            _candidate(tmp_path), catalog, tmp_path / "backups", tmp_path / "provenance",
            confirmed=True, registered_by="engineer:gamal",
        )
    assert catalog.read_bytes() == original
    assert ElectricalSymbolCatalog.load(catalog).catalog_version == 1


def test_full_manufacturer_package_round_trips_and_reviews_complete(tmp_path) -> None:
    bundle = _cad_bundle()
    request, quarantine = _quarantine(tmp_path)
    candidate = create_symbol_candidate(
        bundle, request, quarantine, candidate_id="manufacturer-symbol:review",
        artifact_id="artifact:breaker-cad", symbol_proposal=_proposal(),
        reviewed_by="engineer:gamal", reviewed_on="2026-08-12",
        assumptions=("Reviewed geometry.",),
    )
    binding = create_model_binding(
        _model(), bundle, binding_id="manufacturer-binding:review",
        target_kind=ManufacturerTargetKind.DEVICE, target_id="device:breaker-1",
        claim_ids=("claim:rated-current",), captured_at="2026-08-12T12:00:00Z",
        privacy=PrivacyClassification.INTERNAL, reviewed_by="engineer:gamal",
        reviewed_on="2026-08-12", assumptions=("Reviewed applicability.",),
    )
    catalog = tmp_path / "catalog.json"
    catalog.write_bytes(CATALOG.read_bytes())
    registration = register_manufacturer_symbol_candidate(
        candidate, catalog, tmp_path / "backups", tmp_path / "provenance",
        confirmed=True, registered_by="engineer:gamal",
        clock=lambda: datetime(2026, 8, 12, 12, 30, tzinfo=UTC),
    )

    parsed_bundle = parse_manufacturer_evidence_bundle(bundle.to_dict())
    parsed_request = parse_retrieval_request(request.to_dict())
    parsed_quarantine = parse_quarantine_record(quarantine.to_dict())
    parsed_binding = parse_model_binding(binding.to_dict())
    parsed_candidate = parse_symbol_candidate(candidate.to_dict())
    parsed_registration = parse_catalog_registration(registration.to_dict())
    result = review_manufacturer_package(
        parsed_bundle, review_id="manufacturer-review:complete",
        scope=ManufacturerReviewScope.CATALOG_REGISTRATION,
        retrieval_request=parsed_request, quarantine=parsed_quarantine,
        model_binding=parsed_binding, symbol_candidate=parsed_candidate,
        catalog_registration=parsed_registration, require_model_binding=True,
        reviewed_by="engineer:gamal", reviewed_on="2026-08-12",
        assumptions=("Full local review only; no action performed.",),
    )

    assert result.complete is True
    assert {item.outcome for item in result.findings} == {ManufacturerReviewOutcome.PASS}
    assert result.provider_egress_performed is False
    assert result.retrieval_performed_by_review is False
    assert result.catalog_registration_performed_by_review is False
    assert result.drawing_mutation_performed is False


def test_review_missing_and_conflicting_records_fail_closed(tmp_path) -> None:
    bundle = _cad_bundle()
    missing = review_manufacturer_package(
        bundle, review_id="manufacturer-review:missing",
        scope=ManufacturerReviewScope.SYMBOL_CANDIDATE,
        retrieval_request=None, quarantine=None, model_binding=None,
        symbol_candidate=None, catalog_registration=None, require_model_binding=True,
        reviewed_by="engineer:gamal", reviewed_on="2026-08-12",
        assumptions=("Missing records remain visible.",),
    )
    other_dxf = b"0\nSECTION\n2\nBLOCKS\n0\nENDSEC\n0\nEOF\n"
    other_request, other_quarantine = _quarantine(
        tmp_path, content=other_dxf, suffix="-conflict"
    )
    conflict = review_manufacturer_package(
        bundle, review_id="manufacturer-review:conflict",
        scope=ManufacturerReviewScope.QUARANTINED_ARTIFACT,
        retrieval_request=other_request, quarantine=other_quarantine,
        model_binding=None, symbol_candidate=None, catalog_registration=None,
        require_model_binding=False, reviewed_by="engineer:gamal",
        reviewed_on="2026-08-12", assumptions=("Conflicts stop acceptance.",),
    )
    assert missing.complete is False
    assert ManufacturerReviewOutcome.UNKNOWN in {item.outcome for item in missing.findings}
    assert conflict.complete is False
    assert ManufacturerReviewOutcome.CONFLICT in {item.outcome for item in conflict.findings}
