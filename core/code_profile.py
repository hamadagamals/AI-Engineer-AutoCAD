"""Immutable reviewed project code profiles with pinned-edition impact review."""

from __future__ import annotations

import hashlib
import json
import re
from dataclasses import dataclass
from datetime import date
from enum import Enum
from typing import Any, Mapping

from agent.exceptions import CodeProfileError
from core.electrical_model import JsonRecord


PROFILE_VERSION = "m22.code_profile.v1"
IMPACT_VERSION = "m22.edition_impact.v1"
_IDENTIFIER = re.compile(r"[a-z][a-z0-9_.:-]{0,127}")
_SHA256 = re.compile(r"[0-9a-f]{64}")
_DATE = re.compile(r"\d{4}-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12]\d|3[01])")


class EvidenceAccessBasis(str, Enum):
    """Reviewed lawful access basis without implying redistribution permission."""

    OFFICIAL_PUBLIC = "official_public"
    LICENSED_SUBSCRIPTION = "licensed_subscription"
    USER_PROVIDED_LICENSED = "user_provided_licensed"


class EditionRelationship(str, Enum):
    """Engineer-reviewed relationship to the currently pinned edition."""

    SAME = "same"
    NEWER = "newer"
    OLDER = "older"
    UNKNOWN = "unknown"


@dataclass(frozen=True, slots=True)
class CodeSourceIdentity(JsonRecord):
    """Identity and lawful-access metadata for one reviewed code source."""

    source_id: str
    authority_name: str
    standard_name: str
    edition: str
    publication_date: str
    effective_from: str
    effective_to: str | None
    access_basis: EvidenceAccessBasis
    locator: str
    retrieved_on: str
    content_sha256: str
    full_text_stored: bool = False

    def __post_init__(self) -> None:
        _identifier(self.source_id, "code source ID")
        _text(self.authority_name, "code authority name", 256)
        _text(self.standard_name, "standard name", 256)
        _text(self.edition, "code edition", 64)
        _date(self.publication_date, "publication date")
        _date(self.effective_from, "effective-from date")
        if self.effective_to is not None:
            _date(self.effective_to, "effective-to date")
            if self.effective_to < self.effective_from:
                raise CodeProfileError("Code source effective-to date precedes effective-from.")
        if not isinstance(self.access_basis, EvidenceAccessBasis):
            raise CodeProfileError("Code source access basis is invalid.")
        _text(self.locator, "code source locator", 1024)
        if self.access_basis is EvidenceAccessBasis.OFFICIAL_PUBLIC:
            if not self.locator.startswith("https://"):
                raise CodeProfileError("Official public code sources require an HTTPS locator.")
        elif not self.locator.startswith(("https://", "licensed:", "document:")):
            raise CodeProfileError("Licensed code source locator is unsupported.")
        _date(self.retrieved_on, "retrieval date")
        if self.retrieved_on < self.publication_date:
            raise CodeProfileError("Code source retrieval date precedes publication.")
        _sha256(self.content_sha256, "code source content hash")
        if self.full_text_stored is not False:
            raise CodeProfileError("Code profiles cannot claim stored full protected text.")


@dataclass(frozen=True, slots=True)
class CodeAuthority(JsonRecord):
    """One ordered authority whose exact source edition is pinned in the profile."""

    authority_id: str
    precedence: int
    source_id: str
    topics: tuple[str, ...]

    def __post_init__(self) -> None:
        _identifier(self.authority_id, "code authority ID")
        if (
            not isinstance(self.precedence, int)
            or isinstance(self.precedence, bool)
            or not 1 <= self.precedence <= 64
        ):
            raise CodeProfileError("Code authority precedence must be from 1 through 64.")
        _identifier(self.source_id, "code authority source ID")
        _texts(self.topics, "code authority topics", 1, 32, 128)


@dataclass(frozen=True, slots=True)
class ProjectCodeRequirement(JsonRecord):
    """One explicit project requirement with optional reviewed code-clause basis."""

    requirement_id: str
    statement: str
    source_id: str | None
    clause_locator: str | None

    def __post_init__(self) -> None:
        _identifier(self.requirement_id, "project requirement ID")
        _text(self.statement, "project requirement statement", 1024)
        if (self.source_id is None) != (self.clause_locator is None):
            raise CodeProfileError(
                "Project requirement source and clause locator must be supplied together."
            )
        if self.source_id is not None:
            _identifier(self.source_id, "project requirement source ID")
            _text(self.clause_locator, "project requirement clause locator", 256)


