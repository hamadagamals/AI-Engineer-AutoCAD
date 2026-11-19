"""Reviewed manufacturer-property sidecars and CAD-to-symbol candidates."""

from __future__ import annotations

import hashlib
import json
import os
import re
import tempfile
from dataclasses import dataclass
from datetime import UTC, date, datetime
from enum import Enum
from pathlib import Path
from typing import Any, Callable, Mapping

from agent.exceptions import ConfirmationRequiredError, ManufacturerEvidenceError
from core.electrical_model import (
    Confidence,
    ElectricalProjectModel,
    JsonRecord,
    PrivacyClassification,
    Provenance,
    SourceKind,
)
from core.manufacturer_evidence import (
    ManufacturerArtifactKind,
    ManufacturerEvidenceBundle,
    ManufacturerPropertyClaim,
)
from integrations.electrical_symbols import (
    ElectricalSymbolCatalog,
    register_catalog_definition,
    validate_catalog_definition,
)
from integrations.manufacturer_retrieval import (
    ManufacturerRetrievalRequest,
    QuarantineRecord,
)


MODEL_BINDING_VERSION = "m23.manufacturer_model_binding.v1"
SYMBOL_CANDIDATE_VERSION = "m23.manufacturer_symbol_candidate.v1"
CATALOG_REGISTRATION_VERSION = "m23.manufacturer_catalog_registration.v1"
REVIEW_VERSION = "m23.manufacturer_review.v1"
_IDENTIFIER = re.compile(r"[a-z][a-z0-9_.:-]{0,127}")
_SHA256 = re.compile(r"[0-9a-f]{64}")


class ManufacturerTargetKind(str, Enum):
    """Shared-model object collections that may receive a reviewed product sidecar."""

    DEVICE = "device"
    LOAD = "load"
    PROTECTION_DEVICE = "protection_device"
    PANEL = "panel"
    CABLE = "cable"


class ManufacturerReviewScope(str, Enum):
    """Required evidence depth for deterministic read-only review."""

    EVIDENCE = "evidence"
    QUARANTINED_ARTIFACT = "quarantined_artifact"
    SYMBOL_CANDIDATE = "symbol_candidate"
    CATALOG_REGISTRATION = "catalog_registration"


class ManufacturerReviewOutcome(str, Enum):
    PASS = "pass"
    UNKNOWN = "unknown"
    CONFLICT = "conflict"


@dataclass(frozen=True, slots=True)
class MappedManufacturerProperty(JsonRecord):
    """One property copied exactly from a reviewed claim and artifact identity."""

    claim_id: str
    artifact_id: str
    artifact_content_sha256: str
    property_name: str
    value: str
    unit: str | None
    source_locator: str
    applicability: str

    def __post_init__(self) -> None:
        _identifier(self.claim_id, "mapped manufacturer claim ID")
        _identifier(self.artifact_id, "mapped manufacturer artifact ID")
        _sha256(self.artifact_content_sha256, "mapped artifact content hash")
        _text(self.property_name, "mapped manufacturer property name", 128)
        _text(self.value, "mapped manufacturer property value", 256)
        if self.unit is not None:
            _text(self.unit, "mapped manufacturer property unit", 64)
        _text(self.source_locator, "mapped manufacturer source locator", 256)
        _text(self.applicability, "mapped manufacturer applicability", 512)


@dataclass(frozen=True, slots=True)
class ManufacturerModelBinding(JsonRecord):
    """Self-hashed sidecar bound to one exact shared-model object and model hash."""

    schema_version: int
    binding_version: str
    binding_id: str
    project_id: str
    context_id: str
    model_sha256: str
    target_kind: ManufacturerTargetKind
    target_id: str
    manufacturer_id: str
    product_id: str
    part_number: str
    bundle_sha256: str
    properties: tuple[MappedManufacturerProperty, ...]
    provenance: Provenance
    reviewed_by: str
    reviewed_on: str
    assumptions: tuple[str, ...]
    sidecar_mapping_performed: bool
    shared_model_mutation_performed: bool
    catalog_registration_performed: bool
    drawing_mutation_performed: bool
    binding_sha256: str

    def __post_init__(self) -> None:
        if self.schema_version != 1 or self.binding_version != MODEL_BINDING_VERSION:
            raise ManufacturerEvidenceError("Unsupported manufacturer model-binding version.")
        for value, name in (
            (self.binding_id, "manufacturer model binding ID"),
            (self.project_id, "manufacturer binding project ID"),
            (self.target_id, "manufacturer binding target ID"),
            (self.manufacturer_id, "manufacturer binding manufacturer ID"),
            (self.product_id, "manufacturer binding product ID"),
        ):
            _identifier(value, name)
        if re.fullmatch(r"[0-9a-f]{32}", self.context_id) is None:
            raise ManufacturerEvidenceError("Manufacturer binding context ID is invalid.")
        _sha256(self.model_sha256, "manufacturer binding model hash")
        if not isinstance(self.target_kind, ManufacturerTargetKind):
            raise ManufacturerEvidenceError("Manufacturer binding target kind is invalid.")
        _text(self.part_number, "manufacturer binding part number", 128)
        _sha256(self.bundle_sha256, "manufacturer evidence bundle hash")
        if not isinstance(self.properties, tuple) or not self.properties or any(
            not isinstance(item, MappedManufacturerProperty) for item in self.properties
        ):
            raise ManufacturerEvidenceError("Manufacturer binding requires mapped properties.")
        claim_ids = [item.claim_id for item in self.properties]
        if len(claim_ids) != len(set(claim_ids)):
            raise ManufacturerEvidenceError("Mapped manufacturer claim IDs must be unique.")
        if not isinstance(self.provenance, Provenance):
            raise ManufacturerEvidenceError("Manufacturer binding provenance is invalid.")
        if (
            self.provenance.source_kind is not SourceKind.MANUFACTURER_EVIDENCE
            or self.provenance.confidence is not Confidence.REVIEWED
            or self.provenance.evidence_sha256 != self.bundle_sha256
            or self.provenance.provider_egress_allowed
        ):
            raise ManufacturerEvidenceError("Manufacturer binding provenance is unsafe or mismatched.")
        _text(self.reviewed_by, "manufacturer binding reviewer", 128)
        _calendar_date(self.reviewed_on, "manufacturer binding review date")
        _texts(self.assumptions, "manufacturer binding assumptions", 1, 16, 512)
        if self.sidecar_mapping_performed is not True:
            raise ManufacturerEvidenceError("Manufacturer sidecar mapping must be explicit.")
        if (
            self.shared_model_mutation_performed
            or self.catalog_registration_performed
            or self.drawing_mutation_performed
        ):
            raise ManufacturerEvidenceError(
                "Manufacturer sidecar cannot claim model mutation, catalog registration, or drawing mutation."
            )
        _verify_hash(self, "binding_sha256", "Manufacturer model binding")


