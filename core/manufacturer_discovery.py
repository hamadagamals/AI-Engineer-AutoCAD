"""Project-scoped manufacturer requirements and deterministic candidate comparison."""

from __future__ import annotations

import hashlib
import json
import re
from dataclasses import dataclass
from datetime import date
from decimal import Decimal, InvalidOperation
from enum import Enum
from typing import Any
from urllib.parse import urlsplit

from agent.exceptions import ManufacturerEvidenceError
from core.electrical_model import JsonRecord


REQUEST_VERSION = "m23.manufacturer_discovery_request.v1"
RESULT_VERSION = "m23.manufacturer_discovery_result.v1"
SELECTION_VERSION = "m23.manufacturer_candidate_selection.v1"
_IDENTIFIER = re.compile(r"[a-z][a-z0-9_.:-]{0,127}")
_SHA256 = re.compile(r"[0-9a-f]{64}")


class RequirementOperator(str, Enum):
    EXACT = "exact"
    MINIMUM = "minimum"
    MAXIMUM = "maximum"


class CandidateOutcome(str, Enum):
    PASS = "pass"
    FAIL = "fail"
    UNKNOWN = "unknown"
    CONFLICT = "conflict"


@dataclass(frozen=True, slots=True)
class ManufacturerRequirement(JsonRecord):
    requirement_id: str
    property_name: str
    operator: RequirementOperator
    required_value: str
    unit: str | None
    source_kind: str
    source_id: str
    source_sha256: str
    mandatory: bool

    def __post_init__(self) -> None:
        _identifier(self.requirement_id, "manufacturer requirement ID")
        _text(self.property_name, "manufacturer requirement property", 128)
        if not isinstance(self.operator, RequirementOperator):
            raise ManufacturerEvidenceError("Manufacturer requirement operator is invalid.")
        _value(self.required_value, "manufacturer required value")
        if self.unit is not None:
            _text(self.unit, "manufacturer requirement unit", 64)
        if self.source_kind not in {"shared_model", "calculation", "engineer_input"}:
            raise ManufacturerEvidenceError("Manufacturer requirement source kind is invalid.")
        _identifier(self.source_id, "manufacturer requirement source ID")
        _sha256(self.source_sha256, "manufacturer requirement source hash")
        if not isinstance(self.mandatory, bool):
            raise ManufacturerEvidenceError("Manufacturer requirement mandatory flag is invalid.")


@dataclass(frozen=True, slots=True)
class ManufacturerDiscoveryRequest(JsonRecord):
    schema_version: int
    request_version: str
    request_id: str
    project_id: str
    model_sha256: str
    product_category: str
    search_text: str
    official_domains: tuple[str, ...]
    requirements: tuple[ManufacturerRequirement, ...]
    approved_by: str
    approved_on: str
    egress_approved: bool
    automatic_selection_approved: bool
    automatic_download_approved: bool
    catalog_registration_approved: bool
    drawing_mutation_approved: bool
    request_sha256: str

    def __post_init__(self) -> None:
        if self.schema_version != 1 or self.request_version != REQUEST_VERSION:
            raise ManufacturerEvidenceError("Unsupported manufacturer discovery-request version.")
        _identifier(self.request_id, "manufacturer discovery request ID")
        _identifier(self.project_id, "manufacturer discovery project ID")
        _sha256(self.model_sha256, "manufacturer discovery model hash")
        _text(self.product_category, "manufacturer product category", 128)
        _text(self.search_text, "manufacturer search text", 1024)
        if not isinstance(self.official_domains, tuple) or not 1 <= len(self.official_domains) <= 32:
            raise ManufacturerEvidenceError("Manufacturer discovery official domains are invalid.")
        if len(self.official_domains) != len(set(self.official_domains)):
            raise ManufacturerEvidenceError("Manufacturer discovery official domains must be unique.")
        for domain in self.official_domains:
            if not isinstance(domain, str) or domain != domain.lower() or not _domain(domain):
                raise ManufacturerEvidenceError("Manufacturer discovery official domain is invalid.")
        if not isinstance(self.requirements, tuple) or not 1 <= len(self.requirements) <= 64:
            raise ManufacturerEvidenceError("Manufacturer discovery requirements are invalid.")
        if any(not isinstance(item, ManufacturerRequirement) for item in self.requirements):
            raise ManufacturerEvidenceError("Manufacturer discovery requirement is invalid.")
        ids = [item.requirement_id for item in self.requirements]
        if len(ids) != len(set(ids)):
            raise ManufacturerEvidenceError("Manufacturer discovery requirement IDs must be unique.")
        _text(self.approved_by, "manufacturer discovery approver", 128)
        _calendar_date(self.approved_on, "manufacturer discovery approval date")
        if self.egress_approved is not True:
            raise ManufacturerEvidenceError("Manufacturer discovery requires explicit egress approval.")
        if any(
            (
                self.automatic_selection_approved,
                self.automatic_download_approved,
                self.catalog_registration_approved,
                self.drawing_mutation_approved,
            )
        ):
            raise ManufacturerEvidenceError(
                "Manufacturer discovery cannot approve selection, download, catalog, or drawing actions."
            )
        _verify_hash(self, "request_sha256", "Manufacturer discovery request")


