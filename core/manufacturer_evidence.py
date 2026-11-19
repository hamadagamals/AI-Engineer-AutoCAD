"""Immutable manufacturer evidence and quarantined component-asset contracts."""

from __future__ import annotations

import hashlib
import json
import re
from dataclasses import dataclass
from datetime import date
from enum import Enum
from typing import Any, Mapping
from urllib.parse import urlsplit

from agent.exceptions import ManufacturerEvidenceError
from core.electrical_model import JsonRecord


BUNDLE_VERSION = "m23.manufacturer_evidence.v1"
_IDENTIFIER = re.compile(r"[a-z][a-z0-9_.:-]{0,127}")
_SHA256 = re.compile(r"[0-9a-f]{64}")
_DATE = re.compile(r"\d{4}-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12]\d|3[01])")
_MEDIA_TYPES = {
    "application/pdf",
    "application/dxf",
    "application/acad",
    "application/octet-stream",
    "model/step",
    "application/step",
    "model/iges",
    "image/jpeg",
    "image/png",
}


class ManufacturerAccessBasis(str, Enum):
    """Lawful reviewed access basis for one artifact."""

    OFFICIAL_PUBLIC = "official_public"
    LICENSED_PORTAL = "licensed_portal"
    USER_PROVIDED_LICENSED = "user_provided_licensed"


class ManufacturerArtifactKind(str, Enum):
    """Supported evidence/asset classes."""

    DATA_SHEET = "data_sheet"
    MANUAL = "manual"
    CAD_ASSET = "cad_asset"


@dataclass(frozen=True, slots=True)
class ManufacturerIdentity(JsonRecord):
    """Exact manufacturer identity and approved official HTTPS domains."""

    manufacturer_id: str
    legal_name: str
    brand_name: str
    official_domains: tuple[str, ...]

    def __post_init__(self) -> None:
        _identifier(self.manufacturer_id, "manufacturer ID")
        _text(self.legal_name, "manufacturer legal name", 256)
        _text(self.brand_name, "manufacturer brand name", 128)
        _texts(self.official_domains, "manufacturer official domains", 1, 16, 253)
        for domain in self.official_domains:
            if domain != domain.lower() or _valid_domain(domain) is False:
                raise ManufacturerEvidenceError("Manufacturer official domain is invalid.")


@dataclass(frozen=True, slots=True)
class ManufacturerProductIdentity(JsonRecord):
    """An exact product/variant identity; family-only claims are not silently broadened."""

    product_id: str
    manufacturer_id: str
    family_name: str
    model: str
    part_number: str
    variant: str | None

    def __post_init__(self) -> None:
        _identifier(self.product_id, "manufacturer product ID")
        _identifier(self.manufacturer_id, "product manufacturer ID")
        _text(self.family_name, "product family", 256)
        _text(self.model, "product model", 128)
        _text(self.part_number, "product part number", 128)
        if self.variant is not None:
            _text(self.variant, "product variant", 128)


@dataclass(frozen=True, slots=True)
class ManufacturerArtifact(JsonRecord):
    """Exact downloaded/provided artifact identity; CAD remains quarantined pending review."""

    artifact_id: str
    product_id: str
    kind: ManufacturerArtifactKind
    title: str
    revision: str
    published_on: str | None
    retrieved_on: str
    access_basis: ManufacturerAccessBasis
    locator: str
    media_type: str
    byte_count: int
    content_sha256: str
    quarantined: bool
    executable_content_detected: bool
    license_reference: str

    def __post_init__(self) -> None:
        _identifier(self.artifact_id, "manufacturer artifact ID")
        _identifier(self.product_id, "artifact product ID")
        if not isinstance(self.kind, ManufacturerArtifactKind):
            raise ManufacturerEvidenceError("Manufacturer artifact kind is invalid.")
        _text(self.title, "manufacturer artifact title", 256)
        _text(self.revision, "manufacturer artifact revision", 128)
        if self.published_on is not None:
            _date(self.published_on, "manufacturer artifact publication date")
        _date(self.retrieved_on, "manufacturer artifact retrieval date")
        if self.published_on is not None and self.retrieved_on < self.published_on:
            raise ManufacturerEvidenceError("Artifact retrieval predates publication.")
        if not isinstance(self.access_basis, ManufacturerAccessBasis):
            raise ManufacturerEvidenceError("Manufacturer artifact access basis is invalid.")
        _text(self.locator, "manufacturer artifact locator", 2048)
        if self.access_basis is ManufacturerAccessBasis.OFFICIAL_PUBLIC:
            parsed = urlsplit(self.locator)
            if parsed.scheme != "https" or not parsed.hostname or parsed.username is not None:
                raise ManufacturerEvidenceError("Official public artifacts require a safe HTTPS locator.")
        elif not self.locator.startswith(("https://", "licensed:", "document:")):
            raise ManufacturerEvidenceError("Licensed artifact locator is unsupported.")
        if self.media_type not in _MEDIA_TYPES:
            raise ManufacturerEvidenceError("Manufacturer artifact media type is unsupported.")
        if (
            not isinstance(self.byte_count, int)
            or isinstance(self.byte_count, bool)
            or not 1 <= self.byte_count <= 100_000_000
        ):
            raise ManufacturerEvidenceError("Manufacturer artifact byte count is outside limits.")
        _sha256(self.content_sha256, "manufacturer artifact content hash")
        if self.kind is ManufacturerArtifactKind.CAD_ASSET and not self.quarantined:
            raise ManufacturerEvidenceError("CAD assets must remain quarantined until separate review.")
        if self.executable_content_detected:
            raise ManufacturerEvidenceError("Executable artifact content is forbidden.")
        _text(self.license_reference, "manufacturer artifact license reference", 512)