@dataclass(frozen=True, slots=True)
class ManufacturerSymbolCandidate(JsonRecord):
    """Reviewed exact catalog proposal bound to one quarantined manufacturer CAD asset."""

    schema_version: int
    candidate_version: str
    candidate_id: str
    manufacturer_id: str
    product_id: str
    part_number: str
    bundle_sha256: str
    artifact_id: str
    artifact_content_sha256: str
    quarantine_record_sha256: str
    symbol_id: str
    block_name: str
    symbol_definition_sha256: str
    symbol_definition_json: str
    reviewed_by: str
    reviewed_on: str
    assumptions: tuple[str, ...]
    geometry_derivation_automated: bool
    catalog_registration_performed: bool
    drawing_mutation_performed: bool
    candidate_sha256: str

    def __post_init__(self) -> None:
        if self.schema_version != 1 or self.candidate_version != SYMBOL_CANDIDATE_VERSION:
            raise ManufacturerEvidenceError("Unsupported manufacturer symbol-candidate version.")
        for value, name in (
            (self.candidate_id, "manufacturer symbol candidate ID"),
            (self.manufacturer_id, "symbol candidate manufacturer ID"),
            (self.product_id, "symbol candidate product ID"),
            (self.artifact_id, "symbol candidate artifact ID"),
        ):
            _identifier(value, name)
        _text(self.part_number, "symbol candidate part number", 128)
        for value, name in (
            (self.bundle_sha256, "symbol candidate bundle hash"),
            (self.artifact_content_sha256, "symbol candidate artifact hash"),
            (self.quarantine_record_sha256, "symbol candidate quarantine hash"),
            (self.symbol_definition_sha256, "symbol candidate definition hash"),
        ):
            _sha256(value, name)
        _text(self.symbol_id, "symbol candidate symbol ID", 64)
        _text(self.block_name, "symbol candidate block name", 64)
        if not isinstance(self.symbol_definition_json, str) or not 2 <= len(
            self.symbol_definition_json.encode("utf-8")
        ) <= 65_536:
            raise ManufacturerEvidenceError("Symbol candidate definition JSON is outside limits.")
        try:
            proposal = json.loads(self.symbol_definition_json)
        except json.JSONDecodeError as exc:
            raise ManufacturerEvidenceError("Symbol candidate definition JSON is invalid.") from exc
        symbol = validate_catalog_definition(proposal)
        if (
            symbol.symbol_id != self.symbol_id
            or symbol.block_name != self.block_name
            or symbol.definition_sha256 != self.symbol_definition_sha256
        ):
            raise ManufacturerEvidenceError("Symbol candidate definition identity is mismatched.")
        _text(self.reviewed_by, "manufacturer symbol reviewer", 128)
        _calendar_date(self.reviewed_on, "manufacturer symbol review date")
        _texts(self.assumptions, "manufacturer symbol assumptions", 1, 16, 512)
        if self.geometry_derivation_automated:
            raise ManufacturerEvidenceError("Manufacturer CAD geometry cannot be auto-approved.")
        if self.catalog_registration_performed or self.drawing_mutation_performed:
            raise ManufacturerEvidenceError(
                "Symbol candidate creation cannot claim catalog registration or drawing mutation."
            )
        _verify_hash(self, "candidate_sha256", "Manufacturer symbol candidate")

    def catalog_proposal(self) -> dict[str, object]:
        """Return the exact validated proposal for a later separately approved registration."""
        value = json.loads(self.symbol_definition_json)
        if not isinstance(value, dict):
            raise ManufacturerEvidenceError("Symbol candidate proposal is not an object.")
        return value