@dataclass(frozen=True, slots=True)
class CandidateProperty(JsonRecord):
    property_name: str
    value: str
    unit: str | None
    source_locator: str

    def __post_init__(self) -> None:
        _text(self.property_name, "candidate property name", 128)
        _value(self.value, "candidate property value")
        if self.unit is not None:
            _text(self.unit, "candidate property unit", 64)
        _text(self.source_locator, "candidate property source locator", 256)


@dataclass(frozen=True, slots=True)
class ManufacturerCandidate(JsonRecord):
    candidate_id: str
    manufacturer_name: str
    brand_name: str
    family_name: str
    model: str
    part_number: str
    product_url: str
    official_host: str
    source_retrieved_on: str
    source_content_sha256: str
    properties: tuple[CandidateProperty, ...]

    def __post_init__(self) -> None:
        _identifier(self.candidate_id, "manufacturer candidate ID")
        for value, name, maximum in (
            (self.manufacturer_name, "candidate manufacturer", 256),
            (self.brand_name, "candidate brand", 128),
            (self.family_name, "candidate family", 256),
            (self.model, "candidate model", 128),
            (self.part_number, "candidate part number", 128),
        ):
            _text(value, name, maximum)
        parsed = urlsplit(self.product_url)
        if (
            parsed.scheme != "https" or not parsed.hostname or parsed.username is not None
            or parsed.password is not None or parsed.port not in (None, 443) or parsed.fragment
            or parsed.hostname != self.official_host
        ):
            raise ManufacturerEvidenceError("Manufacturer candidate URL/host is invalid.")
        if self.official_host != self.official_host.lower() or not _domain(self.official_host):
            raise ManufacturerEvidenceError("Manufacturer candidate official host is invalid.")
        _calendar_date(self.source_retrieved_on, "manufacturer candidate retrieval date")
        _sha256(self.source_content_sha256, "manufacturer candidate source hash")
        if not isinstance(self.properties, tuple) or not 1 <= len(self.properties) <= 128:
            raise ManufacturerEvidenceError("Manufacturer candidate properties are invalid.")
        if any(not isinstance(item, CandidateProperty) for item in self.properties):
            raise ManufacturerEvidenceError("Manufacturer candidate property is invalid.")


@dataclass(frozen=True, slots=True)
class RequirementFinding(JsonRecord):
    requirement_id: str
    outcome: CandidateOutcome
    candidate_value: str | None
    unit: str | None
    source_locator: str | None
    message: str

    def __post_init__(self) -> None:
        _identifier(self.requirement_id, "manufacturer finding requirement ID")
        if not isinstance(self.outcome, CandidateOutcome):
            raise ManufacturerEvidenceError("Manufacturer finding outcome is invalid.")
        if self.candidate_value is not None:
            _value(self.candidate_value, "manufacturer finding candidate value")
        if self.unit is not None:
            _text(self.unit, "manufacturer finding unit", 64)
        if self.source_locator is not None:
            _text(self.source_locator, "manufacturer finding source locator", 256)
        _text(self.message, "manufacturer finding message", 512)