@dataclass(frozen=True, slots=True)
class ProjectCodeProfile(JsonRecord):
    """Self-hashed active project profile; source editions never change implicitly."""

    schema_version: int
    profile_version: str
    profile_id: str
    project_id: str
    jurisdiction: str
    project_type: str
    sources: tuple[CodeSourceIdentity, ...]
    authorities: tuple[CodeAuthority, ...]
    requirements: tuple[ProjectCodeRequirement, ...]
    reviewed_by: str
    reviewed_on: str
    assumptions: tuple[str, ...]
    provider_retrieval_performed: bool
    compliance_evaluation_performed: bool
    profile_sha256: str

    def __post_init__(self) -> None:
        if self.schema_version != 1 or self.profile_version != PROFILE_VERSION:
            raise CodeProfileError("Unsupported project code-profile version.")
        _identifier(self.profile_id, "code profile ID")
        _identifier(self.project_id, "code profile project ID")
        _text(self.jurisdiction, "code profile jurisdiction", 256)
        _text(self.project_type, "code profile project type", 256)
        _records(self.sources, CodeSourceIdentity, "code profile sources", 1, 64)
        _records(self.authorities, CodeAuthority, "code profile authorities", 1, 64)
        _records(self.requirements, ProjectCodeRequirement, "project requirements", 0, 256)
        source_ids = [item.source_id for item in self.sources]
        authority_ids = [item.authority_id for item in self.authorities]
        requirement_ids = [item.requirement_id for item in self.requirements]
        for values, name in (
            (source_ids, "source"), (authority_ids, "authority"),
            (requirement_ids, "requirement"),
        ):
            if len(values) != len(set(values)):
                raise CodeProfileError(f"Code profile {name} IDs must be unique.")
        precedence = [item.precedence for item in self.authorities]
        if precedence != list(range(1, len(self.authorities) + 1)):
            raise CodeProfileError("Code authorities must be ordered with contiguous precedence.")
        if len({item.source_id for item in self.authorities}) != len(self.authorities):
            raise CodeProfileError("Each code authority must pin one distinct source.")
        known_sources = set(source_ids)
        if any(item.source_id not in known_sources for item in self.authorities):
            raise CodeProfileError("Code authority references an unknown source.")
        if any(
            item.source_id is not None and item.source_id not in known_sources
            for item in self.requirements
        ):
            raise CodeProfileError("Project requirement references an unknown source.")
        _text(self.reviewed_by, "code profile reviewer", 128)
        _date(self.reviewed_on, "code profile review date")
        _texts(self.assumptions, "code profile assumptions", 1, 16, 512)
        if self.provider_retrieval_performed or self.compliance_evaluation_performed:
            raise CodeProfileError(
                "Profile creation cannot claim provider retrieval or compliance evaluation."
            )
        _sha256(self.profile_sha256, "code profile hash")
        payload = self.to_dict()
        del payload["profile_sha256"]
        if self.profile_sha256 != _canonical_sha256(payload):
            raise CodeProfileError("Project code-profile SHA-256 does not match its content.")


@dataclass(frozen=True, slots=True)
class EditionImpactAssessment(JsonRecord):
    """Self-hashed discovery record that cannot activate a discovered edition."""

    schema_version: int
    assessment_version: str
    assessment_id: str
    profile_id: str
    profile_sha256: str
    authority_id: str
    active_source_id: str
    discovered_source: CodeSourceIdentity
    relationship: EditionRelationship
    impact_review_required: bool
    activation_performed: bool
    assessed_by: str
    assessed_on: str
    notes: tuple[str, ...]
    result_sha256: str

    def __post_init__(self) -> None:
        if self.schema_version != 1 or self.assessment_version != IMPACT_VERSION:
            raise CodeProfileError("Unsupported edition-impact assessment version.")
        for value, name in (
            (self.assessment_id, "edition assessment ID"),
            (self.profile_id, "edition assessment profile ID"),
            (self.authority_id, "edition assessment authority ID"),
            (self.active_source_id, "edition assessment active source ID"),
        ):
            _identifier(value, name)
        _sha256(self.profile_sha256, "edition assessment profile hash")
        if not isinstance(self.discovered_source, CodeSourceIdentity):
            raise CodeProfileError("Edition assessment discovered source is invalid.")
        if not isinstance(self.relationship, EditionRelationship):
            raise CodeProfileError("Edition relationship is invalid.")
        required = self.relationship in {EditionRelationship.NEWER, EditionRelationship.UNKNOWN}
        if self.impact_review_required is not required:
            raise CodeProfileError("Edition impact-review state conflicts with relationship.")
        if self.activation_performed:
            raise CodeProfileError("Edition discovery cannot activate a code edition.")
        _text(self.assessed_by, "edition assessment reviewer", 128)
        _date(self.assessed_on, "edition assessment date")
        _texts(self.notes, "edition assessment notes", 1, 16, 512)
        _sha256(self.result_sha256, "edition assessment result hash")
        payload = self.to_dict()
        del payload["result_sha256"]
        if self.result_sha256 != _canonical_sha256(payload):
            raise CodeProfileError("Edition assessment SHA-256 does not match its content.")


