"""Immutable contracts for hash-bound, preview-only electrical drafting plans."""

from __future__ import annotations

import hashlib
import json
import math
import re
from dataclasses import dataclass, fields, is_dataclass
from datetime import datetime
from enum import Enum
from typing import Any

from agent.exceptions import DraftingContractError
from core.electrical_model import ElectricalProjectModel, Point3D, UnitSystem


_IDENTIFIER = re.compile(r"[a-z][a-z0-9_.:-]{0,127}")
_HEX32 = re.compile(r"[0-9a-f]{32}")
_SHA256 = re.compile(r"[0-9a-f]{64}")


class DraftObjectKind(str, Enum):
    """Object kinds that a Milestone 18 proposal may describe."""

    LAYER = "layer"
    DEVICE = "device"
    TERMINAL = "terminal"
    CIRCUIT = "circuit"
    ROUTE = "route"


class ProposalIssueKind(str, Enum):
    """Inputs the proposal records explicitly instead of guessing."""

    MISSING = "missing"
    UNKNOWN = "unknown"
    AMBIGUOUS = "ambiguous"


class PreviewAction(str, Enum):
    """Non-executable actions supported by the initial preview contract."""

    CREATE = "create"


class JsonContract:
    """Serialize an immutable drafting contract to JSON-compatible values."""

    def to_dict(self) -> dict[str, Any]:
        value = _json_value(self)
        if not isinstance(value, dict):
            raise DraftingContractError("Drafting contract must serialize as an object.")
        return value


@dataclass(frozen=True, slots=True)
class SourceModelBinding(JsonContract):
    """Bind proposed work to one exact context, model, and reviewed catalog."""

    project_id: str
    document_id: str
    drawing_id: str
    context_id: str
    context_evidence_sha256: str
    model_sha256: str
    symbol_catalog_sha256: str
    unit_system: UnitSystem

    def __post_init__(self) -> None:
        for value, name in (
            (self.project_id, "binding project ID"),
            (self.document_id, "binding document ID"),
            (self.drawing_id, "binding drawing ID"),
        ):
            _require_identifier(value, name)
        _require_pattern(self.context_id, _HEX32, "binding context ID")
        _require_pattern(
            self.context_evidence_sha256, _SHA256, "binding context evidence SHA-256"
        )
        _require_pattern(self.model_sha256, _SHA256, "binding model SHA-256")
        _require_pattern(
            self.symbol_catalog_sha256, _SHA256, "binding symbol catalog SHA-256"
        )
        if not isinstance(self.unit_system, UnitSystem):
            raise DraftingContractError("Binding unit system is invalid.")


def create_source_model_binding(
    model: ElectricalProjectModel,
    *,
    context_evidence_sha256: str,
    symbol_catalog_sha256: str,
) -> SourceModelBinding:
    """Create an exact binding from a trusted immutable shared-model snapshot."""
    if not isinstance(model, ElectricalProjectModel):
        raise DraftingContractError("Source binding requires a typed electrical model.")
    _require_pattern(
        context_evidence_sha256, _SHA256, "binding context evidence SHA-256"
    )
    if model.provenance.evidence_sha256 != context_evidence_sha256:
        raise DraftingContractError(
            "Context evidence SHA-256 does not match the model provenance."
        )
    return SourceModelBinding(
        model.project_id,
        model.document_id,
        model.drawings[0].drawing_id,
        model.context_id,
        context_evidence_sha256,
        canonical_model_sha256(model),
        symbol_catalog_sha256,
        model.unit_system,
    )


