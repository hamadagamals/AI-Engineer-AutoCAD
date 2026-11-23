"""Strict read-only AutoCAD context ingestion and electrical-model mapping."""

from __future__ import annotations

import hashlib
import json
import math
import re
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Mapping

from agent.exceptions import AutoCADContextError, ConfirmationRequiredError
from core.electrical_model import (
    Confidence,
    Circuit,
    Device,
    Drawing,
    DrawingLayer,
    ElectricalProjectModel,
    PrivacyClassification,
    Panel,
    Provenance,
    Route,
    SourceKind,
    SourceReference,
    UnknownObject,
    unit_system_from_insunits,
)
from core.file_manager import FileManager
from integrations.electrical_symbols import ElectricalSymbolCatalog
from integrations.autocad_project_conventions import (
    BlockConvention,
    ProjectConventionProfile,
    create_project_convention_profile,
    load_project_convention_profile,
)


_CONTEXT_ID = re.compile(r"[0-9a-f]{32}")
_SHA256 = re.compile(r"[0-9a-f]{64}")
_CIRCUIT_REFERENCE = re.compile(
    r"\s*(?P<circuit>[0-9]{1,3})\s*/\s*(?P<panel>[A-Za-z0-9][A-Za-z0-9._-]{0,63})\s*"
)
_CONTROL_GROUP = re.compile(r"\s*[a-z](?:\s*,\s*[a-z]){0,15}\s*", re.IGNORECASE)
_WEATHERPROOF = re.compile(r"\s*WP\s*", re.IGNORECASE)
_HANDLE = re.compile(r"[0-9A-F]{1,32}")
_GUID = re.compile(
    r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}"
)
_ENTITY_TYPES = frozenset(
    {
        "LINE",
        "LWPOLYLINE",
        "ARC",
        "CIRCLE",
        "INSERT",
        "TEXT",
        "MTEXT",
        "DIMENSION",
        "UNSUPPORTED",
    }
)
_TOP_KEYS = frozenset(
    {
        "schema_version",
        "context_id",
        "captured_at",
        "source_mode",
        "privacy",
        "document",
        "limits",
        "truncated",
        "truncation_reasons",
        "layers",
        "entities",
    }
)
_PRIVACY_KEYS = frozenset({"classification", "provider_egress_allowed"})
_DOCUMENT_KEYS = frozenset(
    {
        "document_id",
        "identity_source",
        "name",
        "drawing_units",
        "layout",
        "database_modified",
    }
)
_LIMIT_KEYS = frozenset(
    {
        "maximum_entities",
        "maximum_layers",
        "maximum_attributes_per_entity",
        "maximum_points_per_entity",
        "maximum_text_bytes",
        "maximum_payload_bytes",
        "maximum_nesting",
        "maximum_duration_seconds",
    }
)
_LAYER_KEYS = frozenset(
    {"name", "color_index", "linetype", "is_off", "is_frozen", "is_locked"}
)
_ATTRIBUTE_KEYS = frozenset({"tag", "value"})
_ENTITY_KEYS = frozenset(
    {
        "object_id",
        "entity_type",
        "original_type",
        "layer",
        "layout",
        "visible",
        "points",
        "radius",
        "rotation_degrees",
        "scale",
        "block_name",
        "text",
        "attributes",
    }
)


@dataclass(frozen=True, slots=True)
class ContextLimits:
    maximum_entities: int = 512
    maximum_layers: int = 128
    maximum_attributes_per_entity: int = 32
    maximum_points_per_entity: int = 4_096
    maximum_text_bytes: int = 4_096
    maximum_payload_bytes: int = 1_048_576
    maximum_nesting: int = 8
    maximum_duration_seconds: int = 30

    def __post_init__(self) -> None:
        bounds = {
            "maximum_entities": (1, 2_000),
            "maximum_layers": (1, 512),
            "maximum_attributes_per_entity": (0, 128),
            "maximum_points_per_entity": (2, 4_096),
            "maximum_text_bytes": (1, 16_384),
            "maximum_payload_bytes": (1_024, 4_194_304),
            "maximum_nesting": (4, 16),
            "maximum_duration_seconds": (1, 120),
        }
        for name, (minimum, maximum) in bounds.items():
            value = getattr(self, name)
            if (
                not isinstance(value, int)
                or isinstance(value, bool)
                or not minimum <= value <= maximum
            ):
                raise AutoCADContextError(
                    f"AutoCAD context {name} must be from {minimum} through {maximum}."
                )

    def to_dict(self) -> dict[str, int]:
        return {name: getattr(self, name) for name in sorted(_LIMIT_KEYS)}


@dataclass(frozen=True, slots=True)
class ContextLayer:
    name: str
    color_index: int
    linetype: str
    is_off: bool
    is_frozen: bool
    is_locked: bool

    def to_dict(self) -> dict[str, object]:
        return {
            "name": self.name,
            "color_index": self.color_index,
            "linetype": self.linetype,
            "is_off": self.is_off,
            "is_frozen": self.is_frozen,
            "is_locked": self.is_locked,
        }


