"""Exact-context-bound reviewed AutoCAD project conventions."""

from __future__ import annotations

import hashlib
import json
import re
from dataclasses import asdict, dataclass
from datetime import date
from pathlib import Path
from typing import Any

from agent.exceptions import AutoCADContextError


CONVENTION_VERSION = "autocad.project_conventions.v1"
_CONTEXT_ID = re.compile(r"[0-9a-f]{32}")
_DOCUMENT_ID = re.compile(
    r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}"
)
_IDENTIFIER = re.compile(r"[a-z][a-z0-9_.:-]{0,127}")
_SYMBOL_ID = re.compile(r"[a-z][a-z0-9_]{0,47}")
_SHA256 = re.compile(r"[0-9a-f]{64}")
_MAX_PROFILE_BYTES = 65_536


@dataclass(frozen=True, slots=True)
class BlockConvention:
    block_name: str
    layer_name: str
    symbol_id: str
    description: str

    def __post_init__(self) -> None:
        _text(self.block_name, "block name", 255)
        _text(self.layer_name, "block layer", 255)
        if _SYMBOL_ID.fullmatch(self.symbol_id) is None:
            raise AutoCADContextError("Project convention symbol ID is invalid.")
        _text(self.description, "block description", 512)


@dataclass(frozen=True, slots=True)
class RouteConvention:
    layer_name: str
    route_kind: str
    connectivity_policy: str

    def __post_init__(self) -> None:
        _text(self.layer_name, "route layer", 255)
        if self.route_kind != "lighting_circuit_route":
            raise AutoCADContextError("Project convention route kind is unsupported.")
        if self.connectivity_policy != "explicit_endpoints_only":
            raise AutoCADContextError(
                "Project convention routes must use explicit endpoint connectivity."
            )


@dataclass(frozen=True, slots=True)
class TagConvention:
    layer_name: str
    tag_kind: str
    grammar: str

    def __post_init__(self) -> None:
        _text(self.layer_name, "tag layer", 255)
        allowed = {
            ("circuit_reference", "circuit_number_slash_panel_id"),
            ("control_group", "comma_separated_control_ids"),
            ("environmental_qualifier", "weatherproof_literal"),
        }
        if (self.tag_kind, self.grammar) not in allowed:
            raise AutoCADContextError("Project convention tag grammar is unsupported.")


@dataclass(frozen=True, slots=True)
class ProjectConventionProfile:
    schema_version: int
    convention_version: str
    profile_id: str
    document_id: str
    context_id: str
    context_evidence_sha256: str
    source_drawing_sha256: str
    reviewed_by: str
    reviewed_on: str
    block_mappings: tuple[BlockConvention, ...]
    route_mappings: tuple[RouteConvention, ...]
    tag_mappings: tuple[TagConvention, ...]
    assumptions: tuple[str, ...]
    profile_sha256: str

    def __post_init__(self) -> None:
        if self.schema_version != 1 or self.convention_version != CONVENTION_VERSION:
            raise AutoCADContextError("Project convention profile version is unsupported.")
        if _IDENTIFIER.fullmatch(self.profile_id) is None:
            raise AutoCADContextError("Project convention profile ID is invalid.")
        if _DOCUMENT_ID.fullmatch(self.document_id) is None:
            raise AutoCADContextError("Project convention document ID is invalid.")
        if _CONTEXT_ID.fullmatch(self.context_id) is None:
            raise AutoCADContextError("Project convention context ID is invalid.")
        _hash(self.context_evidence_sha256, "context evidence")
        _hash(self.source_drawing_sha256, "source drawing")
        _text(self.reviewed_by, "reviewer", 128)
        try:
            if date.fromisoformat(self.reviewed_on).isoformat() != self.reviewed_on:
                raise ValueError
        except ValueError as exc:
            raise AutoCADContextError("Project convention review date is invalid.") from exc
        _typed_tuple(self.block_mappings, BlockConvention, "block mappings", 1, 128)
        _typed_tuple(self.route_mappings, RouteConvention, "route mappings", 0, 16)
        _typed_tuple(self.tag_mappings, TagConvention, "tag mappings", 0, 16)
        _texts(self.assumptions, "assumptions", 1, 16, 512)
        _unique(
            ((item.layer_name.casefold(), item.block_name.casefold()) for item in self.block_mappings),
            "block mapping identities",
        )
        _unique((item.symbol_id for item in self.block_mappings), "symbol IDs")
        _unique((item.layer_name.casefold() for item in self.route_mappings), "route layers")
        _unique(
            ((item.layer_name.casefold(), item.tag_kind) for item in self.tag_mappings),
            "tag mappings",
        )
        _hash(self.profile_sha256, "profile")
        if self.profile_sha256 != _profile_hash(self):
            raise AutoCADContextError("Project convention profile SHA-256 does not match content.")

    def to_dict(self) -> dict[str, object]:
        return asdict(self)