@dataclass(frozen=True, slots=True)
class ProposedLayer(JsonContract):
    """Exact layer schema required by proposed drafting objects."""

    layer_id: str
    name: str
    color_index: int
    linetype: str
    create_if_missing: bool

    def __post_init__(self) -> None:
        _require_identifier(self.layer_id, "proposed layer ID")
        _require_text(self.name, "proposed layer name", 255)
        if (
            not isinstance(self.color_index, int)
            or isinstance(self.color_index, bool)
            or not 1 <= self.color_index <= 255
        ):
            raise DraftingContractError("Proposed layer color index must be ACI 1-255.")
        _require_text(self.linetype, "proposed layer linetype", 255)
        if not isinstance(self.create_if_missing, bool):
            raise DraftingContractError(
                "Proposed layer create_if_missing flag must be boolean."
            )


@dataclass(frozen=True, slots=True)
class ProposedDevice(JsonContract):
    """One proposed reviewed-symbol insertion; it has no execution behavior."""

    device_id: str
    symbol_id: str
    symbol_definition_sha256: str
    block_name: str
    layer_id: str
    insertion_point: Point3D
    rotation_degrees: float
    scale: Point3D
    attributes: tuple[tuple[str, str], ...]

    def __post_init__(self) -> None:
        for value, name in (
            (self.device_id, "proposed device ID"),
            (self.symbol_id, "proposed symbol ID"),
            (self.layer_id, "proposed device layer ID"),
        ):
            _require_identifier(value, name)
        _require_pattern(
            self.symbol_definition_sha256, _SHA256, "symbol definition SHA-256"
        )
        _require_text(self.block_name, "proposed block name", 255)
        _require_point(self.insertion_point, "proposed device insertion point")
        _require_finite(self.rotation_degrees, "proposed device rotation")
        _require_point(self.scale, "proposed device scale")
        _require_pairs(self.attributes, "proposed device attributes", 64)


@dataclass(frozen=True, slots=True)
class ProposedTerminal(JsonContract):
    """One explicitly named terminal associated with a proposed device identity."""

    terminal_id: str
    device_id: str
    name: str
    position: Point3D | None

    def __post_init__(self) -> None:
        _require_identifier(self.terminal_id, "proposed terminal ID")
        _require_identifier(self.device_id, "proposed terminal device ID")
        _require_text(self.name, "proposed terminal name", 128)
        if self.position is not None:
            _require_point(self.position, "proposed terminal position")


@dataclass(frozen=True, slots=True)
class ProposedCircuit(JsonContract):
    """One proposed circuit with explicit terminal and route identities."""

    circuit_id: str
    name: str
    terminal_ids: tuple[str, ...]
    route_ids: tuple[str, ...]

    def __post_init__(self) -> None:
        _require_identifier(self.circuit_id, "proposed circuit ID")
        _require_text(self.name, "proposed circuit name", 128)
        _require_identifiers(self.terminal_ids, "proposed circuit terminal IDs", 1024)
        _require_identifiers(self.route_ids, "proposed circuit route IDs", 256)


@dataclass(frozen=True, slots=True)
class ProposedRoute(JsonContract):
    """One proposed route in the binding's explicit unit system."""

    route_id: str
    circuit_id: str
    layer_id: str
    start_terminal_id: str
    end_terminal_id: str
    points: tuple[Point3D, ...]

    def __post_init__(self) -> None:
        _require_identifier(self.route_id, "proposed route ID")
        _require_identifier(self.circuit_id, "proposed route circuit ID")
        _require_identifier(self.layer_id, "proposed route layer ID")
        _require_identifier(self.start_terminal_id, "proposed route start terminal ID")
        _require_identifier(self.end_terminal_id, "proposed route end terminal ID")
        if not isinstance(self.points, tuple) or len(self.points) > 4096:
            raise DraftingContractError(
                "Proposed route points must be an immutable tuple of at most 4096 points."
            )
        for point in self.points:
            _require_point(point, "proposed route point")