@dataclass(frozen=True, slots=True)
class CandidateComparison(JsonRecord):
    candidate_id: str
    part_number: str
    product_url: str
    findings: tuple[RequirementFinding, ...]
    mandatory_requirements_satisfied: bool


@dataclass(frozen=True, slots=True)
class ManufacturerDiscoveryResult(JsonRecord):
    schema_version: int
    result_version: str
    discovery_id: str
    request_sha256: str
    provider_name: str
    candidates: tuple[ManufacturerCandidate, ...]
    comparisons: tuple[CandidateComparison, ...]
    complete: bool
    reviewed_by: str
    reviewed_on: str
    assumptions: tuple[str, ...]
    provider_egress_performed: bool
    automatic_selection_performed: bool
    automatic_download_performed: bool
    catalog_registration_performed: bool
    drawing_mutation_performed: bool
    result_sha256: str

    def __post_init__(self) -> None:
        if self.schema_version != 1 or self.result_version != RESULT_VERSION:
            raise ManufacturerEvidenceError("Unsupported manufacturer discovery-result version.")
        _identifier(self.discovery_id, "manufacturer discovery result ID")
        _sha256(self.request_sha256, "manufacturer discovery request hash")
        _identifier(self.provider_name, "manufacturer discovery provider")
        if not isinstance(self.candidates, tuple) or not 1 <= len(self.candidates) <= 32:
            raise ManufacturerEvidenceError("Manufacturer discovery candidates are invalid.")
        if any(not isinstance(item, ManufacturerCandidate) for item in self.candidates):
            raise ManufacturerEvidenceError("Manufacturer discovery candidate is invalid.")
        if not isinstance(self.comparisons, tuple) or len(self.comparisons) != len(self.candidates):
            raise ManufacturerEvidenceError("Manufacturer discovery comparisons are invalid.")
        if any(not isinstance(item, CandidateComparison) for item in self.comparisons):
            raise ManufacturerEvidenceError("Manufacturer discovery comparison is invalid.")
        if not isinstance(self.complete, bool) or self.complete != all(
            all(finding.outcome not in {CandidateOutcome.UNKNOWN, CandidateOutcome.CONFLICT}
                for finding in comparison.findings)
            for comparison in self.comparisons
        ):
            raise ManufacturerEvidenceError("Manufacturer discovery completeness is invalid.")
        _text(self.reviewed_by, "manufacturer discovery reviewer", 128)
        _calendar_date(self.reviewed_on, "manufacturer discovery review date")
        _texts(self.assumptions, "manufacturer discovery assumptions", 1, 16, 512)
        if not isinstance(self.provider_egress_performed, bool):
            raise ManufacturerEvidenceError("Manufacturer discovery provider-egress flag is invalid.")
        if any(
            (
                self.automatic_selection_performed,
                self.automatic_download_performed,
                self.catalog_registration_performed,
                self.drawing_mutation_performed,
            )
        ):
            raise ManufacturerEvidenceError(
                "Manufacturer discovery cannot select, download, register, or mutate a drawing."
            )
        _verify_hash(self, "result_sha256", "Manufacturer discovery result")


@dataclass(frozen=True, slots=True)
class ManufacturerCandidateSelection(JsonRecord):
    schema_version: int
    selection_version: str
    selection_id: str
    discovery_result_sha256: str
    candidate_id: str
    part_number: str
    selection_scope: str
    selected_by: str
    selected_on: str
    rationale: str
    product_approval_performed: bool
    automatic_download_performed: bool
    catalog_registration_performed: bool
    drawing_mutation_performed: bool
    selection_sha256: str

    def __post_init__(self) -> None:
        if self.schema_version != 1 or self.selection_version != SELECTION_VERSION:
            raise ManufacturerEvidenceError("Unsupported manufacturer selection version.")
        _identifier(self.selection_id, "manufacturer selection ID")
        _sha256(self.discovery_result_sha256, "manufacturer discovery result hash")
        _identifier(self.candidate_id, "manufacturer selection candidate ID")
        _text(self.part_number, "manufacturer selection part number", 128)
        if self.selection_scope != "candidate_for_follow_on_review":
            raise ManufacturerEvidenceError("Manufacturer selection scope is invalid.")
        _text(self.selected_by, "manufacturer selection reviewer", 128)
        _calendar_date(self.selected_on, "manufacturer selection date")
        _text(self.rationale, "manufacturer selection rationale", 512)
        if any((
            self.product_approval_performed,
            self.automatic_download_performed,
            self.catalog_registration_performed,
            self.drawing_mutation_performed,
        )):
            raise ManufacturerEvidenceError(
                "Candidate selection cannot approve a product or perform a follow-on action."
            )
        _verify_hash(self, "selection_sha256", "Manufacturer candidate selection")