def _compatible_limits(supplied: ContextLimits, policy: ContextLimits) -> bool:
    """Accept exact policy or a legacy context whose only stricter bound is points."""
    supplied_values = supplied.to_dict()
    policy_values = policy.to_dict()
    for name, value in supplied_values.items():
        if name == "maximum_points_per_entity":
            if value > policy_values[name]:
                return False
        elif value != policy_values[name]:
            return False
    return True


@dataclass(frozen=True, slots=True)
class ContextAttribute:
    tag: str
    value: str

    def to_dict(self) -> dict[str, str]:
        return {"tag": self.tag, "value": self.value}


@dataclass(frozen=True, slots=True)
class ContextEntity:
    object_id: str
    entity_type: str
    original_type: str
    layer: str
    layout: str
    visible: bool
    points: tuple[tuple[float, float, float], ...]
    radius: float | None
    rotation_degrees: float | None
    scale: tuple[float, float, float] | None
    block_name: str | None
    text: str | None
    attributes: tuple[ContextAttribute, ...]

    def to_dict(self) -> dict[str, object]:
        return {
            "object_id": self.object_id,
            "entity_type": self.entity_type,
            "original_type": self.original_type,
            "layer": self.layer,
            "layout": self.layout,
            "visible": self.visible,
            "points": [list(point) for point in self.points],
            "radius": self.radius,
            "rotation_degrees": self.rotation_degrees,
            "scale": list(self.scale) if self.scale is not None else None,
            "block_name": self.block_name,
            "text": self.text,
            "attributes": [item.to_dict() for item in self.attributes],
        }


@dataclass(frozen=True, slots=True)
class AutoCADContext:
    schema_version: int
    context_id: str
    captured_at: str
    source_mode: str
    privacy: PrivacyClassification
    provider_egress_allowed: bool
    document_id: str
    identity_source: str
    document_name: str
    drawing_units: int
    layout: str
    database_modified: int
    limits: ContextLimits
    truncated: bool
    truncation_reasons: tuple[str, ...]
    layers: tuple[ContextLayer, ...]
    entities: tuple[ContextEntity, ...]

    def to_dict(self) -> dict[str, object]:
        return {
            "schema_version": self.schema_version,
            "context_id": self.context_id,
            "captured_at": self.captured_at,
            "source_mode": self.source_mode,
            "privacy": {
                "classification": self.privacy.value,
                "provider_egress_allowed": self.provider_egress_allowed,
            },
            "document": {
                "document_id": self.document_id,
                "identity_source": self.identity_source,
                "name": self.document_name,
                "drawing_units": self.drawing_units,
                "layout": self.layout,
                "database_modified": self.database_modified,
            },
            "limits": self.limits.to_dict(),
            "truncated": self.truncated,
            "truncation_reasons": list(self.truncation_reasons),
            "layers": [item.to_dict() for item in self.layers],
            "entities": [item.to_dict() for item in self.entities],
        }


@dataclass(frozen=True, slots=True)
class ContextImportResult:
    context: AutoCADContext
    model: ElectricalProjectModel
    evidence_sha256: str
    evidence_bytes: int
    read_only: bool
    provider_egress_performed: bool

    def to_dict(self) -> dict[str, object]:
        return {
            "context": self.context.to_dict(),
            "model": self.model.to_dict(),
            "evidence_sha256": self.evidence_sha256,
            "evidence_bytes": self.evidence_bytes,
            "read_only": self.read_only,
            "provider_egress_performed": self.provider_egress_performed,
        }