@dataclass(frozen=True, slots=True)
class ProposalInputIssue(JsonContract):
    """One unknown, missing, or ambiguous input retained for later validation."""

    issue_id: str
    kind: ProposalIssueKind
    field_name: str
    reason: str
    object_ids: tuple[str, ...]

    def __post_init__(self) -> None:
        _require_identifier(self.issue_id, "proposal issue ID")
        if not isinstance(self.kind, ProposalIssueKind):
            raise DraftingContractError("Proposal issue kind is invalid.")
        if not isinstance(self.field_name, str) or re.fullmatch(
            r"[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*){0,7}", self.field_name
        ) is None:
            raise DraftingContractError("Proposal issue field name is invalid.")
        _require_text(self.reason, "proposal issue reason", 512)
        _require_identifiers(self.object_ids, "proposal issue object IDs", 256)


@dataclass(frozen=True, slots=True)
class DraftingProposal(JsonContract):
    """Exact immutable proposed changes, before deterministic validation or approval."""

    schema_version: int
    proposal_id: str
    binding: SourceModelBinding
    layers: tuple[ProposedLayer, ...]
    devices: tuple[ProposedDevice, ...]
    terminals: tuple[ProposedTerminal, ...]
    circuits: tuple[ProposedCircuit, ...]
    routes: tuple[ProposedRoute, ...]
    input_issues: tuple[ProposalInputIssue, ...]
    created_at: str
    expires_at: str
    proposal_sha256: str
    drawing_mutation_performed: bool = False

    def __post_init__(self) -> None:
        if self.schema_version != 1:
            raise DraftingContractError("Drafting proposal schema_version must be 1.")
        _require_pattern(self.proposal_id, _HEX32, "drafting proposal ID")
        if not isinstance(self.binding, SourceModelBinding):
            raise DraftingContractError("Drafting proposal binding is invalid.")
        _require_records(self.layers, ProposedLayer, "proposal layers", 256)
        _require_records(self.devices, ProposedDevice, "proposal devices", 256)
        _require_records(self.terminals, ProposedTerminal, "proposal terminals", 1024)
        _require_records(self.circuits, ProposedCircuit, "proposal circuits", 256)
        _require_records(self.routes, ProposedRoute, "proposal routes", 256)
        _require_records(
            self.input_issues, ProposalInputIssue, "proposal input issues", 256
        )
        if not self.devices and not self.terminals and not self.circuits and not self.routes:
            raise DraftingContractError("Drafting proposal must contain at least one object.")
        for values, attribute, name in (
            (self.layers, "layer_id", "layer"),
            (self.devices, "device_id", "device"),
            (self.terminals, "terminal_id", "terminal"),
            (self.circuits, "circuit_id", "circuit"),
            (self.routes, "route_id", "route"),
            (self.input_issues, "issue_id", "input issue"),
        ):
            _require_unique_attribute(values, attribute, f"Proposal {name} IDs")
        created = _require_utc(self.created_at, "proposal created_at")
        expires = _require_utc(self.expires_at, "proposal expires_at")
        if created >= expires:
            raise DraftingContractError("Drafting proposal must expire after creation.")
        if self.drawing_mutation_performed is not False:
            raise DraftingContractError("A drafting proposal cannot claim drawing mutation.")
        _require_pattern(self.proposal_sha256, _SHA256, "drafting proposal SHA-256")
        if self.proposal_sha256 != _contract_sha256(self, "proposal_sha256"):
            raise DraftingContractError(
                "Drafting proposal SHA-256 does not match its exact contents."
            )


def create_drafting_proposal(
    *,
    proposal_id: str,
    binding: SourceModelBinding,
    layers: tuple[ProposedLayer, ...],
    devices: tuple[ProposedDevice, ...],
    terminals: tuple[ProposedTerminal, ...],
    circuits: tuple[ProposedCircuit, ...],
    routes: tuple[ProposedRoute, ...],
    input_issues: tuple[ProposalInputIssue, ...],
    created_at: str,
    expires_at: str,
) -> DraftingProposal:
    """Create a self-hashed immutable proposal without validating its topology."""
    values: dict[str, Any] = {
        "schema_version": 1,
        "proposal_id": proposal_id,
        "binding": binding,
        "layers": layers,
        "devices": devices,
        "terminals": terminals,
        "circuits": circuits,
        "routes": routes,
        "input_issues": input_issues,
        "created_at": created_at,
        "expires_at": expires_at,
        "drawing_mutation_performed": False,
    }
    digest = _canonical_sha256(values)
    return DraftingProposal(
        1,
        proposal_id,
        binding,
        layers,
        devices,
        terminals,
        circuits,
        routes,
        input_issues,
        created_at,
        expires_at,
        digest,
        False,
    )