@dataclass(frozen=True, slots=True)
class ManufacturerPropertyClaim(JsonRecord):
    """One bounded engineer-reviewed property claim from an exact artifact location."""

    claim_id: str
    product_id: str
    artifact_id: str
    property_name: str
    value: str
    unit: str | None
    source_locator: str
    applicability: str
    reviewed_by: str
    reviewed_on: str
    assumptions: tuple[str, ...]

    def __post_init__(self) -> None:
        for value, name in (
            (self.claim_id, "manufacturer property claim ID"),
            (self.product_id, "property claim product ID"),
            (self.artifact_id, "property claim artifact ID"),
        ):
            _identifier(value, name)
        _text(self.property_name, "manufacturer property name", 128)
        _text(self.value, "manufacturer property value", 256)
        if self.unit is not None:
            _text(self.unit, "manufacturer property unit", 64)
        _text(self.source_locator, "manufacturer property source locator", 256)
        _text(self.applicability, "manufacturer property applicability", 512)
        _text(self.reviewed_by, "manufacturer property reviewer", 128)
        _date(self.reviewed_on, "manufacturer property review date")
        _texts(self.assumptions, "manufacturer property assumptions", 1, 16, 512)


@dataclass(frozen=True, slots=True)
class ManufacturerEvidenceBundle(JsonRecord):
    """Self-hashed review bundle that grants no retrieval, catalog, or drawing authority."""

    schema_version: int
    bundle_version: str
    bundle_id: str
    project_id: str
    manufacturer: ManufacturerIdentity
    product: ManufacturerProductIdentity
    artifacts: tuple[ManufacturerArtifact, ...]
    property_claims: tuple[ManufacturerPropertyClaim, ...]
    reviewed_by: str
    reviewed_on: str
    assumptions: tuple[str, ...]
    retrieval_performed_by_contract: bool
    provider_egress_performed: bool
    catalog_registration_performed: bool
    drawing_mutation_performed: bool
    bundle_sha256: str

    def __post_init__(self) -> None:
        if self.schema_version != 1 or self.bundle_version != BUNDLE_VERSION:
            raise ManufacturerEvidenceError("Unsupported manufacturer evidence-bundle version.")
        _identifier(self.bundle_id, "manufacturer evidence bundle ID")
        _identifier(self.project_id, "manufacturer evidence project ID")
        if not isinstance(self.manufacturer, ManufacturerIdentity):
            raise ManufacturerEvidenceError("Manufacturer identity is invalid.")
        if not isinstance(self.product, ManufacturerProductIdentity):
            raise ManufacturerEvidenceError("Manufacturer product identity is invalid.")
        if self.product.manufacturer_id != self.manufacturer.manufacturer_id:
            raise ManufacturerEvidenceError("Product is bound to a different manufacturer.")
        _records(self.artifacts, ManufacturerArtifact, "manufacturer artifacts", 1, 64)
        _records(
            self.property_claims,
            ManufacturerPropertyClaim,
            "manufacturer property claims",
            0,
            256,
        )
        artifact_ids = [item.artifact_id for item in self.artifacts]
        claim_ids = [item.claim_id for item in self.property_claims]
        if len(artifact_ids) != len(set(artifact_ids)) or len(claim_ids) != len(set(claim_ids)):
            raise ManufacturerEvidenceError("Manufacturer artifact and claim IDs must be unique.")
        if any(item.product_id != self.product.product_id for item in self.artifacts):
            raise ManufacturerEvidenceError("Artifact is bound to a different product.")
        known_artifacts = set(artifact_ids)
        if any(
            item.product_id != self.product.product_id or item.artifact_id not in known_artifacts
            for item in self.property_claims
        ):
            raise ManufacturerEvidenceError("Property claim binding is invalid.")
        for artifact in self.artifacts:
            if artifact.access_basis is ManufacturerAccessBasis.OFFICIAL_PUBLIC:
                host = urlsplit(artifact.locator).hostname
                if host not in self.manufacturer.official_domains:
                    raise ManufacturerEvidenceError("Official artifact host is not approved for manufacturer.")
        _text(self.reviewed_by, "manufacturer evidence reviewer", 128)
        _date(self.reviewed_on, "manufacturer evidence review date")
        _texts(self.assumptions, "manufacturer evidence assumptions", 1, 16, 512)
        if (
            self.retrieval_performed_by_contract
            or self.provider_egress_performed
            or self.catalog_registration_performed
            or self.drawing_mutation_performed
        ):
            raise ManufacturerEvidenceError(
                "Evidence contract cannot claim retrieval, provider egress, catalog registration, or drawing mutation."
            )
        _sha256(self.bundle_sha256, "manufacturer evidence bundle hash")
        payload = self.to_dict()
        del payload["bundle_sha256"]
        if self.bundle_sha256 != _canonical_sha256(payload):
            raise ManufacturerEvidenceError("Manufacturer evidence SHA-256 does not match content.")