@dataclass(frozen=True, slots=True)
class ManufacturerCatalogRegistration(JsonRecord):
    """Durable source lineage for one backed-up catalog registration."""

    schema_version: int
    registration_version: str
    registration_id: str
    candidate_id: str
    candidate_sha256: str
    manufacturer_id: str
    product_id: str
    part_number: str
    bundle_sha256: str
    artifact_id: str
    artifact_content_sha256: str
    quarantine_record_sha256: str
    symbol_id: str
    block_name: str
    symbol_definition_sha256: str
    catalog_sha256_before: str
    catalog_sha256_after: str
    catalog_version_before: int
    catalog_version_after: int
    backup_sha256: str
    provenance_relative_path: str
    registered_by: str
    registered_at: str
    catalog_registration_performed: bool
    provenance_write_performed: bool
    drawing_placement_performed: bool
    drawing_mutation_performed: bool
    registration_sha256: str

    def __post_init__(self) -> None:
        if self.schema_version != 1 or self.registration_version != CATALOG_REGISTRATION_VERSION:
            raise ManufacturerEvidenceError("Unsupported manufacturer catalog-registration version.")
        for value, name in (
            (self.registration_id, "manufacturer catalog registration ID"),
            (self.candidate_id, "manufacturer catalog candidate ID"),
            (self.manufacturer_id, "manufacturer catalog manufacturer ID"),
            (self.product_id, "manufacturer catalog product ID"),
            (self.artifact_id, "manufacturer catalog artifact ID"),
        ):
            _identifier(value, name)
        for value, name in (
            (self.candidate_sha256, "manufacturer catalog candidate hash"),
            (self.bundle_sha256, "manufacturer catalog bundle hash"),
            (self.artifact_content_sha256, "manufacturer catalog artifact hash"),
            (self.quarantine_record_sha256, "manufacturer catalog quarantine hash"),
            (self.symbol_definition_sha256, "manufacturer catalog definition hash"),
            (self.catalog_sha256_before, "manufacturer catalog prior hash"),
            (self.catalog_sha256_after, "manufacturer catalog resulting hash"),
            (self.backup_sha256, "manufacturer catalog backup hash"),
        ):
            _sha256(value, name)
        _text(self.part_number, "manufacturer catalog part number", 128)
        _text(self.symbol_id, "manufacturer catalog symbol ID", 64)
        _text(self.block_name, "manufacturer catalog block name", 64)
        if (
            not isinstance(self.catalog_version_before, int)
            or isinstance(self.catalog_version_before, bool)
            or self.catalog_version_before < 1
            or self.catalog_version_after != self.catalog_version_before + 1
        ):
            raise ManufacturerEvidenceError("Manufacturer catalog versions are inconsistent.")
        if self.catalog_sha256_before != self.backup_sha256:
            raise ManufacturerEvidenceError("Manufacturer catalog backup does not match prior catalog.")
        if self.catalog_sha256_after == self.catalog_sha256_before:
            raise ManufacturerEvidenceError("Manufacturer catalog registration did not change catalog content.")
        if (
            not isinstance(self.provenance_relative_path, str)
            or Path(self.provenance_relative_path).is_absolute()
            or len(Path(self.provenance_relative_path).parts) != 1
            or ".." in Path(self.provenance_relative_path).parts
        ):
            raise ManufacturerEvidenceError("Manufacturer catalog provenance path is unsafe.")
        _text(self.registered_by, "manufacturer catalog registrar", 128)
        _utc_timestamp(self.registered_at, "manufacturer catalog registration timestamp")
        if self.catalog_registration_performed is not True or self.provenance_write_performed is not True:
            raise ManufacturerEvidenceError("Manufacturer catalog registration and provenance write must be explicit.")
        if self.drawing_placement_performed or self.drawing_mutation_performed:
            raise ManufacturerEvidenceError("Catalog registration cannot claim drawing placement or mutation.")
        _verify_hash(self, "registration_sha256", "Manufacturer catalog registration")


@dataclass(frozen=True, slots=True)
class ManufacturerReviewFinding(JsonRecord):
    subject_id: str
    outcome: ManufacturerReviewOutcome
    message: str

    def __post_init__(self) -> None:
        _identifier(self.subject_id, "manufacturer review subject ID")
        if not isinstance(self.outcome, ManufacturerReviewOutcome):
            raise ManufacturerEvidenceError("Manufacturer review outcome is invalid.")
        _text(self.message, "manufacturer review message", 512)


@dataclass(frozen=True, slots=True)
class ManufacturerReviewResult(JsonRecord):
    schema_version: int
    review_version: str
    review_id: str
    scope: ManufacturerReviewScope
    bundle_sha256: str
    retrieval_request_sha256: str | None
    quarantine_record_sha256: str | None
    model_binding_sha256: str | None
    symbol_candidate_sha256: str | None
    catalog_registration_sha256: str | None
    findings: tuple[ManufacturerReviewFinding, ...]
    complete: bool
    reviewed_by: str
    reviewed_on: str
    assumptions: tuple[str, ...]
    provider_egress_performed: bool
    retrieval_performed_by_review: bool
    catalog_registration_performed_by_review: bool
    drawing_mutation_performed: bool
    result_sha256: str

    def __post_init__(self) -> None:
        if self.schema_version != 1 or self.review_version != REVIEW_VERSION:
            raise ManufacturerEvidenceError("Unsupported manufacturer review version.")
        _identifier(self.review_id, "manufacturer review ID")
        if not isinstance(self.scope, ManufacturerReviewScope):
            raise ManufacturerEvidenceError("Manufacturer review scope is invalid.")
        _sha256(self.bundle_sha256, "manufacturer review bundle hash")
        for value, name in (
            (self.retrieval_request_sha256, "manufacturer review retrieval hash"),
            (self.quarantine_record_sha256, "manufacturer review quarantine hash"),
            (self.model_binding_sha256, "manufacturer review model-binding hash"),
            (self.symbol_candidate_sha256, "manufacturer review candidate hash"),
            (self.catalog_registration_sha256, "manufacturer review registration hash"),
        ):
            if value is not None:
                _sha256(value, name)
        if not isinstance(self.findings, tuple) or not self.findings or any(
            not isinstance(item, ManufacturerReviewFinding) for item in self.findings
        ):
            raise ManufacturerEvidenceError("Manufacturer review requires typed findings.")
        expected_complete = all(
            item.outcome is ManufacturerReviewOutcome.PASS for item in self.findings
        )
        if self.complete is not expected_complete:
            raise ManufacturerEvidenceError("Manufacturer review completeness is inconsistent.")
        _text(self.reviewed_by, "manufacturer review reviewer", 128)
        _calendar_date(self.reviewed_on, "manufacturer review date")
        _texts(self.assumptions, "manufacturer review assumptions", 1, 16, 512)
        if (
            self.provider_egress_performed
            or self.retrieval_performed_by_review
            or self.catalog_registration_performed_by_review
            or self.drawing_mutation_performed
        ):
            raise ManufacturerEvidenceError("Manufacturer review cannot claim an external action.")
        _verify_hash(self, "result_sha256", "Manufacturer review result")