def drafting_proposal_from_dict(value: object) -> DraftingProposal:
    """Parse one untrusted bounded JSON object without coercion or extra fields."""
    _validate_json_structure(value)
    raw = _object(
        value,
        {
            "schema_version",
            "proposal_id",
            "binding",
            "layers",
            "devices",
            "terminals",
            "circuits",
            "routes",
            "input_issues",
            "created_at",
            "expires_at",
            "proposal_sha256",
            "drawing_mutation_performed",
        },
        "drafting proposal",
    )
    binding_raw = _object(
        raw["binding"],
        {
            "project_id",
            "document_id",
            "drawing_id",
            "context_id",
            "context_evidence_sha256",
            "model_sha256",
            "symbol_catalog_sha256",
            "unit_system",
        },
        "drafting binding",
    )
    try:
        unit_system = UnitSystem(_string(binding_raw["unit_system"], "binding unit system"))
    except ValueError as exc:
        raise DraftingContractError("Binding unit system is invalid.") from exc
    binding = SourceModelBinding(
        _string(binding_raw["project_id"], "binding project ID"),
        _string(binding_raw["document_id"], "binding document ID"),
        _string(binding_raw["drawing_id"], "binding drawing ID"),
        _string(binding_raw["context_id"], "binding context ID"),
        _string(binding_raw["context_evidence_sha256"], "context evidence SHA-256"),
        _string(binding_raw["model_sha256"], "model SHA-256"),
        _string(binding_raw["symbol_catalog_sha256"], "catalog SHA-256"),
        unit_system,
    )
    return DraftingProposal(
        _integer(raw["schema_version"], "proposal schema_version"),
        _string(raw["proposal_id"], "proposal ID"),
        binding,
        tuple(_layer_from_dict(item) for item in _array(raw["layers"], "proposal layers", 256)),
        tuple(_device_from_dict(item) for item in _array(raw["devices"], "proposal devices", 256)),
        tuple(
            _terminal_from_dict(item)
            for item in _array(raw["terminals"], "proposal terminals", 1024)
        ),
        tuple(_circuit_from_dict(item) for item in _array(raw["circuits"], "proposal circuits", 256)),
        tuple(_route_from_dict(item) for item in _array(raw["routes"], "proposal routes", 256)),
        tuple(
            _issue_from_dict(item)
            for item in _array(raw["input_issues"], "proposal input issues", 256)
        ),
        _string(raw["created_at"], "proposal created_at"),
        _string(raw["expires_at"], "proposal expires_at"),
        _string(raw["proposal_sha256"], "proposal SHA-256"),
        _boolean(raw["drawing_mutation_performed"], "drawing mutation flag"),
    )


def _layer_from_dict(value: object) -> ProposedLayer:
    raw = _object(
        value,
        {"layer_id", "name", "color_index", "linetype", "create_if_missing"},
        "proposed layer",
    )
    return ProposedLayer(
        _string(raw["layer_id"], "layer ID"),
        _string(raw["name"], "layer name"),
        _integer(raw["color_index"], "layer color index"),
        _string(raw["linetype"], "layer linetype"),
        _boolean(raw["create_if_missing"], "layer create flag"),
    )