def create_manufacturer_evidence_bundle(
    *,
    bundle_id: str,
    project_id: str,
    manufacturer: ManufacturerIdentity,
    product: ManufacturerProductIdentity,
    artifacts: tuple[ManufacturerArtifact, ...],
    property_claims: tuple[ManufacturerPropertyClaim, ...],
    reviewed_by: str,
    reviewed_on: str,
    assumptions: tuple[str, ...],
) -> ManufacturerEvidenceBundle:
    """Create a reviewed bundle without performing retrieval, registration, or drafting."""
    payload: dict[str, Any] = {
        "schema_version": 1,
        "bundle_version": BUNDLE_VERSION,
        "bundle_id": bundle_id,
        "project_id": project_id,
        "manufacturer": manufacturer,
        "product": product,
        "artifacts": artifacts,
        "property_claims": property_claims,
        "reviewed_by": reviewed_by,
        "reviewed_on": reviewed_on,
        "assumptions": assumptions,
        "retrieval_performed_by_contract": False,
        "provider_egress_performed": False,
        "catalog_registration_performed": False,
        "drawing_mutation_performed": False,
    }
    return ManufacturerEvidenceBundle(
        **payload, bundle_sha256=_canonical_sha256(payload)
    )


def parse_manufacturer_evidence_bundle(value: object) -> ManufacturerEvidenceBundle:
    """Parse an exact JSON-shaped evidence bundle and revalidate its self hash."""
    item = _mapping(value, "manufacturer evidence bundle")
    _exact_keys(item, ManufacturerEvidenceBundle, "manufacturer evidence bundle")
    manufacturer_value = _mapping(item["manufacturer"], "manufacturer identity")
    _exact_keys(manufacturer_value, ManufacturerIdentity, "manufacturer identity")
    domains = _array(manufacturer_value["official_domains"], "manufacturer official domains")
    manufacturer = ManufacturerIdentity(
        **{**manufacturer_value, "official_domains": tuple(domains)}
    )
    product_value = _mapping(item["product"], "manufacturer product")
    _exact_keys(product_value, ManufacturerProductIdentity, "manufacturer product")
    product = ManufacturerProductIdentity(**product_value)
    artifacts = []
    for raw in _array(item["artifacts"], "manufacturer artifacts"):
        artifact = _mapping(raw, "manufacturer artifact")
        _exact_keys(artifact, ManufacturerArtifact, "manufacturer artifact")
        artifacts.append(
            ManufacturerArtifact(
                **{
                    **artifact,
                    "kind": _enum(ManufacturerArtifactKind, artifact["kind"], "artifact kind"),
                    "access_basis": _enum(
                        ManufacturerAccessBasis, artifact["access_basis"], "artifact access basis"
                    ),
                }
            )
        )
    claims = []
    for raw in _array(item["property_claims"], "manufacturer property claims"):
        claim = _mapping(raw, "manufacturer property claim")
        _exact_keys(claim, ManufacturerPropertyClaim, "manufacturer property claim")
        assumptions = _array(claim["assumptions"], "manufacturer property assumptions")
        claims.append(ManufacturerPropertyClaim(**{**claim, "assumptions": tuple(assumptions)}))
    assumptions = _array(item["assumptions"], "manufacturer evidence assumptions")
    return ManufacturerEvidenceBundle(
        **{
            **item,
            "manufacturer": manufacturer,
            "product": product,
            "artifacts": tuple(artifacts),
            "property_claims": tuple(claims),
            "assumptions": tuple(assumptions),
        }
    )