def create_model_binding(
    model: ElectricalProjectModel,
    bundle: ManufacturerEvidenceBundle,
    *,
    binding_id: str,
    target_kind: ManufacturerTargetKind,
    target_id: str,
    claim_ids: tuple[str, ...],
    captured_at: str,
    privacy: PrivacyClassification,
    reviewed_by: str,
    reviewed_on: str,
    assumptions: tuple[str, ...],
) -> ManufacturerModelBinding:
    """Map exact reviewed claims to one existing object without mutating the model."""
    if not isinstance(model, ElectricalProjectModel) or not isinstance(
        bundle, ManufacturerEvidenceBundle
    ):
        raise ManufacturerEvidenceError("Manufacturer mapping requires typed model and evidence.")
    if model.project_id != bundle.project_id:
        raise ManufacturerEvidenceError("Manufacturer bundle is bound to a different project.")
    if not isinstance(target_kind, ManufacturerTargetKind):
        raise ManufacturerEvidenceError("Manufacturer mapping target kind is invalid.")
    collection_name, attribute = {
        ManufacturerTargetKind.DEVICE: ("devices", "device_id"),
        ManufacturerTargetKind.LOAD: ("loads", "load_id"),
        ManufacturerTargetKind.PROTECTION_DEVICE: ("protection_devices", "protection_id"),
        ManufacturerTargetKind.PANEL: ("panels", "panel_id"),
        ManufacturerTargetKind.CABLE: ("cables", "cable_id"),
    }[target_kind]
    if not any(getattr(item, attribute) == target_id for item in getattr(model, collection_name)):
        raise ManufacturerEvidenceError("Manufacturer mapping target does not exist in exact model.")
    if not isinstance(claim_ids, tuple) or not claim_ids or len(claim_ids) != len(set(claim_ids)):
        raise ManufacturerEvidenceError("Manufacturer mapping claim IDs must be a non-empty unique tuple.")
    claims = {item.claim_id: item for item in bundle.property_claims}
    if any(item not in claims for item in claim_ids):
        raise ManufacturerEvidenceError("Manufacturer mapping references an unknown property claim.")
    artifacts = {item.artifact_id: item for item in bundle.artifacts}
    mapped = tuple(_mapped_property(claims[item], artifacts) for item in claim_ids)
    model_sha256 = _canonical_sha256(model.to_dict())
    provenance = Provenance(
        SourceKind.MANUFACTURER_EVIDENCE,
        captured_at,
        Confidence.REVIEWED,
        privacy,
        False,
        bundle.bundle_sha256,
    )
    payload: dict[str, Any] = {
        "schema_version": 1, "binding_version": MODEL_BINDING_VERSION,
        "binding_id": binding_id, "project_id": model.project_id,
        "context_id": model.context_id, "model_sha256": model_sha256,
        "target_kind": target_kind, "target_id": target_id,
        "manufacturer_id": bundle.manufacturer.manufacturer_id,
        "product_id": bundle.product.product_id, "part_number": bundle.product.part_number,
        "bundle_sha256": bundle.bundle_sha256, "properties": mapped,
        "provenance": provenance, "reviewed_by": reviewed_by, "reviewed_on": reviewed_on,
        "assumptions": assumptions, "sidecar_mapping_performed": True,
        "shared_model_mutation_performed": False, "catalog_registration_performed": False,
        "drawing_mutation_performed": False,
    }
    return ManufacturerModelBinding(**payload, binding_sha256=_canonical_sha256(payload))