def _device_from_dict(value: object) -> ProposedDevice:
    raw = _object(
        value,
        {
            "device_id", "symbol_id", "symbol_definition_sha256", "block_name",
            "layer_id", "insertion_point", "rotation_degrees", "scale", "attributes",
        },
        "proposed device",
    )
    attributes = tuple(
        (
            _string(_pair(item, "device attribute")[0], "attribute key"),
            _string(_pair(item, "device attribute")[1], "attribute value", allow_empty=True),
        )
        for item in _array(raw["attributes"], "device attributes", 64)
    )
    return ProposedDevice(
        _string(raw["device_id"], "device ID"),
        _string(raw["symbol_id"], "symbol ID"),
        _string(raw["symbol_definition_sha256"], "symbol definition SHA-256"),
        _string(raw["block_name"], "block name"),
        _string(raw["layer_id"], "device layer ID"),
        _point_from_json(raw["insertion_point"], "device insertion point"),
        _number(raw["rotation_degrees"], "device rotation"),
        _point_from_json(raw["scale"], "device scale"),
        attributes,
    )


def _terminal_from_dict(value: object) -> ProposedTerminal:
    raw = _object(value, {"terminal_id", "device_id", "name", "position"}, "proposed terminal")
    position = None if raw["position"] is None else _point_from_json(raw["position"], "terminal position")
    return ProposedTerminal(
        _string(raw["terminal_id"], "terminal ID"),
        _string(raw["device_id"], "terminal device ID"),
        _string(raw["name"], "terminal name"),
        position,
    )


def _circuit_from_dict(value: object) -> ProposedCircuit:
    raw = _object(value, {"circuit_id", "name", "terminal_ids", "route_ids"}, "proposed circuit")
    return ProposedCircuit(
        _string(raw["circuit_id"], "circuit ID"),
        _string(raw["name"], "circuit name"),
        _identifier_array(raw["terminal_ids"], "circuit terminal IDs", 1024),
        _identifier_array(raw["route_ids"], "circuit route IDs", 256),
    )


def _route_from_dict(value: object) -> ProposedRoute:
    raw = _object(
        value,
        {"route_id", "circuit_id", "layer_id", "start_terminal_id", "end_terminal_id", "points"},
        "proposed route",
    )
    return ProposedRoute(
        _string(raw["route_id"], "route ID"),
        _string(raw["circuit_id"], "route circuit ID"),
        _string(raw["layer_id"], "route layer ID"),
        _string(raw["start_terminal_id"], "route start terminal ID"),
        _string(raw["end_terminal_id"], "route end terminal ID"),
        tuple(
            _point_from_json(item, "route point")
            for item in _array(raw["points"], "route points", 4096)
        ),
    )


def _issue_from_dict(value: object) -> ProposalInputIssue:
    raw = _object(value, {"issue_id", "kind", "field_name", "reason", "object_ids"}, "proposal issue")
    try:
        kind = ProposalIssueKind(_string(raw["kind"], "proposal issue kind"))
    except ValueError as exc:
        raise DraftingContractError("Proposal issue kind is invalid.") from exc
    return ProposalInputIssue(
        _string(raw["issue_id"], "proposal issue ID"),
        kind,
        _string(raw["field_name"], "proposal issue field"),
        _string(raw["reason"], "proposal issue reason"),
        _identifier_array(raw["object_ids"], "proposal issue object IDs", 256),
    )


@dataclass(frozen=True, slots=True)
class PreviewItem(JsonContract):
    """One non-executable visible item in an exact proposal preview."""

    kind: DraftObjectKind
    object_id: str
    action: PreviewAction = PreviewAction.CREATE

    def __post_init__(self) -> None:
        if not isinstance(self.kind, DraftObjectKind):
            raise DraftingContractError("Preview item kind is invalid.")
        _require_identifier(self.object_id, "preview object ID")
        if self.action is not PreviewAction.CREATE:
            raise DraftingContractError("Initial preview items support create only.")