class AutoCADContextService:
    """Load exact local bridge evidence and map it without provider data egress."""

    def __init__(
        self,
        files: FileManager,
        symbol_catalog_path: Path,
        *,
        limits: ContextLimits | None = None,
    ) -> None:
        self._files = files
        self.symbol_catalog_path = symbol_catalog_path
        self.limits = limits or ContextLimits()

    def import_context(
        self,
        *,
        context_id: str,
        evidence_sha256: str,
        confirmed: bool = False,
    ) -> ContextImportResult:
        """Import one exact context ID after durable approval and local hash binding."""
        if not confirmed:
            raise ConfirmationRequiredError(
                "AutoCAD drawing-context import requires explicit durable approval."
            )
        _require_pattern(context_id, _CONTEXT_ID, "context_id")
        _require_pattern(evidence_sha256, _SHA256, "evidence_sha256")
        evidence_path = self._find_evidence(context_id)
        raw_bytes = _read_bounded(evidence_path, self.limits.maximum_payload_bytes)
        actual_sha256 = hashlib.sha256(raw_bytes).hexdigest()
        if actual_sha256 != evidence_sha256:
            raise AutoCADContextError(
                "AutoCAD context digest does not match the approved evidence."
            )
        try:
            raw = json.loads(raw_bytes.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise AutoCADContextError("AutoCAD context is not valid UTF-8 JSON.") from exc
        context = parse_autocad_context(raw, expected_limits=self.limits)
        if context.context_id != context_id:
            raise AutoCADContextError("AutoCAD context ID does not match its filename.")
        conventions = self._find_conventions(
            context=context, evidence_sha256=actual_sha256
        )
        model = map_context_to_electrical_model(
            context,
            evidence_sha256=actual_sha256,
            symbol_catalog_path=self.symbol_catalog_path,
            conventions=conventions,
        )
        return ContextImportResult(
            context,
            model,
            actual_sha256,
            len(raw_bytes),
            True,
            False,
        )

    def _find_evidence(self, context_id: str) -> Path:
        matches: list[Path] = []
        for root in self._files.workspace_roots:
            candidate = self._files.resolve_path(
                root
                / ".ai-engineer"
                / "autocad"
                / "context"
                / "inbox"
                / f"{context_id}.json"
            )
            if candidate.is_file():
                matches.append(candidate)
        if len(matches) != 1:
            raise AutoCADContextError(
                "Exactly one workspace AutoCAD context record is required."
            )
        return matches[0]

    def _find_conventions(
        self, *, context: AutoCADContext, evidence_sha256: str
    ) -> ProjectConventionProfile | None:
        matches: list[ProjectConventionProfile] = []
        for root in self._files.workspace_roots:
            directory = root / ".ai-engineer" / "autocad" / "project-conventions"
            if not directory.is_dir():
                continue
            paths = sorted(directory.glob("*.json"))
            if len(paths) > 128:
                raise AutoCADContextError(
                    "AutoCAD project-convention profile count exceeds 128."
                )
            for path in paths:
                resolved = self._files.resolve_path(path)
                profile = load_project_convention_profile(resolved)
                if (
                    profile.context_id == context.context_id
                    and profile.context_evidence_sha256 == evidence_sha256
                    and profile.document_id == context.document_id
                ):
                    matches.append(profile)
        if len(matches) > 1:
            raise AutoCADContextError(
                "Multiple project-convention profiles match the exact context evidence."
            )
        return matches[0] if matches else None


def parse_autocad_context(
    raw: object, *, expected_limits: ContextLimits | None = None
) -> AutoCADContext:
    """Validate a complete schema-v1 bridge document with exact fields and limits."""
    configured_limits = expected_limits or ContextLimits()
    _validate_structure(raw, configured_limits.maximum_nesting)
    document = _mapping(raw, _TOP_KEYS, "context")
    if document["schema_version"] != 1:
        raise AutoCADContextError("AutoCAD context schema_version must be 1.")
    context_id = _string(document["context_id"], "context_id", 32)
    _require_pattern(context_id, _CONTEXT_ID, "context_id")
    captured_at = _utc_timestamp(document["captured_at"])
    source_mode = _string(document["source_mode"], "source_mode", 16)
    if source_mode not in {"selection", "drawing"}:
        raise AutoCADContextError("AutoCAD context source_mode is unsupported.")

    privacy_raw = _mapping(document["privacy"], _PRIVACY_KEYS, "privacy")
    try:
        privacy = PrivacyClassification(
            _string(privacy_raw["classification"], "privacy classification", 64)
        )
    except ValueError as exc:
        raise AutoCADContextError("AutoCAD context privacy classification is invalid.") from exc
    provider_egress_allowed = _boolean(
        privacy_raw["provider_egress_allowed"], "provider_egress_allowed"
    )
    if provider_egress_allowed:
        raise AutoCADContextError(
            "Milestone 16 context must prohibit provider data egress."
        )

    document_raw = _mapping(document["document"], _DOCUMENT_KEYS, "document")
    document_id = _string(document_raw["document_id"], "document_id", 36)
    _require_pattern(document_id, _GUID, "document_id")
    identity_source = _string(
        document_raw["identity_source"], "identity_source", 32
    )
    if identity_source not in {"fingerprint_guid", "path_sha256"}:
        raise AutoCADContextError("AutoCAD document identity source is unsupported.")
    document_name = _string(document_raw["name"], "document name", 255)
    drawing_units = _integer(document_raw["drawing_units"], "drawing_units", 0, 24)
    layout = _string(document_raw["layout"], "layout", 255)
    database_modified = _integer(
        document_raw["database_modified"], "database_modified", 0, 63
    )

    limits_raw = _mapping(document["limits"], _LIMIT_KEYS, "limits")
    supplied_limits = ContextLimits(
        **{
            name: _integer(limits_raw[name], name, 0, 4_194_304)
            for name in _LIMIT_KEYS
        }
    )
    limits = supplied_limits
    if expected_limits is not None and not _compatible_limits(
        supplied_limits, expected_limits
    ):
        raise AutoCADContextError(
            "AutoCAD context limits do not match the configured local policy."
        )
    truncated = _boolean(document["truncated"], "truncated")
    reasons_raw = _list(document["truncation_reasons"], "truncation_reasons")
    if len(reasons_raw) > 16:
        raise AutoCADContextError("AutoCAD context has too many truncation reasons.")
    reasons = tuple(_string(item, "truncation reason", 256) for item in reasons_raw)
    if truncated != bool(reasons):
        raise AutoCADContextError(
            "AutoCAD context truncation state and reasons are inconsistent."
        )

    layers_raw = _list(document["layers"], "layers")
    entities_raw = _list(document["entities"], "entities")
    if len(layers_raw) > limits.maximum_layers:
        raise AutoCADContextError("AutoCAD context exceeds its layer limit.")
    if len(entities_raw) > limits.maximum_entities:
        raise AutoCADContextError("AutoCAD context exceeds its entity limit.")
    layers = tuple(_parse_layer(item) for item in layers_raw)
    layer_names = [item.name.casefold() for item in layers]
    if len(layer_names) != len(set(layer_names)):
        raise AutoCADContextError("AutoCAD context layer names must be unique.")
    entities = tuple(_parse_entity(item, limits) for item in entities_raw)
    handles = [item.object_id for item in entities]
    if len(handles) != len(set(handles)):
        raise AutoCADContextError("AutoCAD context object IDs must be unique.")
    available_layers = set(layer_names)
    if any(item.layer.casefold() not in available_layers for item in entities):
        raise AutoCADContextError("AutoCAD context entity references an unknown layer.")
    return AutoCADContext(
        1,
        context_id,
        captured_at,
        source_mode,
        privacy,
        provider_egress_allowed,
        document_id,
        identity_source,
        document_name,
        drawing_units,
        layout,
        database_modified,
        limits,
        truncated,
        reasons,
        layers,
        entities,
    )


def map_context_to_electrical_model(
    context: AutoCADContext,
    *,
    evidence_sha256: str,
    symbol_catalog_path: Path,
    conventions: ProjectConventionProfile | None = None,
) -> ElectricalProjectModel:
    """Map observed objects conservatively; unsupported meaning stays explicit."""
    _require_pattern(evidence_sha256, _SHA256, "evidence_sha256")
    catalog = ElectricalSymbolCatalog.load(symbol_catalog_path)
    symbols_by_block = {item.block_name.casefold(): item for item in catalog.symbols}
    convention_blocks: dict[tuple[str, str], BlockConvention] = {}
    convention_route_layers: set[str] = set()
    if conventions is not None:
        if (
            conventions.context_id != context.context_id
            or conventions.context_evidence_sha256 != evidence_sha256
            or conventions.document_id != context.document_id
        ):
            raise AutoCADContextError(
                "Project conventions do not match the exact context evidence and document."
            )
        convention_blocks = {
            (item.layer_name.casefold(), item.block_name.casefold()): item
            for item in conventions.block_mappings
        }
        convention_route_layers = {
            item.layer_name.casefold() for item in conventions.route_mappings
        }
    layer_ids = {
        layer.name.casefold(): f"layer:{index + 1}"
        for index, layer in enumerate(context.layers)
    }
    layers = tuple(
        DrawingLayer(
            layer_ids[item.name.casefold()],
            item.name,
            item.color_index,
            item.linetype,
            item.is_off,
            item.is_frozen,
            item.is_locked,
        )
        for item in context.layers
    )
    normalized_document_id = "autocad:" + context.document_id.replace("-", "")
    source_kind = (
        SourceKind.AUTOCAD_SELECTION
        if context.source_mode == "selection"
        else SourceKind.AUTOCAD_DRAWING
    )
    provenance = Provenance(
        source_kind,
        context.captured_at,
        Confidence.OBSERVED,
        context.privacy,
        False,
        evidence_sha256,
    )
    drawings = (
        Drawing(
            normalized_document_id,
            context.document_name,
            unit_system_from_insunits(context.drawing_units),
            context.layout,
            tuple(item.layer_id for item in layers),
            context.database_modified,
        ),
    )
    devices: list[Device] = []
    routes: list[Route] = []
    unknown: list[UnknownObject] = []
    panels: dict[str, Panel] = {}
    circuits: dict[tuple[str, str], Circuit] = {}
    tag_conventions = (
        {
            (item.layer_name.casefold(), item.tag_kind): item
            for item in conventions.tag_mappings
        }
        if conventions is not None
        else {}
    )
    observed_control_tags = 0
    observed_environmental_qualifiers = 0
    for index, entity in enumerate(context.entities):
        layer_id = layer_ids[entity.layer.casefold()]
        source = SourceReference(
            "autocad",
            normalized_document_id,
            entity.object_id,
            context.context_id,
        )
        if entity.entity_type == "INSERT" and entity.block_name is not None:
            symbol = symbols_by_block.get(entity.block_name.casefold())
            convention = convention_blocks.get(
                (entity.layer.casefold(), entity.block_name.casefold())
            )
            symbol_id = (
                symbol.symbol_id if symbol is not None
                else convention.symbol_id if convention is not None else None
            )
            if symbol_id is not None and entity.points and entity.scale is not None:
                devices.append(
                    Device(
                        f"device:{index + 1}",
                        symbol_id,
                        entity.block_name,
                        layer_id,
                        entity.points[0],
                        entity.rotation_degrees or 0.0,
                        entity.scale,
                        tuple((item.tag, item.value) for item in entity.attributes),
                        (),
                        source,
                    )
                )
                continue
        if (
            entity.entity_type in {"LINE", "LWPOLYLINE"}
            and (
                entity.layer.casefold() == "e-wire"
                or entity.layer.casefold() in convention_route_layers
            )
            and len(entity.points) >= 2
        ):
            routes.append(Route(f"route:{index + 1}", layer_id, entity.points, source))
            continue
        if entity.entity_type in {"TEXT", "MTEXT"} and entity.text is not None:
            circuit_tag = tag_conventions.get(
                (entity.layer.casefold(), "circuit_reference")
            )
            if circuit_tag is not None:
                match = _CIRCUIT_REFERENCE.fullmatch(entity.text)
                if match is not None:
                    panel_name = match.group("panel")
                    circuit_name = match.group("circuit")
                    panel_key = panel_name.casefold()
                    panel_id = "panel:tag:" + hashlib.sha256(
                        panel_key.encode("utf-8")
                    ).hexdigest()[:16]
                    if panel_key not in panels:
                        panels[panel_key] = Panel(panel_id, panel_name, source)
                    circuit_key = (panel_key, circuit_name)
                    if circuit_key not in circuits:
                        identity = f"{panel_key}/{circuit_name}".encode("utf-8")
                        circuits[circuit_key] = Circuit(
                            "circuit:tag:" + hashlib.sha256(identity).hexdigest()[:16],
                            panel_id,
                            circuit_name,
                            (),
                        )
                    continue
            control_tag = tag_conventions.get(
                (entity.layer.casefold(), "control_group")
            )
            if control_tag is not None and _CONTROL_GROUP.fullmatch(entity.text):
                observed_control_tags += 1
                continue
            environmental_tag = tag_conventions.get(
                (entity.layer.casefold(), "environmental_qualifier")
            )
            if environmental_tag is not None and _WEATHERPROOF.fullmatch(entity.text):
                observed_environmental_qualifiers += 1
                continue
        reason = _unknown_reason(entity, symbols_by_block)
        unknown.append(
            UnknownObject(
                f"unknown:{index + 1}",
                entity.original_type,
                layer_id,
                reason,
                source,
            )
        )
    warnings = list(context.truncation_reasons)
    if context.database_modified != 0:
        warnings.append(
            "The active AutoCAD document had unsaved database modifications when context was captured."
        )
    if context.identity_source == "path_sha256":
        warnings.append(
            "The drawing had no AutoCAD fingerprint GUID; its local source identity was derived from the normalized document path."
        )
    if unknown:
        warnings.append(
            f"{len(unknown)} observed object(s) remain explicitly unclassified."
        )
    if conventions is not None:
        warnings.append(
            "Reviewed project conventions were applied only to the exact bound context evidence."
        )
        if conventions.tag_mappings:
            warnings.append(
                "Reviewed tag grammars are retained as evidence; spatial circuit/control association is not inferred."
            )
        if observed_control_tags:
            warnings.append(
                f"{observed_control_tags} reviewed control tag(s) remain unassociated until explicit topology is supplied."
            )
        if observed_environmental_qualifiers:
            warnings.append(
                f"{observed_environmental_qualifiers} reviewed weatherproof qualifier(s) remain unassociated until explicit topology is supplied."
            )
    return ElectricalProjectModel(
        1,
        "project:" + hashlib.sha256(normalized_document_id.encode("ascii")).hexdigest()[:16],
        normalized_document_id,
        context.context_id,
        unit_system_from_insunits(context.drawing_units),
        provenance,
        drawings,
        layers,
        tuple(devices),
        tuple(panels.values()),
        tuple(circuits.values()),
        (),
        (),
        (),
        (),
        tuple(routes),
        tuple(unknown),
        tuple(warnings),
    )


def rebind_project_conventions(
    previous: ProjectConventionProfile,
    context: AutoCADContext,
    *,
    context_evidence_sha256: str,
    source_drawing_sha256: str,
) -> ProjectConventionProfile:
    """Carry reviewed conventions to one fully matching new exact context."""
    _require_pattern(context_evidence_sha256, _SHA256, "evidence_sha256")
    _require_pattern(source_drawing_sha256, _SHA256, "source_drawing_sha256")
    if context.truncated:
        raise AutoCADContextError(
            "Project conventions cannot be rebound to a truncated context."
        )
    if context.database_modified & (1 | 4 | 32):
        raise AutoCADContextError(
            "Project convention rebind rejects persistent object, database-variable, or field DBMOD bits."
        )
    if (
        context.document_id != previous.document_id
        or source_drawing_sha256 != previous.source_drawing_sha256
    ):
        raise AutoCADContextError(
            "Project convention rebind requires the exact document and source drawing."
        )
    approved_blocks = {
        (item.layer_name.casefold(), item.block_name.casefold())
        for item in previous.block_mappings
    }
    approved_routes = {
        item.layer_name.casefold() for item in previous.route_mappings
    }
    tags = {
        (item.layer_name.casefold(), item.tag_kind): item
        for item in previous.tag_mappings
    }
    observed_blocks = 0
    for entity in context.entities:
        layer = entity.layer.casefold()
        if entity.entity_type == "INSERT":
            observed_blocks += 1
            if entity.block_name is None or (
                layer, entity.block_name.casefold()
            ) not in approved_blocks:
                raise AutoCADContextError(
                    "Project convention rebind encountered an unreviewed block/layer pair."
                )
        elif entity.entity_type in {"LINE", "LWPOLYLINE"}:
            if layer not in approved_routes:
                raise AutoCADContextError(
                    "Project convention rebind encountered an unreviewed route layer."
                )
        elif entity.entity_type in {"TEXT", "MTEXT"}:
            value = entity.text or ""
            circuit = tags.get((layer, "circuit_reference"))
            control = tags.get((layer, "control_group"))
            environmental = tags.get((layer, "environmental_qualifier"))
            if circuit is not None and _CIRCUIT_REFERENCE.fullmatch(value):
                continue
            if control is not None and _CONTROL_GROUP.fullmatch(value):
                continue
            if environmental is not None and _WEATHERPROOF.fullmatch(value):
                continue
            raise AutoCADContextError(
                "Project convention rebind encountered an unreviewed tag value or layer."
            )
        elif entity.entity_type == "UNSUPPORTED" and layer in approved_routes:
            continue
        else:
            raise AutoCADContextError(
                "Project convention rebind encountered an unreviewed entity class."
            )
    if observed_blocks == 0:
        raise AutoCADContextError(
            "Project convention rebind requires at least one reviewed block observation."
        )
    return create_project_convention_profile(
        profile_id="profile:rebind-" + context.context_id[:16],
        document_id=context.document_id,
        context_id=context.context_id,
        context_evidence_sha256=context_evidence_sha256,
        source_drawing_sha256=source_drawing_sha256,
        reviewed_by=previous.reviewed_by,
        reviewed_on=previous.reviewed_on,
        block_mappings=previous.block_mappings,
        route_mappings=previous.route_mappings,
        tag_mappings=previous.tag_mappings,
        assumptions=previous.assumptions + (
            "Rebound only after exact source, block/layer, route-layer, tag-grammar, and completeness checks.",
        ),
    )


def rebind_matching_project_conventions(
    previous: ProjectConventionProfile,
    context: AutoCADContext,
    *,
    context_evidence_sha256: str,
    source_drawing_sha256: str,
) -> ProjectConventionProfile:
    """Rebind only previously reviewed exact matches; preserve every other observation as unknown."""
    _require_pattern(context_evidence_sha256, _SHA256, "evidence_sha256")
    _require_pattern(source_drawing_sha256, _SHA256, "source_drawing_sha256")
    if context.truncated:
        raise AutoCADContextError(
            "Partial project convention rebind requires a complete context."
        )
    if context.database_modified & (1 | 4 | 32):
        raise AutoCADContextError(
            "Partial project convention rebind rejects persistent object, database-variable, or field DBMOD bits."
        )
    if (
        context.document_id != previous.document_id
        or source_drawing_sha256 != previous.source_drawing_sha256
    ):
        raise AutoCADContextError(
            "Partial project convention rebind requires the exact document and source drawing."
        )

    approved_blocks = {
        (item.layer_name.casefold(), item.block_name.casefold())
        for item in previous.block_mappings
    }
    approved_routes = {
        item.layer_name.casefold() for item in previous.route_mappings
    }
    tags = {
        (item.layer_name.casefold(), item.tag_kind): item
        for item in previous.tag_mappings
    }
    matched_blocks = unmatched_blocks = 0
    matched_routes = unmatched_routes = 0
    matched_tags = unmatched_text = 0
    for entity in context.entities:
        layer = entity.layer.casefold()
        if entity.entity_type == "INSERT":
            matched = entity.block_name is not None and (
                layer, entity.block_name.casefold()
            ) in approved_blocks
            if matched:
                matched_blocks += 1
            else:
                unmatched_blocks += 1
        elif entity.entity_type in {"LINE", "LWPOLYLINE"}:
            if layer in approved_routes:
                matched_routes += 1
            else:
                unmatched_routes += 1
        elif entity.entity_type in {"TEXT", "MTEXT"}:
            value = entity.text or ""
            circuit = tags.get((layer, "circuit_reference"))
            control = tags.get((layer, "control_group"))
            environmental = tags.get((layer, "environmental_qualifier"))
            if (
                (circuit is not None and _CIRCUIT_REFERENCE.fullmatch(value))
                or (control is not None and _CONTROL_GROUP.fullmatch(value))
                or (environmental is not None and _WEATHERPROOF.fullmatch(value))
            ):
                matched_tags += 1
            else:
                unmatched_text += 1

    if matched_blocks + matched_routes + matched_tags == 0:
        raise AutoCADContextError(
            "Partial project convention rebind found no reviewed exact observations."
        )
    coverage = (
        "Partial exact-match rebind; unmatched observations remain unknown: "
        f"blocks {matched_blocks} matched/{unmatched_blocks} unmatched, "
        f"route primitives {matched_routes} matched/{unmatched_routes} unmatched, "
        f"tags {matched_tags} matched/{unmatched_text} unmatched text."
    )
    return create_project_convention_profile(
        profile_id="profile:partial-rebind-" + context.context_id[:16],
        document_id=context.document_id,
        context_id=context.context_id,
        context_evidence_sha256=context_evidence_sha256,
        source_drawing_sha256=source_drawing_sha256,
        reviewed_by=previous.reviewed_by,
        reviewed_on=previous.reviewed_on,
        block_mappings=previous.block_mappings,
        route_mappings=previous.route_mappings,
        tag_mappings=previous.tag_mappings,
        assumptions=previous.assumptions + (coverage,),
    )


def _parse_layer(raw: object) -> ContextLayer:
    item = _mapping(raw, _LAYER_KEYS, "layer")
    return ContextLayer(
        _string(item["name"], "layer name", 255),
        _integer(item["color_index"], "layer color index", -255, 256),
        _string(item["linetype"], "layer linetype", 255),
        _boolean(item["is_off"], "layer is_off"),
        _boolean(item["is_frozen"], "layer is_frozen"),
        _boolean(item["is_locked"], "layer is_locked"),
    )


def _parse_entity(raw: object, limits: ContextLimits) -> ContextEntity:
    item = _mapping(raw, _ENTITY_KEYS, "entity")
    object_id = _string(item["object_id"], "entity object_id", 32)
    _require_pattern(object_id, _HANDLE, "entity object_id")
    entity_type = _string(item["entity_type"], "entity type", 32)
    if entity_type not in _ENTITY_TYPES:
        raise AutoCADContextError("AutoCAD context entity type is not allowlisted.")
    original_type = _string(item["original_type"], "original entity type", 128)
    layer = _string(item["layer"], "entity layer", 255)
    layout = _string(item["layout"], "entity layout", 255)
    visible = _boolean(item["visible"], "entity visible")
    points_raw = _list(item["points"], "entity points")
    if len(points_raw) > limits.maximum_points_per_entity:
        raise AutoCADContextError("AutoCAD entity exceeds its geometry-point limit.")
    points = tuple(_point(value) for value in points_raw)
    radius = _optional_number(item["radius"], "entity radius")
    if radius is not None and radius <= 0:
        raise AutoCADContextError("AutoCAD entity radius must be positive.")
    rotation = _optional_number(item["rotation_degrees"], "entity rotation")
    scale = None if item["scale"] is None else _point(item["scale"])
    if scale is not None and any(value == 0 for value in scale):
        raise AutoCADContextError(
            "AutoCAD block scale values must be non-zero; negative values represent mirroring."
        )
    block_name = _optional_string(item["block_name"], "block name", 255)
    text = _optional_string(item["text"], "entity text", limits.maximum_text_bytes)
    attributes_raw = _list(item["attributes"], "entity attributes")
    if len(attributes_raw) > limits.maximum_attributes_per_entity:
        raise AutoCADContextError("AutoCAD entity exceeds its attribute limit.")
    attributes: list[ContextAttribute] = []
    tags: list[str] = []
    for raw_attribute in attributes_raw:
        attribute = _mapping(raw_attribute, _ATTRIBUTE_KEYS, "attribute")
        tag = _string(attribute["tag"], "attribute tag", 128)
        value = _string(
            attribute["value"],
            "attribute value",
            limits.maximum_text_bytes,
            allow_empty=True,
        )
        tags.append(tag.casefold())
        attributes.append(ContextAttribute(tag, value))
    if len(tags) != len(set(tags)):
        raise AutoCADContextError("AutoCAD entity attribute tags must be unique.")
    _validate_entity_shape(
        entity_type,
        points,
        radius,
        rotation,
        scale,
        block_name,
        text,
        attributes,
    )
    return ContextEntity(
        object_id,
        entity_type,
        original_type,
        layer,
        layout,
        visible,
        points,
        radius,
        rotation,
        scale,
        block_name,
        text,
        tuple(attributes),
    )


def _validate_entity_shape(
    entity_type: str,
    points: tuple[tuple[float, float, float], ...],
    radius: float | None,
    rotation: float | None,
    scale: tuple[float, float, float] | None,
    block_name: str | None,
    text: str | None,
    attributes: list[ContextAttribute],
) -> None:
    if entity_type == "LINE" and len(points) != 2:
        raise AutoCADContextError("LINE context must contain exactly two points.")
    if entity_type == "LWPOLYLINE" and len(points) < 2:
        raise AutoCADContextError("LWPOLYLINE context must contain at least two points.")
    if entity_type in {"ARC", "CIRCLE"} and (not points or radius is None):
        raise AutoCADContextError(f"{entity_type} context requires center and radius.")
    if entity_type == "INSERT" and (
        len(points) != 1 or block_name is None or scale is None or rotation is None
    ):
        raise AutoCADContextError(
            "INSERT context requires insertion point, block, scale, and rotation."
        )
    if entity_type in {"TEXT", "MTEXT", "DIMENSION"} and text is None:
        raise AutoCADContextError(f"{entity_type} context requires bounded text.")
    if entity_type != "INSERT" and (block_name is not None or scale is not None or attributes):
        raise AutoCADContextError("Only INSERT context may contain block metadata.")


def _unknown_reason(
    entity: ContextEntity, symbols_by_block: Mapping[str, object]
) -> str:
    if entity.entity_type == "INSERT":
        if entity.block_name is None:
            return "Block reference has no validated name."
        if entity.block_name.casefold() not in symbols_by_block:
            return "Block name is not present in the reviewed electrical symbol catalog."
        return "Block reference lacks required validated transform data."
    if entity.entity_type in {"LINE", "LWPOLYLINE"}:
        return "Geometry is not on the reviewed E-WIRE electrical route layer."
    if entity.entity_type == "UNSUPPORTED":
        return "AutoCAD runtime type is outside the initial context allowlist."
    return "Observed geometry has no approved electrical classification in schema v1."


def _read_bounded(path: Path, maximum_bytes: int) -> bytes:
    try:
        size = path.stat().st_size
        if size <= 0 or size > maximum_bytes:
            raise AutoCADContextError("AutoCAD context is empty or exceeds its byte limit.")
        data = path.read_bytes()
    except AutoCADContextError:
        raise
    except OSError as exc:
        raise AutoCADContextError(f"Cannot read AutoCAD context evidence: {exc}") from exc
    if len(data) != size:
        raise AutoCADContextError("AutoCAD context changed while it was being read.")
    return data


def _mapping(raw: object, keys: frozenset[str], name: str) -> Mapping[str, Any]:
    if not isinstance(raw, Mapping) or set(raw) != keys:
        raise AutoCADContextError(f"AutoCAD {name} must contain exact schema fields.")
    return raw


def _list(raw: object, name: str) -> list[Any]:
    if not isinstance(raw, list):
        raise AutoCADContextError(f"AutoCAD {name} must be an array.")
    return raw


def _string(
    raw: object, name: str, maximum_bytes: int, *, allow_empty: bool = False
) -> str:
    if (
        not isinstance(raw, str)
        or (not raw and not allow_empty)
        or len(raw.encode("utf-8")) > maximum_bytes
        or any(ord(character) < 32 and character != "\t" for character in raw)
    ):
        qualifier = "" if allow_empty else "non-empty and "
        raise AutoCADContextError(
            f"AutoCAD {name} must be {qualifier}at most {maximum_bytes} bytes."
        )
    return raw


def _optional_string(raw: object, name: str, maximum_bytes: int) -> str | None:
    return (
        None
        if raw is None
        else _string(raw, name, maximum_bytes, allow_empty=True)
    )


def _validate_structure(raw: object, maximum_nesting: int) -> None:
    """Reject cyclic or excessively nested in-memory context before field traversal."""
    stack: list[tuple[object, int, frozenset[int]]] = [(raw, 1, frozenset())]
    while stack:
        value, depth, ancestors = stack.pop()
        if depth > maximum_nesting:
            raise AutoCADContextError("AutoCAD context exceeds its nesting limit.")
        if not isinstance(value, (Mapping, list)):
            continue
        identity = id(value)
        if identity in ancestors:
            raise AutoCADContextError("AutoCAD context contains a cyclic structure.")
        next_ancestors = ancestors | {identity}
        children = value.values() if isinstance(value, Mapping) else value
        stack.extend((child, depth + 1, next_ancestors) for child in children)


def _boolean(raw: object, name: str) -> bool:
    if not isinstance(raw, bool):
        raise AutoCADContextError(f"AutoCAD {name} must be boolean.")
    return raw


def _integer(raw: object, name: str, minimum: int, maximum: int) -> int:
    if (
        not isinstance(raw, int)
        or isinstance(raw, bool)
        or not minimum <= raw <= maximum
    ):
        raise AutoCADContextError(
            f"AutoCAD {name} must be an integer from {minimum} through {maximum}."
        )
    return raw


def _optional_number(raw: object, name: str) -> float | None:
    if raw is None:
        return None
    if (
        not isinstance(raw, (int, float))
        or isinstance(raw, bool)
        or not math.isfinite(raw)
        or abs(float(raw)) > 1_000_000_000
    ):
        raise AutoCADContextError(f"AutoCAD {name} must be a bounded finite number.")
    value = round(float(raw), 9)
    return 0.0 if value == 0 else value


def _point(raw: object) -> tuple[float, float, float]:
    if not isinstance(raw, list) or len(raw) != 3:
        raise AutoCADContextError("AutoCAD geometry point must contain three numbers.")
    values = tuple(_optional_number(value, "coordinate") for value in raw)
    if any(value is None for value in values):
        raise AutoCADContextError("AutoCAD geometry coordinates cannot be null.")
    return values[0], values[1], values[2]  # type: ignore[return-value]


def _utc_timestamp(raw: object) -> str:
    value = _string(raw, "captured_at", 40)
    if not value.endswith("Z"):
        raise AutoCADContextError("AutoCAD captured_at must be a UTC timestamp.")
    try:
        from datetime import datetime

        datetime.fromisoformat(value[:-1] + "+00:00")
    except ValueError as exc:
        raise AutoCADContextError("AutoCAD captured_at is invalid.") from exc
    return value


def _require_pattern(value: str, pattern: re.Pattern[str], name: str) -> None:
    if not isinstance(value, str) or pattern.fullmatch(value) is None:
        raise AutoCADContextError(f"AutoCAD {name} has an invalid format.")