def create_discovery_request(
    *, request_id: str, project_id: str, model_sha256: str, product_category: str,
    search_text: str, official_domains: tuple[str, ...],
    requirements: tuple[ManufacturerRequirement, ...], approved_by: str, approved_on: str,
) -> ManufacturerDiscoveryRequest:
    payload: dict[str, Any] = {
        "schema_version": 1, "request_version": REQUEST_VERSION,
        "request_id": request_id, "project_id": project_id, "model_sha256": model_sha256,
        "product_category": product_category, "search_text": search_text,
        "official_domains": official_domains, "requirements": requirements,
        "approved_by": approved_by, "approved_on": approved_on, "egress_approved": True,
        "automatic_selection_approved": False, "automatic_download_approved": False,
        "catalog_registration_approved": False, "drawing_mutation_approved": False,
    }
    return ManufacturerDiscoveryRequest(**payload, request_sha256=_canonical_sha256(payload))


def parse_discovery_request(value: object) -> ManufacturerDiscoveryRequest:
    """Parse exact JSON discovery approval and revalidate its self hash."""
    if not isinstance(value, dict) or set(value) != set(ManufacturerDiscoveryRequest.__dataclass_fields__):
        raise ManufacturerEvidenceError("Manufacturer discovery request fields are invalid.")
    raw_domains = value["official_domains"]
    raw_requirements = value["requirements"]
    if not isinstance(raw_domains, list) or not isinstance(raw_requirements, list):
        raise ManufacturerEvidenceError("Manufacturer discovery arrays are invalid.")
    requirements = []
    for raw in raw_requirements:
        if not isinstance(raw, dict) or set(raw) != set(ManufacturerRequirement.__dataclass_fields__):
            raise ManufacturerEvidenceError("Manufacturer discovery requirement fields are invalid.")
        try:
            operator = RequirementOperator(raw["operator"])
        except (TypeError, ValueError) as exc:
            raise ManufacturerEvidenceError("Manufacturer discovery operator is unsupported.") from exc
        requirements.append(ManufacturerRequirement(**{**raw, "operator": operator}))
    return ManufacturerDiscoveryRequest(
        **{**value, "official_domains": tuple(raw_domains), "requirements": tuple(requirements)}
    )


def parse_discovery_result(value: object) -> ManufacturerDiscoveryResult:
    """Parse an exact self-hashed discovery result for a later local review decision."""
    if not isinstance(value, dict) or set(value) != set(ManufacturerDiscoveryResult.__dataclass_fields__):
        raise ManufacturerEvidenceError("Manufacturer discovery result fields are invalid.")
    candidates = []
    for raw_candidate in value.get("candidates", []):
        if not isinstance(raw_candidate, dict) or set(raw_candidate) != set(ManufacturerCandidate.__dataclass_fields__):
            raise ManufacturerEvidenceError("Manufacturer discovery candidate fields are invalid.")
        properties = raw_candidate.get("properties")
        if not isinstance(properties, list):
            raise ManufacturerEvidenceError("Manufacturer discovery candidate properties are invalid.")
        for item in properties:
            if not isinstance(item, dict) or set(item) != set(CandidateProperty.__dataclass_fields__):
                raise ManufacturerEvidenceError("Manufacturer discovery candidate property fields are invalid.")
        candidates.append(ManufacturerCandidate(
            **{**raw_candidate, "properties": tuple(CandidateProperty(**item) for item in properties)}
        ))
    comparisons = []
    for raw_comparison in value.get("comparisons", []):
        if not isinstance(raw_comparison, dict) or set(raw_comparison) != set(CandidateComparison.__dataclass_fields__):
            raise ManufacturerEvidenceError("Manufacturer discovery comparison fields are invalid.")
        findings = []
        for raw_finding in raw_comparison.get("findings", []):
            if not isinstance(raw_finding, dict) or set(raw_finding) != set(RequirementFinding.__dataclass_fields__):
                raise ManufacturerEvidenceError("Manufacturer discovery finding fields are invalid.")
            try:
                outcome = CandidateOutcome(raw_finding["outcome"])
            except (TypeError, ValueError) as exc:
                raise ManufacturerEvidenceError("Manufacturer discovery finding outcome is invalid.") from exc
            findings.append(RequirementFinding(**{**raw_finding, "outcome": outcome}))
        comparisons.append(CandidateComparison(
            **{**raw_comparison, "findings": tuple(findings)}
        ))
    assumptions = value.get("assumptions")
    if not isinstance(assumptions, list):
        raise ManufacturerEvidenceError("Manufacturer discovery assumptions are invalid.")
    return ManufacturerDiscoveryResult(**{
        **value,
        "candidates": tuple(candidates),
        "comparisons": tuple(comparisons),
        "assumptions": tuple(assumptions),
    })