@dataclass(frozen=True, slots=True)
class DraftingPreview(JsonContract):
    """Reviewable snapshot of every item in one exact hash-bound proposal."""

    schema_version: int
    preview_id: str
    proposal: DraftingProposal
    binding: SourceModelBinding
    items: tuple[PreviewItem, ...]
    created_at: str
    expires_at: str
    preview_sha256: str
    drawing_mutation_performed: bool = False
    execution_approved: bool = False

    def __post_init__(self) -> None:
        if self.schema_version != 1:
            raise DraftingContractError("Drafting preview schema_version must be 1.")
        _require_pattern(self.preview_id, _HEX32, "drafting preview ID")
        if not isinstance(self.proposal, DraftingProposal):
            raise DraftingContractError("Drafting preview proposal is invalid.")
        if self.binding != self.proposal.binding:
            raise DraftingContractError("Preview binding differs from its exact proposal.")
        _require_records(self.items, PreviewItem, "preview items", 1792)
        expected = _proposal_item_keys(self.proposal)
        actual = tuple((item.kind, item.object_id) for item in self.items)
        if actual != expected:
            raise DraftingContractError(
                "Preview items do not match the exact ordered proposal objects."
            )
        created = _require_utc(self.created_at, "preview created_at")
        expires = _require_utc(self.expires_at, "preview expires_at")
        proposal_created = _require_utc(self.proposal.created_at, "proposal created_at")
        proposal_expires = _require_utc(self.proposal.expires_at, "proposal expires_at")
        if not proposal_created <= created < expires <= proposal_expires:
            raise DraftingContractError(
                "Preview timestamps must remain inside the proposal lifetime."
            )
        if self.drawing_mutation_performed is not False:
            raise DraftingContractError("A drafting preview cannot claim drawing mutation.")
        if self.execution_approved is not False:
            raise DraftingContractError("A drafting preview cannot grant execution approval.")
        _require_pattern(self.preview_sha256, _SHA256, "drafting preview SHA-256")
        if self.preview_sha256 != _contract_sha256(self, "preview_sha256"):
            raise DraftingContractError(
                "Drafting preview SHA-256 does not match its exact contents."
            )


def create_drafting_preview(
    *,
    preview_id: str,
    proposal: DraftingProposal,
    created_at: str,
    expires_at: str,
) -> DraftingPreview:
    """Create a read-only preview snapshot containing every proposed object."""
    if not isinstance(proposal, DraftingProposal):
        raise DraftingContractError("Drafting preview requires a typed proposal.")
    items = tuple(
        PreviewItem(kind, object_id)
        for kind, object_id in _proposal_item_keys(proposal)
    )
    values: dict[str, Any] = {
        "schema_version": 1,
        "preview_id": preview_id,
        "proposal": proposal,
        "binding": proposal.binding,
        "items": items,
        "created_at": created_at,
        "expires_at": expires_at,
        "drawing_mutation_performed": False,
        "execution_approved": False,
    }
    digest = _canonical_sha256(values)
    return DraftingPreview(
        1,
        preview_id,
        proposal,
        proposal.binding,
        items,
        created_at,
        expires_at,
        digest,
        False,
        False,
    )


def canonical_model_sha256(model: ElectricalProjectModel) -> str:
    """Return the deterministic SHA-256 of one immutable shared-model snapshot."""
    if not isinstance(model, ElectricalProjectModel):
        raise DraftingContractError("Model hashing requires a typed electrical model.")
    return _canonical_sha256(model.to_dict())


def _proposal_item_keys(proposal: DraftingProposal) -> tuple[tuple[DraftObjectKind, str], ...]:
    return (
        tuple((DraftObjectKind.LAYER, item.layer_id) for item in proposal.layers)
        + tuple((DraftObjectKind.DEVICE, item.device_id) for item in proposal.devices)
        + tuple((DraftObjectKind.TERMINAL, item.terminal_id) for item in proposal.terminals)
        + tuple((DraftObjectKind.CIRCUIT, item.circuit_id) for item in proposal.circuits)
        + tuple((DraftObjectKind.ROUTE, item.route_id) for item in proposal.routes)
    )