def create_symbol_candidate(
    bundle: ManufacturerEvidenceBundle,
    retrieval_request: ManufacturerRetrievalRequest,
    quarantine: QuarantineRecord,
    *,
    candidate_id: str,
    artifact_id: str,
    symbol_proposal: dict[str, object],
    reviewed_by: str,
    reviewed_on: str,
    assumptions: tuple[str, ...],
) -> ManufacturerSymbolCandidate:
    """Bind a manually reviewed symbol proposal to an exact quarantined CAD asset."""
    if (
        not isinstance(bundle, ManufacturerEvidenceBundle)
        or not isinstance(retrieval_request, ManufacturerRetrievalRequest)
        or not isinstance(quarantine, QuarantineRecord)
    ):
        raise ManufacturerEvidenceError(
            "Symbol candidate requires typed evidence, retrieval approval, and quarantine."
        )
    artifact = next((item for item in bundle.artifacts if item.artifact_id == artifact_id), None)
    if artifact is None or artifact.kind is not ManufacturerArtifactKind.CAD_ASSET:
        raise ManufacturerEvidenceError("Symbol candidate requires a known CAD artifact.")
    if not artifact.quarantined:
        raise ManufacturerEvidenceError("Symbol candidate CAD artifact is not quarantined.")
    if (
        retrieval_request.manufacturer_id != bundle.manufacturer.manufacturer_id
        or retrieval_request.product_id != bundle.product.product_id
        or retrieval_request.artifact_id != artifact.artifact_id
        or retrieval_request.locator != artifact.locator
        or quarantine.request_id != retrieval_request.request_id
        or quarantine.request_sha256 != retrieval_request.request_sha256
    ):
        raise ManufacturerEvidenceError("CAD retrieval approval does not match evidence and quarantine.")
    if (
        quarantine.artifact_id != artifact.artifact_id
        or quarantine.locator != artifact.locator
        or quarantine.content_sha256 != artifact.content_sha256
        or quarantine.byte_count != artifact.byte_count
        or quarantine.media_type != artifact.media_type
    ):
        raise ManufacturerEvidenceError("Quarantine record does not match the exact CAD artifact.")
    symbol = validate_catalog_definition(symbol_proposal)
    definition_json = json.dumps(
        symbol_proposal, ensure_ascii=False, sort_keys=True, separators=(",", ":")
    )
    payload: dict[str, Any] = {
        "schema_version": 1, "candidate_version": SYMBOL_CANDIDATE_VERSION,
        "candidate_id": candidate_id, "manufacturer_id": bundle.manufacturer.manufacturer_id,
        "product_id": bundle.product.product_id, "part_number": bundle.product.part_number,
        "bundle_sha256": bundle.bundle_sha256, "artifact_id": artifact.artifact_id,
        "artifact_content_sha256": artifact.content_sha256,
        "quarantine_record_sha256": quarantine.record_sha256,
        "symbol_id": symbol.symbol_id, "block_name": symbol.block_name,
        "symbol_definition_sha256": symbol.definition_sha256,
        "symbol_definition_json": definition_json, "reviewed_by": reviewed_by,
        "reviewed_on": reviewed_on, "assumptions": assumptions,
        "geometry_derivation_automated": False, "catalog_registration_performed": False,
        "drawing_mutation_performed": False,
    }
    return ManufacturerSymbolCandidate(**payload, candidate_sha256=_canonical_sha256(payload))


def register_manufacturer_symbol_candidate(
    candidate: ManufacturerSymbolCandidate,
    catalog_path: Path,
    backup_directory: Path,
    provenance_directory: Path,
    *,
    confirmed: bool = False,
    registered_by: str,
    clock: Callable[[], datetime] | None = None,
) -> ManufacturerCatalogRegistration:
    """Register one exact candidate and durably preserve source lineage or roll back."""
    if not confirmed:
        raise ConfirmationRequiredError(
            "Manufacturer symbol catalog registration requires explicit confirmation."
        )
    if not isinstance(candidate, ManufacturerSymbolCandidate):
        raise ManufacturerEvidenceError("Manufacturer catalog registration requires a typed candidate.")
    _text(registered_by, "manufacturer catalog registrar", 128)
    now = (clock or (lambda: datetime.now(UTC)))()
    if not isinstance(now, datetime) or now.tzinfo is None:
        raise ManufacturerEvidenceError("Manufacturer catalog registration clock must be timezone-aware.")
    registered_at = now.astimezone(UTC).isoformat(timespec="microseconds").replace("+00:00", "Z")
    resolved_catalog = catalog_path.resolve(strict=True)
    before_bytes = resolved_catalog.read_bytes()
    before_sha256 = hashlib.sha256(before_bytes).hexdigest()
    before_version = ElectricalSymbolCatalog.load(resolved_catalog).catalog_version
    proposal = candidate.catalog_proposal()
    result = register_catalog_definition(
        resolved_catalog,
        backup_directory,
        proposal,
        confirmed=True,
        clock=lambda: now,
    )
    provenance_filename = f"manufacturer-{candidate.candidate_sha256}.json"
    provenance_path = provenance_directory.resolve() / provenance_filename
    provenance_created = False
    try:
        after_bytes = resolved_catalog.read_bytes()
        after_sha256 = hashlib.sha256(after_bytes).hexdigest()
        backup_bytes = result.backup_path.read_bytes()
        if hashlib.sha256(backup_bytes).hexdigest() != before_sha256:
            raise ManufacturerEvidenceError("Manufacturer catalog registration backup verification failed.")
        if (
            result.catalog_version != before_version + 1
            or result.symbol.symbol_id != candidate.symbol_id
            or result.symbol.block_name != candidate.block_name
            or result.symbol.definition_sha256 != candidate.symbol_definition_sha256
        ):
            raise ManufacturerEvidenceError("Manufacturer catalog registration result mismatched candidate.")
        payload: dict[str, Any] = {
            "schema_version": 1,
            "registration_version": CATALOG_REGISTRATION_VERSION,
            "registration_id": f"manufacturer-registration:{candidate.candidate_id}",
            "candidate_id": candidate.candidate_id,
            "candidate_sha256": candidate.candidate_sha256,
            "manufacturer_id": candidate.manufacturer_id,
            "product_id": candidate.product_id,
            "part_number": candidate.part_number,
            "bundle_sha256": candidate.bundle_sha256,
            "artifact_id": candidate.artifact_id,
            "artifact_content_sha256": candidate.artifact_content_sha256,
            "quarantine_record_sha256": candidate.quarantine_record_sha256,
            "symbol_id": candidate.symbol_id,
            "block_name": candidate.block_name,
            "symbol_definition_sha256": candidate.symbol_definition_sha256,
            "catalog_sha256_before": before_sha256,
            "catalog_sha256_after": after_sha256,
            "catalog_version_before": before_version,
            "catalog_version_after": result.catalog_version,
            "backup_sha256": hashlib.sha256(backup_bytes).hexdigest(),
            "provenance_relative_path": provenance_filename,
            "registered_by": registered_by,
            "registered_at": registered_at,
            "catalog_registration_performed": True,
            "provenance_write_performed": True,
            "drawing_placement_performed": False,
            "drawing_mutation_performed": False,
        }
        registration = ManufacturerCatalogRegistration(
            **payload, registration_sha256=_canonical_sha256(payload)
        )
        _write_new_json(provenance_path, registration.to_dict())
        provenance_created = True
        persisted = json.loads(provenance_path.read_text(encoding="utf-8"))
        if persisted != registration.to_dict():
            raise ManufacturerEvidenceError("Manufacturer catalog provenance verification failed.")
        return registration
    except Exception as exc:
        cleanup_error: OSError | None = None
        if provenance_created:
            try:
                provenance_path.unlink()
            except OSError as cleanup_exc:
                cleanup_error = cleanup_exc
        _restore_catalog(resolved_catalog, before_bytes, before_sha256)
        if cleanup_error is not None:
            raise ManufacturerEvidenceError(
                "Prior catalog was restored, but failed provenance cleanup requires review."
            ) from cleanup_error
        if isinstance(exc, ManufacturerEvidenceError):
            raise
        raise ManufacturerEvidenceError(
            "Manufacturer catalog registration failed; prior catalog was restored."
        ) from exc