def select_candidate_for_review(
    result: ManufacturerDiscoveryResult,
    *, selection_id: str, candidate_id: str, selected_by: str, selected_on: str,
    rationale: str,
) -> ManufacturerCandidateSelection:
    """Record an engineer's candidate choice without implying technical product approval."""
    if not isinstance(result, ManufacturerDiscoveryResult):
        raise ManufacturerEvidenceError("Manufacturer selection requires a typed discovery result.")
    matches = [candidate for candidate in result.candidates if candidate.candidate_id == candidate_id]
    if len(matches) != 1:
        raise ManufacturerEvidenceError("Selected manufacturer candidate is not in the discovery result.")
    payload: dict[str, Any] = {
        "schema_version": 1,
        "selection_version": SELECTION_VERSION,
        "selection_id": selection_id,
        "discovery_result_sha256": result.result_sha256,
        "candidate_id": candidate_id,
        "part_number": matches[0].part_number,
        "selection_scope": "candidate_for_follow_on_review",
        "selected_by": selected_by,
        "selected_on": selected_on,
        "rationale": rationale,
        "product_approval_performed": False,
        "automatic_download_performed": False,
        "catalog_registration_performed": False,
        "drawing_mutation_performed": False,
    }
    return ManufacturerCandidateSelection(
        **payload, selection_sha256=_canonical_sha256(payload)
    )


def compare_candidates(
    request: ManufacturerDiscoveryRequest, candidates: tuple[ManufacturerCandidate, ...],
    *, discovery_id: str, provider_name: str, reviewed_by: str, reviewed_on: str,
    assumptions: tuple[str, ...], provider_egress_performed: bool = True,
) -> ManufacturerDiscoveryResult:
    if not isinstance(request, ManufacturerDiscoveryRequest):
        raise ManufacturerEvidenceError("Manufacturer discovery requires a typed request.")
    if not isinstance(candidates, tuple) or not 1 <= len(candidates) <= 32:
        raise ManufacturerEvidenceError("Manufacturer discovery candidate count is invalid.")
    if any(item.official_host not in request.official_domains for item in candidates):
        raise ManufacturerEvidenceError("Manufacturer candidate host was not approved for discovery.")
    comparisons = tuple(_compare_candidate(request, item) for item in candidates)
    complete = all(
        all(finding.outcome not in {CandidateOutcome.UNKNOWN, CandidateOutcome.CONFLICT}
            for finding in item.findings)
        for item in comparisons
    )
    payload: dict[str, Any] = {
        "schema_version": 1, "result_version": RESULT_VERSION,
        "discovery_id": discovery_id, "request_sha256": request.request_sha256,
        "provider_name": provider_name, "candidates": candidates, "comparisons": comparisons,
        "complete": complete, "reviewed_by": reviewed_by, "reviewed_on": reviewed_on,
        "assumptions": assumptions, "provider_egress_performed": provider_egress_performed,
        "automatic_selection_performed": False, "automatic_download_performed": False,
        "catalog_registration_performed": False, "drawing_mutation_performed": False,
    }
    return ManufacturerDiscoveryResult(**payload, result_sha256=_canonical_sha256(payload))