def create_project_code_profile(
    *,
    profile_id: str,
    project_id: str,
    jurisdiction: str,
    project_type: str,
    sources: tuple[CodeSourceIdentity, ...],
    authorities: tuple[CodeAuthority, ...],
    requirements: tuple[ProjectCodeRequirement, ...],
    reviewed_by: str,
    reviewed_on: str,
    assumptions: tuple[str, ...],
) -> ProjectCodeProfile:
    """Create one reviewed profile without retrieval, compliance, or edition activation."""
    payload: dict[str, Any] = {
        "schema_version": 1,
        "profile_version": PROFILE_VERSION,
        "profile_id": profile_id,
        "project_id": project_id,
        "jurisdiction": jurisdiction,
        "project_type": project_type,
        "sources": sources,
        "authorities": authorities,
        "requirements": requirements,
        "reviewed_by": reviewed_by,
        "reviewed_on": reviewed_on,
        "assumptions": assumptions,
        "provider_retrieval_performed": False,
        "compliance_evaluation_performed": False,
    }
    return ProjectCodeProfile(**payload, profile_sha256=_canonical_sha256(payload))


def assess_discovered_edition(
    profile: ProjectCodeProfile,
    *,
    assessment_id: str,
    authority_id: str,
    discovered_source: CodeSourceIdentity,
    relationship: EditionRelationship,
    assessed_by: str,
    assessed_on: str,
    notes: tuple[str, ...],
) -> EditionImpactAssessment:
    """Record edition discovery while leaving the active profile byte-for-byte unchanged."""
    if not isinstance(profile, ProjectCodeProfile):
        raise CodeProfileError("Edition assessment requires a typed project code profile.")
    if not isinstance(discovered_source, CodeSourceIdentity):
        raise CodeProfileError("Edition assessment requires a typed discovered source.")
    authority = next((item for item in profile.authorities if item.authority_id == authority_id), None)
    if authority is None:
        raise CodeProfileError("Edition assessment authority is not pinned in the profile.")
    active = next(item for item in profile.sources if item.source_id == authority.source_id)
    if (
        discovered_source.authority_name != active.authority_name
        or discovered_source.standard_name != active.standard_name
    ):
        raise CodeProfileError("Discovered edition does not match the pinned authority and standard.")
    if relationship is EditionRelationship.SAME:
        if (
            discovered_source.edition != active.edition
            or discovered_source.content_sha256 != active.content_sha256
        ):
            raise CodeProfileError("A same-edition discovery must match edition and content hash.")
    elif relationship in {EditionRelationship.NEWER, EditionRelationship.OLDER}:
        if discovered_source.edition == active.edition:
            raise CodeProfileError("Newer/older discovery must identify a different edition.")
    payload: dict[str, Any] = {
        "schema_version": 1,
        "assessment_version": IMPACT_VERSION,
        "assessment_id": assessment_id,
        "profile_id": profile.profile_id,
        "profile_sha256": profile.profile_sha256,
        "authority_id": authority.authority_id,
        "active_source_id": active.source_id,
        "discovered_source": discovered_source,
        "relationship": relationship,
        "impact_review_required": relationship in {
            EditionRelationship.NEWER, EditionRelationship.UNKNOWN
        },
        "activation_performed": False,
        "assessed_by": assessed_by,
        "assessed_on": assessed_on,
        "notes": notes,
    }
    return EditionImpactAssessment(**payload, result_sha256=_canonical_sha256(payload))