def review_manufacturer_package(
    bundle: ManufacturerEvidenceBundle,
    *,
    review_id: str,
    scope: ManufacturerReviewScope,
    retrieval_request: ManufacturerRetrievalRequest | None,
    quarantine: QuarantineRecord | None,
    model_binding: ManufacturerModelBinding | None,
    symbol_candidate: ManufacturerSymbolCandidate | None,
    catalog_registration: ManufacturerCatalogRegistration | None,
    require_model_binding: bool,
    reviewed_by: str,
    reviewed_on: str,
    assumptions: tuple[str, ...],
) -> ManufacturerReviewResult:
    """Review exact upstream records without performing retrieval, registration, or drafting."""
    if not isinstance(bundle, ManufacturerEvidenceBundle):
        raise ManufacturerEvidenceError("Manufacturer review requires typed evidence.")
    if not isinstance(scope, ManufacturerReviewScope) or not isinstance(require_model_binding, bool):
        raise ManufacturerEvidenceError("Manufacturer review scope/options are invalid.")
    findings = [
        ManufacturerReviewFinding(
            "manufacturer:evidence", ManufacturerReviewOutcome.PASS,
            "Manufacturer, product, artifacts, claims, and bundle SHA-256 are valid.",
        )
    ]
    levels = {
        ManufacturerReviewScope.EVIDENCE: 0,
        ManufacturerReviewScope.QUARANTINED_ARTIFACT: 1,
        ManufacturerReviewScope.SYMBOL_CANDIDATE: 2,
        ManufacturerReviewScope.CATALOG_REGISTRATION: 3,
    }
    level = levels[scope]
    artifact = None
    if retrieval_request is not None:
        artifact = next(
            (item for item in bundle.artifacts if item.artifact_id == retrieval_request.artifact_id),
            None,
        )
    if level >= 1:
        if retrieval_request is None:
            findings.append(_review_finding("manufacturer:retrieval", ManufacturerReviewOutcome.UNKNOWN, "Exact retrieval approval is missing."))
        elif artifact is None:
            findings.append(_review_finding("manufacturer:retrieval", ManufacturerReviewOutcome.CONFLICT, "Retrieval artifact is absent from evidence bundle."))
        elif (
            retrieval_request.manufacturer_id != bundle.manufacturer.manufacturer_id
            or retrieval_request.product_id != bundle.product.product_id
            or retrieval_request.locator != artifact.locator
            or retrieval_request.approved_host not in bundle.manufacturer.official_domains
        ):
            findings.append(_review_finding("manufacturer:retrieval", ManufacturerReviewOutcome.CONFLICT, "Retrieval approval conflicts with manufacturer evidence."))
        else:
            findings.append(_review_finding("manufacturer:retrieval", ManufacturerReviewOutcome.PASS, "Exact approved retrieval is bound to product and official host."))
        if quarantine is None:
            findings.append(_review_finding("manufacturer:quarantine", ManufacturerReviewOutcome.UNKNOWN, "Quarantine record is missing."))
        elif retrieval_request is None or artifact is None:
            findings.append(_review_finding("manufacturer:quarantine", ManufacturerReviewOutcome.CONFLICT, "Quarantine cannot be bound without retrieval evidence."))
        elif (
            quarantine.request_sha256 != retrieval_request.request_sha256
            or quarantine.artifact_id != artifact.artifact_id
            or quarantine.locator != artifact.locator
            or quarantine.content_sha256 != artifact.content_sha256
            or quarantine.media_type != artifact.media_type
            or quarantine.byte_count != artifact.byte_count
        ):
            findings.append(_review_finding("manufacturer:quarantine", ManufacturerReviewOutcome.CONFLICT, "Quarantine record conflicts with exact artifact or approval."))
        else:
            findings.append(_review_finding("manufacturer:quarantine", ManufacturerReviewOutcome.PASS, "Quarantined bytes match exact approved artifact identity."))
    if require_model_binding:
        if model_binding is None:
            findings.append(_review_finding("manufacturer:model-binding", ManufacturerReviewOutcome.UNKNOWN, "Required shared-model sidecar is missing."))
        elif (
            model_binding.bundle_sha256 != bundle.bundle_sha256
            or model_binding.manufacturer_id != bundle.manufacturer.manufacturer_id
            or model_binding.product_id != bundle.product.product_id
            or model_binding.project_id != bundle.project_id
        ):
            findings.append(_review_finding("manufacturer:model-binding", ManufacturerReviewOutcome.CONFLICT, "Shared-model sidecar conflicts with evidence bundle."))
        else:
            findings.append(_review_finding("manufacturer:model-binding", ManufacturerReviewOutcome.PASS, "Reviewed properties are bound to the exact shared model and object."))
    if level >= 2:
        if symbol_candidate is None:
            findings.append(_review_finding("manufacturer:symbol-candidate", ManufacturerReviewOutcome.UNKNOWN, "Reviewed symbol candidate is missing."))
        elif quarantine is None:
            findings.append(_review_finding("manufacturer:symbol-candidate", ManufacturerReviewOutcome.CONFLICT, "Symbol candidate cannot be bound without quarantine."))
        elif (
            symbol_candidate.bundle_sha256 != bundle.bundle_sha256
            or symbol_candidate.product_id != bundle.product.product_id
            or symbol_candidate.quarantine_record_sha256 != quarantine.record_sha256
            or symbol_candidate.artifact_content_sha256 != quarantine.content_sha256
        ):
            findings.append(_review_finding("manufacturer:symbol-candidate", ManufacturerReviewOutcome.CONFLICT, "Symbol candidate conflicts with evidence or quarantine."))
        else:
            findings.append(_review_finding("manufacturer:symbol-candidate", ManufacturerReviewOutcome.PASS, "Reviewed catalog proposal is bound to exact quarantined CAD."))
    if level >= 3:
        if catalog_registration is None:
            findings.append(_review_finding("manufacturer:catalog-registration", ManufacturerReviewOutcome.UNKNOWN, "Catalog registration lineage is missing."))
        elif symbol_candidate is None:
            findings.append(_review_finding("manufacturer:catalog-registration", ManufacturerReviewOutcome.CONFLICT, "Catalog lineage cannot be bound without candidate."))
        elif (
            catalog_registration.candidate_sha256 != symbol_candidate.candidate_sha256
            or catalog_registration.bundle_sha256 != bundle.bundle_sha256
            or catalog_registration.symbol_definition_sha256
            != symbol_candidate.symbol_definition_sha256
        ):
            findings.append(_review_finding("manufacturer:catalog-registration", ManufacturerReviewOutcome.CONFLICT, "Catalog lineage conflicts with exact symbol candidate."))
        else:
            findings.append(_review_finding("manufacturer:catalog-registration", ManufacturerReviewOutcome.PASS, "Backed-up catalog registration retains exact source lineage."))
    complete = all(item.outcome is ManufacturerReviewOutcome.PASS for item in findings)
    payload: dict[str, Any] = {
        "schema_version": 1, "review_version": REVIEW_VERSION, "review_id": review_id,
        "scope": scope, "bundle_sha256": bundle.bundle_sha256,
        "retrieval_request_sha256": retrieval_request.request_sha256 if retrieval_request else None,
        "quarantine_record_sha256": quarantine.record_sha256 if quarantine else None,
        "model_binding_sha256": model_binding.binding_sha256 if model_binding else None,
        "symbol_candidate_sha256": symbol_candidate.candidate_sha256 if symbol_candidate else None,
        "catalog_registration_sha256": (
            catalog_registration.registration_sha256 if catalog_registration else None
        ),
        "findings": tuple(findings), "complete": complete, "reviewed_by": reviewed_by,
        "reviewed_on": reviewed_on, "assumptions": assumptions,
        "provider_egress_performed": False, "retrieval_performed_by_review": False,
        "catalog_registration_performed_by_review": False,
        "drawing_mutation_performed": False,
    }
    return ManufacturerReviewResult(**payload, result_sha256=_canonical_sha256(payload))