def _contract_sha256(contract: JsonContract, digest_field: str) -> str:
    value = contract.to_dict()
    del value[digest_field]
    return _canonical_sha256(value)


def _canonical_sha256(value: object) -> str:
    encoded = json.dumps(
        _json_value(value), ensure_ascii=False, sort_keys=True, separators=(",", ":")
    ).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest()


def _json_value(value: Any) -> Any:
    if isinstance(value, Enum):
        return value.value
    if is_dataclass(value):
        return {item.name: _json_value(getattr(value, item.name)) for item in fields(value)}
    if isinstance(value, tuple):
        return [_json_value(item) for item in value]
    if isinstance(value, dict):
        return {str(key): _json_value(item) for key, item in value.items()}
    return value


def _require_identifier(value: object, name: str) -> None:
    if not isinstance(value, str) or _IDENTIFIER.fullmatch(value) is None:
        raise DraftingContractError(f"{name} must be a bounded lowercase identifier.")


def _require_pattern(value: object, pattern: re.Pattern[str], name: str) -> None:
    if not isinstance(value, str) or pattern.fullmatch(value) is None:
        raise DraftingContractError(f"{name} has an invalid format.")


def _require_text(value: object, name: str, maximum_bytes: int) -> None:
    if (
        not isinstance(value, str)
        or not value.strip()
        or len(value.encode("utf-8")) > maximum_bytes
        or any(ord(character) < 32 and character not in "\t\n" for character in value)
    ):
        raise DraftingContractError(
            f"{name} must be non-empty bounded UTF-8 without unsupported controls."
        )


def _require_finite(value: object, name: str) -> None:
    if (
        not isinstance(value, (int, float))
        or isinstance(value, bool)
        or not math.isfinite(value)
        or abs(float(value)) > 1_000_000_000
    ):
        raise DraftingContractError(f"{name} must be finite and bounded.")


def _require_point(value: object, name: str) -> None:
    if not isinstance(value, tuple) or len(value) != 3:
        raise DraftingContractError(f"{name} must be an immutable three-coordinate point.")
    for coordinate in value:
        _require_finite(coordinate, name)


def _require_pairs(values: object, name: str, maximum: int) -> None:
    if not isinstance(values, tuple) or len(values) > maximum:
        raise DraftingContractError(f"{name} must be a bounded immutable tuple.")
    keys: list[str] = []
    for value in values:
        if not isinstance(value, tuple) or len(value) != 2:
            raise DraftingContractError(f"{name} must contain immutable text pairs.")
        key, item = value
        _require_text(key, name, 128)
        if not isinstance(item, str) or len(item.encode("utf-8")) > 4096:
            raise DraftingContractError(f"{name} values must be bounded text.")
        keys.append(key.casefold())
    if len(keys) != len(set(keys)):
        raise DraftingContractError(f"{name} keys must be unique.")


def _require_identifiers(values: object, name: str, maximum: int) -> None:
    if not isinstance(values, tuple) or len(values) > maximum:
        raise DraftingContractError(f"{name} must be a bounded immutable tuple.")
    for value in values:
        _require_identifier(value, name)
    if len(values) != len(set(values)):
        raise DraftingContractError(f"{name} must be unique.")


def _require_records(values: object, record_type: type, name: str, maximum: int) -> None:
    if (
        not isinstance(values, tuple)
        or len(values) > maximum
        or any(not isinstance(value, record_type) for value in values)
    ):
        raise DraftingContractError(f"{name} must be a bounded immutable typed tuple.")


def _require_unique_attribute(values: tuple[Any, ...], attribute: str, name: str) -> None:
    identities = [getattr(value, attribute) for value in values]
    if len(identities) != len(set(identities)):
        raise DraftingContractError(f"{name} must be unique.")