def parse_project_code_profile(value: object) -> ProjectCodeProfile:
    """Parse an exact JSON-shaped profile without coercion or ignored fields."""
    item = _mapping(value, "project code profile")
    _exact_keys(item, ProjectCodeProfile, "project code profile")
    sources_value = item["sources"]
    authorities_value = item["authorities"]
    requirements_value = item["requirements"]
    if not isinstance(sources_value, list):
        raise CodeProfileError("Project code-profile sources must be a JSON array.")
    if not isinstance(authorities_value, list):
        raise CodeProfileError("Project code-profile authorities must be a JSON array.")
    if not isinstance(requirements_value, list):
        raise CodeProfileError("Project code-profile requirements must be a JSON array.")

    sources: list[CodeSourceIdentity] = []
    for raw in sources_value:
        source = _mapping(raw, "code source")
        _exact_keys(source, CodeSourceIdentity, "code source")
        sources.append(
            CodeSourceIdentity(
                **{
                    **source,
                    "access_basis": _enum(
                        EvidenceAccessBasis, source["access_basis"], "source access basis"
                    ),
                }
            )
        )
    authorities: list[CodeAuthority] = []
    for raw in authorities_value:
        authority = _mapping(raw, "code authority")
        _exact_keys(authority, CodeAuthority, "code authority")
        topics = authority["topics"]
        if not isinstance(topics, list):
            raise CodeProfileError("Code authority topics must be a JSON array.")
        authorities.append(CodeAuthority(**{**authority, "topics": tuple(topics)}))
    requirements: list[ProjectCodeRequirement] = []
    for raw in requirements_value:
        requirement = _mapping(raw, "project requirement")
        _exact_keys(requirement, ProjectCodeRequirement, "project requirement")
        requirements.append(ProjectCodeRequirement(**requirement))
    assumptions = item["assumptions"]
    if not isinstance(assumptions, list):
        raise CodeProfileError("Project code-profile assumptions must be a JSON array.")
    return ProjectCodeProfile(
        **{
            **item,
            "sources": tuple(sources),
            "authorities": tuple(authorities),
            "requirements": tuple(requirements),
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
        raise CodeProfileError(f"{name} is invalid.")


def _text(value: object, name: str, maximum: int) -> None:
    if (
        not isinstance(value, str)
        or not value.strip()
        or value != value.strip()
        or len(value.encode("utf-8")) > maximum
        or any(ord(character) < 32 for character in value)
    ):
        raise CodeProfileError(f"{name} is invalid or exceeds {maximum} UTF-8 bytes.")


def _date(value: object, name: str) -> None:
    if not isinstance(value, str) or _DATE.fullmatch(value) is None:
        raise CodeProfileError(f"{name} must use YYYY-MM-DD.")
    try:
        date.fromisoformat(value)
    except ValueError as exc:
        raise CodeProfileError(f"{name} is not a valid calendar date.") from exc


def _sha256(value: object, name: str) -> None:
    if not isinstance(value, str) or _SHA256.fullmatch(value) is None:
        raise CodeProfileError(f"{name} must be lowercase SHA-256.")


def _texts(
    values: object, name: str, minimum: int, maximum: int, maximum_bytes: int
) -> None:
    if not isinstance(values, tuple) or not minimum <= len(values) <= maximum:
        raise CodeProfileError(f"{name} must contain {minimum} through {maximum} entries.")
    for value in values:
        _text(value, name, maximum_bytes)
    if len(values) != len(set(values)):
        raise CodeProfileError(f"{name} must not contain duplicates.")


def _records(
    values: object, record_type: type, name: str, minimum: int, maximum: int
) -> None:
    if (
        not isinstance(values, tuple)
        or not minimum <= len(values) <= maximum
        or any(not isinstance(item, record_type) for item in values)
    ):
        raise CodeProfileError(f"{name} is invalid or outside its count limit.")


def _mapping(value: object, name: str) -> dict[str, Any]:
    if not isinstance(value, dict) or any(not isinstance(key, str) for key in value):
        raise CodeProfileError(f"{name} must be a JSON object with string keys.")
    return value


def _exact_keys(value: Mapping[str, object], record_type: type, name: str) -> None:
    expected = set(record_type.__dataclass_fields__)
    if set(value) != expected:
        raise CodeProfileError(f"{name} fields do not match the supported schema.")


def _enum(enum_type: type[Enum], value: object, name: str) -> Enum:
    if not isinstance(value, str):
        raise CodeProfileError(f"{name} is invalid.")
    try:
        return enum_type(value)
    except ValueError as exc:
        raise CodeProfileError(f"{name} is unsupported.") from exc