def _canonical_sha256(value: object) -> str:
    def convert(item: object) -> object:
        if isinstance(item, Enum):
            return item.value
        if isinstance(item, JsonRecord):
            return item.to_dict()
        if isinstance(item, tuple):
            return [convert(child) for child in item]
        if isinstance(item, dict):
            return {str(key): convert(child) for key, child in item.items()}
        return item
    encoded = json.dumps(
        convert(value), ensure_ascii=False, sort_keys=True, separators=(",", ":")
    ).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest()


def _identifier(value: object, name: str) -> None:
    if not isinstance(value, str) or _IDENTIFIER.fullmatch(value) is None:
        raise ManufacturerEvidenceError(f"{name} is invalid.")


def _text(value: object, name: str, maximum: int) -> None:
    if (
        not isinstance(value, str)
        or not value.strip()
        or value != value.strip()
        or len(value.encode("utf-8")) > maximum
        or any(ord(character) < 32 for character in value)
    ):
        raise ManufacturerEvidenceError(f"{name} is invalid or exceeds {maximum} UTF-8 bytes.")


def _texts(values: object, name: str, minimum: int, maximum: int, maximum_bytes: int) -> None:
    if not isinstance(values, tuple) or not minimum <= len(values) <= maximum:
        raise ManufacturerEvidenceError(f"{name} must contain {minimum} through {maximum} entries.")
    for value in values:
        _text(value, name, maximum_bytes)
    if len(values) != len(set(values)):
        raise ManufacturerEvidenceError(f"{name} must not contain duplicates.")


def _records(values: object, record_type: type, name: str, minimum: int, maximum: int) -> None:
    if (
        not isinstance(values, tuple)
        or not minimum <= len(values) <= maximum
        or any(not isinstance(item, record_type) for item in values)
    ):
        raise ManufacturerEvidenceError(f"{name} is invalid or outside its count limit.")


def _date(value: object, name: str) -> None:
    if not isinstance(value, str) or _DATE.fullmatch(value) is None:
        raise ManufacturerEvidenceError(f"{name} must use YYYY-MM-DD.")
    try:
        date.fromisoformat(value)
    except ValueError as exc:
        raise ManufacturerEvidenceError(f"{name} is not a valid calendar date.") from exc


def _sha256(value: object, name: str) -> None:
    if not isinstance(value, str) or _SHA256.fullmatch(value) is None:
        raise ManufacturerEvidenceError(f"{name} must be lowercase SHA-256.")


def _valid_domain(domain: str) -> bool:
    if len(domain) > 253 or domain.startswith(".") or domain.endswith("."):
        return False
    labels = domain.split(".")
    return len(labels) >= 2 and all(
        label
        and len(label) <= 63
        and not label.startswith("-")
        and not label.endswith("-")
        and all(character.isalnum() or character == "-" for character in label)
        for label in labels
    )


def _mapping(value: object, name: str) -> dict[str, Any]:
    if not isinstance(value, dict) or any(not isinstance(key, str) for key in value):
        raise ManufacturerEvidenceError(f"{name} must be a JSON object with string keys.")
    return value


def _array(value: object, name: str) -> list[object]:
    if not isinstance(value, list):
        raise ManufacturerEvidenceError(f"{name} must be a JSON array.")
    return value


def _exact_keys(value: Mapping[str, object], record_type: type, name: str) -> None:
    if set(value) != set(record_type.__dataclass_fields__):
        raise ManufacturerEvidenceError(f"{name} fields do not match the supported schema.")


def _enum(enum_type: type[Enum], value: object, name: str) -> Enum:
    if not isinstance(value, str):
        raise ManufacturerEvidenceError(f"{name} is invalid.")
    try:
        return enum_type(value)
    except ValueError as exc:
        raise ManufacturerEvidenceError(f"{name} is unsupported.") from exc