def _compare_candidate(
    request: ManufacturerDiscoveryRequest, candidate: ManufacturerCandidate
) -> CandidateComparison:
    findings = []
    for requirement in request.requirements:
        matches = tuple(
            item for item in candidate.properties if item.property_name == requirement.property_name
        )
        if not matches:
            findings.append(RequirementFinding(
                requirement.requirement_id, CandidateOutcome.UNKNOWN, None, requirement.unit,
                None, "Official candidate evidence does not state this requirement.",
            ))
            continue
        if len(matches) != 1:
            findings.append(RequirementFinding(
                requirement.requirement_id, CandidateOutcome.CONFLICT, None, requirement.unit,
                None, "Official candidate evidence contains competing values.",
            ))
            continue
        prop = matches[0]
        if prop.unit != requirement.unit:
            findings.append(RequirementFinding(
                requirement.requirement_id, CandidateOutcome.CONFLICT, prop.value, prop.unit,
                prop.source_locator, "Candidate and requirement units differ.",
            ))
            continue
        outcome = _compare_values(requirement, prop.value)
        findings.append(RequirementFinding(
            requirement.requirement_id, outcome, prop.value, prop.unit, prop.source_locator,
            "Deterministic exact comparison completed." if requirement.operator is RequirementOperator.EXACT
            else "Deterministic decimal comparison completed.",
        ))
    satisfied = all(
        finding.outcome is CandidateOutcome.PASS
        for requirement, finding in zip(request.requirements, findings, strict=True)
        if requirement.mandatory
    )
    return CandidateComparison(
        candidate.candidate_id, candidate.part_number, candidate.product_url,
        tuple(findings), satisfied,
    )


def _compare_values(requirement: ManufacturerRequirement, candidate_value: str) -> CandidateOutcome:
    if requirement.operator is RequirementOperator.EXACT:
        return CandidateOutcome.PASS if candidate_value == requirement.required_value else CandidateOutcome.FAIL
    try:
        required = Decimal(requirement.required_value)
        candidate = Decimal(candidate_value)
    except InvalidOperation:
        return CandidateOutcome.CONFLICT
    if not required.is_finite() or not candidate.is_finite():
        return CandidateOutcome.CONFLICT
    if requirement.operator is RequirementOperator.MINIMUM:
        return CandidateOutcome.PASS if candidate >= required else CandidateOutcome.FAIL
    return CandidateOutcome.PASS if candidate <= required else CandidateOutcome.FAIL


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


def _sha256(value: object, name: str) -> None:
    if not isinstance(value, str) or _SHA256.fullmatch(value) is None:
        raise ManufacturerEvidenceError(f"{name} must be lowercase SHA-256.")


def _text(value: object, name: str, maximum: int) -> None:
    if (
        not isinstance(value, str) or not value.strip() or value != value.strip()
        or len(value.encode("utf-8")) > maximum or any(ord(character) < 32 for character in value)
    ):
        raise ManufacturerEvidenceError(f"{name} is invalid or exceeds {maximum} UTF-8 bytes.")


def _value(value: object, name: str) -> None:
    _text(value, name, 256)


def _texts(values: object, name: str, minimum: int, maximum: int, maximum_bytes: int) -> None:
    if not isinstance(values, tuple) or not minimum <= len(values) <= maximum:
        raise ManufacturerEvidenceError(f"{name} must contain {minimum} through {maximum} entries.")
    for value in values: _text(value, name, maximum_bytes)


def _calendar_date(value: object, name: str) -> None:
    if not isinstance(value, str) or re.fullmatch(r"\d{4}-\d{2}-\d{2}", value) is None:
        raise ManufacturerEvidenceError(f"{name} must use YYYY-MM-DD.")
    try: date.fromisoformat(value)
    except ValueError as exc: raise ManufacturerEvidenceError(f"{name} is invalid.") from exc


def _domain(value: str) -> bool:
    if len(value) > 253 or value.startswith(".") or value.endswith("."):
        return False
    labels = value.split(".")
    return len(labels) >= 2 and all(
        label and len(label) <= 63 and re.fullmatch(r"[a-z0-9](?:[a-z0-9-]*[a-z0-9])?", label)
        for label in labels
    )