def create_project_convention_profile(
    *, profile_id: str, document_id: str, context_id: str,
    context_evidence_sha256: str, source_drawing_sha256: str,
    reviewed_by: str, reviewed_on: str,
    block_mappings: tuple[BlockConvention, ...],
    route_mappings: tuple[RouteConvention, ...],
    tag_mappings: tuple[TagConvention, ...], assumptions: tuple[str, ...],
) -> ProjectConventionProfile:
    """Create one immutable, self-hashed reviewed convention profile."""
    values: dict[str, Any] = {
        "schema_version": 1,
        "convention_version": CONVENTION_VERSION,
        "profile_id": profile_id,
        "document_id": document_id,
        "context_id": context_id,
        "context_evidence_sha256": context_evidence_sha256,
        "source_drawing_sha256": source_drawing_sha256,
        "reviewed_by": reviewed_by,
        "reviewed_on": reviewed_on,
        "block_mappings": block_mappings,
        "route_mappings": route_mappings,
        "tag_mappings": tag_mappings,
        "assumptions": assumptions,
    }
    digest = hashlib.sha256(_canonical(values)).hexdigest()
    return ProjectConventionProfile(**values, profile_sha256=digest)


def parse_project_convention_profile(raw: object) -> ProjectConventionProfile:
    """Parse and revalidate an exact schema-v1 convention profile."""
    if not isinstance(raw, dict):
        raise AutoCADContextError("Project convention profile must be an object.")
    expected = {
        "schema_version", "convention_version", "profile_id", "document_id",
        "context_id", "context_evidence_sha256", "source_drawing_sha256",
        "reviewed_by", "reviewed_on", "block_mappings", "route_mappings",
        "tag_mappings", "assumptions", "profile_sha256",
    }
    if set(raw) != expected:
        raise AutoCADContextError("Project convention profile must use the exact schema.")
    blocks = _records(raw["block_mappings"], BlockConvention, "block mappings")
    routes = _records(raw["route_mappings"], RouteConvention, "route mappings")
    tags = _records(raw["tag_mappings"], TagConvention, "tag mappings")
    assumptions = raw["assumptions"]
    if not isinstance(assumptions, list):
        raise AutoCADContextError("Project convention assumptions must be an array.")
    values = dict(raw)
    values["block_mappings"] = blocks
    values["route_mappings"] = routes
    values["tag_mappings"] = tags
    values["assumptions"] = tuple(assumptions)
    return ProjectConventionProfile(**values)


def load_project_convention_profile(path: Path) -> ProjectConventionProfile:
    """Load one bounded regular UTF-8 JSON profile."""
    resolved = path.resolve(strict=True)
    if path.is_symlink() or not resolved.is_file():
        raise AutoCADContextError("Project convention profile must be a regular non-link file.")
    raw = resolved.read_bytes()
    if not raw or len(raw) > _MAX_PROFILE_BYTES:
        raise AutoCADContextError("Project convention profile is empty or too large.")
    try:
        return parse_project_convention_profile(json.loads(raw.decode("utf-8")))
    except (UnicodeError, json.JSONDecodeError) as exc:
        raise AutoCADContextError("Project convention profile must be valid UTF-8 JSON.") from exc


def _profile_hash(profile: ProjectConventionProfile) -> str:
    values = asdict(profile)
    del values["profile_sha256"]
    return hashlib.sha256(_canonical(values)).hexdigest()


def _canonical(value: object) -> bytes:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"), default=asdict).encode("utf-8")


def _records(raw: object, kind: type, name: str) -> tuple:
    if not isinstance(raw, list):
        raise AutoCADContextError(f"Project convention {name} must be an array.")
    result = []
    expected = set(kind.__dataclass_fields__)
    for item in raw:
        if not isinstance(item, dict) or set(item) != expected:
            raise AutoCADContextError(f"Project convention {name} must use exact records.")
        result.append(kind(**item))
    return tuple(result)


def _text(value: object, name: str, maximum: int) -> None:
    if not isinstance(value, str) or not value.strip() or len(value.encode("utf-8")) > maximum:
        raise AutoCADContextError(f"Project convention {name} is invalid.")


def _hash(value: object, name: str) -> None:
    if not isinstance(value, str) or _SHA256.fullmatch(value) is None:
        raise AutoCADContextError(f"Project convention {name} SHA-256 is invalid.")


def _typed_tuple(value: object, kind: type, name: str, minimum: int, maximum: int) -> None:
    if not isinstance(value, tuple) or not minimum <= len(value) <= maximum or any(not isinstance(item, kind) for item in value):
        raise AutoCADContextError(f"Project convention {name} are invalid.")


def _texts(value: object, name: str, minimum: int, maximum: int, byte_limit: int) -> None:
    if not isinstance(value, tuple) or not minimum <= len(value) <= maximum:
        raise AutoCADContextError(f"Project convention {name} are invalid.")
    for item in value:
        _text(item, name, byte_limit)


def _unique(values: object, name: str) -> None:
    items = list(values)  # type: ignore[arg-type]
    if len(items) != len(set(items)):
        raise AutoCADContextError(f"Project convention {name} must be unique.")