def parse_model_binding(value: object) -> ManufacturerModelBinding:
    item = _mapping(value, "manufacturer model binding")
    _exact_keys(item, ManufacturerModelBinding, "manufacturer model binding")
    properties = []
    raw_properties = item["properties"]
    if not isinstance(raw_properties, list):
        raise ManufacturerEvidenceError("Mapped manufacturer properties must be a JSON array.")
    for raw in raw_properties:
        prop = _mapping(raw, "mapped manufacturer property")
        _exact_keys(prop, MappedManufacturerProperty, "mapped manufacturer property")
        properties.append(MappedManufacturerProperty(**prop))
    raw_provenance = _mapping(item["provenance"], "manufacturer binding provenance")
    if set(raw_provenance) != set(Provenance.__dataclass_fields__):
        raise ManufacturerEvidenceError("Manufacturer binding provenance fields are invalid.")
    provenance = Provenance(
        _enum_value(SourceKind, raw_provenance["source_kind"], "provenance source kind"),
        raw_provenance["captured_at"],
        _enum_value(Confidence, raw_provenance["confidence"], "provenance confidence"),
        _enum_value(
            PrivacyClassification, raw_provenance["privacy"], "provenance privacy"
        ),
        raw_provenance["provider_egress_allowed"], raw_provenance["evidence_sha256"],
    )
    assumptions = item["assumptions"]
    if not isinstance(assumptions, list):
        raise ManufacturerEvidenceError("Manufacturer binding assumptions must be a JSON array.")
    return ManufacturerModelBinding(
        **{**item, "target_kind": _enum_value(
               ManufacturerTargetKind, item["target_kind"], "manufacturer target kind"
           ),
           "properties": tuple(properties), "provenance": provenance,
           "assumptions": tuple(assumptions)}
    )