def _require_utc(value: object, name: str) -> datetime:
    if not isinstance(value, str) or not value.endswith("Z") or len(value) > 40:
        raise DraftingContractError(f"{name} must be a bounded UTC timestamp.")
    try:
        return datetime.fromisoformat(value[:-1] + "+00:00")
    except ValueError as exc:
        raise DraftingContractError(f"{name} must be a valid UTC timestamp.") from exc


def _validate_json_structure(value: object) -> None:
    """Bound untrusted decoded JSON before constructing typed records."""
    stack: list[tuple[object, int, frozenset[int]]] = [(value, 1, frozenset())]
    nodes = 0
    while stack:
        item, depth, ancestors = stack.pop()
        nodes += 1
        if nodes > 20_000 or depth > 16:
            raise DraftingContractError("Drafting proposal JSON exceeds structure limits.")
        if isinstance(item, dict):
            identity = id(item)
            if identity in ancestors:
                raise DraftingContractError("Drafting proposal JSON contains a cycle.")
            if any(not isinstance(key, str) for key in item):
                raise DraftingContractError("Drafting proposal JSON keys must be text.")
            nested = ancestors | {identity}
            stack.extend((child, depth + 1, nested) for child in item.values())
        elif isinstance(item, list):
            identity = id(item)
            if identity in ancestors:
                raise DraftingContractError("Drafting proposal JSON contains a cycle.")
            nested = ancestors | {identity}
            stack.extend((child, depth + 1, nested) for child in item)
        elif isinstance(item, str):
            if len(item.encode("utf-8")) > 65_536:
                raise DraftingContractError("Drafting proposal JSON text is too large.")
        elif item is not None and not isinstance(item, (bool, int, float)):
            raise DraftingContractError("Drafting proposal contains a non-JSON value.")


def _object(value: object, keys: set[str], name: str) -> dict[str, Any]:
    if not isinstance(value, dict) or set(value) != keys:
        raise DraftingContractError(f"{name} fields are incomplete or unexpected.")
    return value


def _array(value: object, name: str, maximum: int) -> list[Any]:
    if not isinstance(value, list) or len(value) > maximum:
        raise DraftingContractError(f"{name} must be a bounded JSON array.")
    return value


def _pair(value: object, name: str) -> list[Any]:
    if not isinstance(value, list) or len(value) != 2:
        raise DraftingContractError(f"{name} must contain exactly two JSON values.")
    return value


def _string(
    value: object,
    name: str,
    maximum_bytes: int = 65_536,
    *,
    allow_empty: bool = False,
) -> str:
    if (
        not isinstance(value, str)
        or (not allow_empty and not value)
        or len(value.encode("utf-8")) > maximum_bytes
    ):
        raise DraftingContractError(f"{name} must be bounded JSON text.")
    return value


def _integer(value: object, name: str) -> int:
    if not isinstance(value, int) or isinstance(value, bool):
        raise DraftingContractError(f"{name} must be a JSON integer without coercion.")
    return value


def _number(value: object, name: str) -> int | float:
    if (
        not isinstance(value, (int, float))
        or isinstance(value, bool)
        or not math.isfinite(value)
    ):
        raise DraftingContractError(f"{name} must be a finite JSON number.")
    return value


def _boolean(value: object, name: str) -> bool:
    if not isinstance(value, bool):
        raise DraftingContractError(f"{name} must be a JSON boolean without coercion.")
    return value


def _point_from_json(value: object, name: str) -> Point3D:
    if not isinstance(value, list) or len(value) != 3:
        raise DraftingContractError(f"{name} must be a three-number JSON array.")
    return (
        _number(value[0], name),
        _number(value[1], name),
        _number(value[2], name),
    )


def _identifier_array(value: object, name: str, maximum: int) -> tuple[str, ...]:
    return tuple(
        _string(item, name) for item in _array(value, name, maximum)
    )