def parse_symbol_candidate(value: object) -> ManufacturerSymbolCandidate:
    item = _mapping(value, "manufacturer symbol candidate")
    _exact_keys(item, ManufacturerSymbolCandidate, "manufacturer symbol candidate")
    assumptions = item["assumptions"]
    if not isinstance(assumptions, list):
        raise ManufacturerEvidenceError("Manufacturer symbol assumptions must be a JSON array.")
    return ManufacturerSymbolCandidate(**{**item, "assumptions": tuple(assumptions)})


def parse_catalog_registration(value: object) -> ManufacturerCatalogRegistration:
    item = _mapping(value, "manufacturer catalog registration")
    _exact_keys(item, ManufacturerCatalogRegistration, "manufacturer catalog registration")
    return ManufacturerCatalogRegistration(**item)


def _review_finding(
    subject: str, outcome: ManufacturerReviewOutcome, message: str
) -> ManufacturerReviewFinding:
    return ManufacturerReviewFinding(subject, outcome, message)


def _mapped_property(
    claim: ManufacturerPropertyClaim, artifacts: dict[str, object]
) -> MappedManufacturerProperty:
    artifact = artifacts[claim.artifact_id]
    return MappedManufacturerProperty(
        claim.claim_id, claim.artifact_id, artifact.content_sha256, claim.property_name,
        claim.value, claim.unit, claim.source_locator, claim.applicability,
    )


def _canonical_sha256(value: object) -> str:
    def convert(item: object) -> object:
        if isinstance(item, Enum): return item.value
        if isinstance(item, JsonRecord): return item.to_dict()
        if isinstance(item, tuple): return [convert(child) for child in item]
        if isinstance(item, dict): return {str(key): convert(child) for key, child in item.items()}
        return item
    encoded = json.dumps(convert(value), ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest()


def _verify_hash(record: JsonRecord, field: str, name: str) -> None:
    value = getattr(record, field)
    _sha256(value, f"{name} hash")
    payload = record.to_dict()
    del payload[field]
    if value != _canonical_sha256(payload):
        raise ManufacturerEvidenceError(f"{name} SHA-256 does not match content.")


def _identifier(value: object, name: str) -> None:
    if not isinstance(value, str) or _IDENTIFIER.fullmatch(value) is None:
        raise ManufacturerEvidenceError(f"{name} is invalid.")


def _text(value: object, name: str, maximum: int) -> None:
    if not isinstance(value, str) or not value.strip() or value != value.strip() or len(value.encode("utf-8")) > maximum:
        raise ManufacturerEvidenceError(f"{name} is invalid or exceeds {maximum} UTF-8 bytes.")


def _texts(values: object, name: str, minimum: int, maximum: int, maximum_bytes: int) -> None:
    if not isinstance(values, tuple) or not minimum <= len(values) <= maximum:
        raise ManufacturerEvidenceError(f"{name} must contain {minimum} through {maximum} entries.")
    for value in values: _text(value, name, maximum_bytes)
    if len(values) != len(set(values)): raise ManufacturerEvidenceError(f"{name} must not contain duplicates.")


def _calendar_date(value: object, name: str) -> None:
    if not isinstance(value, str) or re.fullmatch(r"\d{4}-\d{2}-\d{2}", value) is None:
        raise ManufacturerEvidenceError(f"{name} must use YYYY-MM-DD.")
    try: date.fromisoformat(value)
    except ValueError as exc: raise ManufacturerEvidenceError(f"{name} is invalid.") from exc


def _sha256(value: object, name: str) -> None:
    if not isinstance(value, str) or _SHA256.fullmatch(value) is None:
        raise ManufacturerEvidenceError(f"{name} must be lowercase SHA-256.")


def _utc_timestamp(value: object, name: str) -> None:
    if not isinstance(value, str) or not value.endswith("Z"):
        raise ManufacturerEvidenceError(f"{name} must be a UTC timestamp.")
    try:
        datetime.fromisoformat(value[:-1] + "+00:00")
    except ValueError as exc:
        raise ManufacturerEvidenceError(f"{name} is invalid.") from exc


def _write_new_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    content = (json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")) + "\n").encode("utf-8")
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
            raise ManufacturerEvidenceError("Manufacturer provenance identity already exists.") from exc
        temporary.unlink()
    finally:
        if temporary.exists():
            temporary.unlink()


def _restore_catalog(path: Path, content: bytes, expected_sha256: str) -> None:
    descriptor, temporary_name = tempfile.mkstemp(prefix=f".{path.name}.restore.", dir=path.parent)
    temporary = Path(temporary_name)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
        if hashlib.sha256(path.read_bytes()).hexdigest() != expected_sha256:
            raise ManufacturerEvidenceError(
                "Manufacturer catalog automatic restore verification failed; use the backup."
            )
    except ManufacturerEvidenceError:
        raise
    except OSError as exc:
        raise ManufacturerEvidenceError(
            "Manufacturer catalog automatic restore failed; use the backup."
        ) from exc
    finally:
        if temporary.exists():
            temporary.unlink()


def _mapping(value: object, name: str) -> dict[str, Any]:
    if not isinstance(value, dict) or any(not isinstance(key, str) for key in value):
        raise ManufacturerEvidenceError(f"{name} must be a JSON object with string keys.")
    return value


def _exact_keys(value: Mapping[str, object], record_type: type, name: str) -> None:
    if set(value) != set(record_type.__dataclass_fields__):
        raise ManufacturerEvidenceError(f"{name} fields do not match the supported schema.")


def _enum_value(enum_type: type[Enum], value: object, name: str) -> Enum:
    try:
        return enum_type(value)
    except (TypeError, ValueError) as exc:
        raise ManufacturerEvidenceError(f"{name} is not supported.") from exc
