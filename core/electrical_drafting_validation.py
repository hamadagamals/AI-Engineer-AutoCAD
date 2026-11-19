"""Deterministic fail-closed validation for electrical drafting proposals."""

from __future__ import annotations

import hashlib
import json
import math
from dataclasses import dataclass, fields, is_dataclass
from enum import Enum
from pathlib import Path
from typing import Any

from agent.exceptions import DraftingContractError, ElectricalSymbolCatalogError
from core.electrical_drafting import (
    DraftingProposal,
    JsonContract,
    ProposedCircuit,
    ProposedRoute,
    canonical_model_sha256,
)
from core.electrical_model import ElectricalProjectModel, Point3D, UnitSystem
from integrations.electrical_symbols import ElectricalSymbol, ElectricalSymbolCatalog


_VALIDATOR_VERSION = "m18.validation.v1"
_MAX_PHYSICAL_MILLIMETERS = 1_000_000_000.0
_ENDPOINT_TOLERANCE_MILLIMETERS = 0.001
_MILLIMETERS_PER_UNIT = {
    UnitSystem.INCH: 25.4,
    UnitSystem.FOOT: 304.8,
    UnitSystem.MILLIMETER: 1.0,
    UnitSystem.CENTIMETER: 10.0,
    UnitSystem.METER: 1000.0,
}
_CATALOG_UNITS = {"millimeters": UnitSystem.MILLIMETER}


@dataclass(frozen=True, slots=True)
class DraftingValidationFinding(JsonContract):
    """One deterministic reason why a proposal cannot proceed to preview."""

    code: str
    message: str
    object_ids: tuple[str, ...]

    def __post_init__(self) -> None:
        if (
            not isinstance(self.code, str)
            or not self.code
            or len(self.code) > 96
            or any(
                part == "" or not part.replace("_", "").isalnum()
                for part in self.code.split(".")
            )
        ):
            raise DraftingContractError("Drafting validation finding code is invalid.")
        if (
            not isinstance(self.message, str)
            or not self.message.strip()
            or len(self.message.encode("utf-8")) > 512
        ):
            raise DraftingContractError("Drafting validation finding message is invalid.")
        if not isinstance(self.object_ids, tuple) or len(self.object_ids) > 256:
            raise DraftingContractError(
                "Drafting validation finding object IDs must be an immutable tuple."
            )
        if any(not isinstance(value, str) or not value for value in self.object_ids):
            raise DraftingContractError("Drafting validation finding object ID is invalid.")
        if len(self.object_ids) != len(set(self.object_ids)):
            raise DraftingContractError(
                "Drafting validation finding object IDs must be unique."
            )


@dataclass(frozen=True, slots=True)
class DraftingValidationResult(JsonContract):
    """Hash-bound deterministic validation outcome with no drawing side effects."""

    schema_version: int
    validator_version: str
    proposal_id: str
    proposal_sha256: str
    model_sha256: str
    symbol_catalog_sha256: str
    findings: tuple[DraftingValidationFinding, ...]
    is_valid: bool
    preview_allowed: bool
    drawing_mutation_performed: bool
    validation_sha256: str

    def __post_init__(self) -> None:
        if self.schema_version != 1 or self.validator_version != _VALIDATOR_VERSION:
            raise DraftingContractError("Drafting validation result version is invalid.")
        for value, name, length in (
            (self.proposal_id, "proposal ID", 32),
            (self.proposal_sha256, "proposal SHA-256", 64),
            (self.model_sha256, "model SHA-256", 64),
            (self.symbol_catalog_sha256, "catalog SHA-256", 64),
            (self.validation_sha256, "validation SHA-256", 64),
        ):
            if not isinstance(value, str) or len(value) != length or any(
                character not in "0123456789abcdef" for character in value
            ):
                raise DraftingContractError(f"Drafting validation {name} is invalid.")
        if not isinstance(self.findings, tuple) or any(
            not isinstance(item, DraftingValidationFinding) for item in self.findings
        ):
            raise DraftingContractError(
                "Drafting validation findings must be an immutable typed tuple."
            )
        expected_valid = not self.findings
        if self.is_valid is not expected_valid or self.preview_allowed is not expected_valid:
            raise DraftingContractError(
                "Drafting validation readiness does not match its findings."
            )
        if self.drawing_mutation_performed is not False:
            raise DraftingContractError("Drafting validation cannot claim drawing mutation.")
        if self.validation_sha256 != _result_sha256(self):
            raise DraftingContractError(
                "Drafting validation SHA-256 does not match its exact contents."
            )


def validate_drafting_proposal(
    proposal: DraftingProposal,
    *,
    model: ElectricalProjectModel,
    symbol_catalog_path: Path,
) -> DraftingValidationResult:
    """Validate one proposal without mutating files, state, or an AutoCAD drawing."""
    if not isinstance(proposal, DraftingProposal):
        raise DraftingContractError("Drafting validation requires a typed proposal.")
    if not isinstance(model, ElectricalProjectModel):
        raise DraftingContractError("Drafting validation requires a typed electrical model.")
    if not isinstance(symbol_catalog_path, Path):
        raise DraftingContractError("Drafting validation catalog path must be a Path.")

    findings: list[DraftingValidationFinding] = []
    _validate_binding(proposal, model, findings)
    catalog, catalog_sha256 = _load_catalog_snapshot(symbol_catalog_path, findings)
    if catalog_sha256 and catalog_sha256 != proposal.binding.symbol_catalog_sha256:
        _add(
            findings,
            "catalog.hash_mismatch",
            "The reviewed symbol catalog SHA-256 differs from the proposal binding.",
        )
    _validate_identities(proposal, model, findings)
    _validate_input_issues(proposal, findings)
    _validate_layers(proposal, model, catalog, findings)
    _validate_devices(proposal, model, catalog, findings)
    _validate_geometry(proposal, findings)
    _validate_topology(proposal, findings)
    return _create_result(proposal, tuple(findings))


def _validate_binding(
    proposal: DraftingProposal,
    model: ElectricalProjectModel,
    findings: list[DraftingValidationFinding],
) -> None:
    binding = proposal.binding
    expected = (
        (binding.project_id, model.project_id, "binding.project_mismatch"),
        (binding.document_id, model.document_id, "binding.document_mismatch"),
        (binding.drawing_id, model.drawings[0].drawing_id, "binding.drawing_mismatch"),
        (binding.context_id, model.context_id, "binding.context_mismatch"),
        (
            binding.context_evidence_sha256,
            model.provenance.evidence_sha256,
            "binding.context_evidence_mismatch",
        ),
        (binding.unit_system, model.unit_system, "binding.unit_mismatch"),
        (binding.model_sha256, canonical_model_sha256(model), "binding.model_hash_mismatch"),
    )
    for actual, wanted, code in expected:
        if actual != wanted:
            _add(findings, code, "The proposal binding does not match the exact source model.")
    if binding.unit_system not in _MILLIMETERS_PER_UNIT:
        _add(
            findings,
            "unit.unsupported",
            "Drafting requires an explicit supported physical length unit.",
        )


def _load_catalog_snapshot(
    path: Path, findings: list[DraftingValidationFinding]
) -> tuple[ElectricalSymbolCatalog | None, str]:
    try:
        resolved = path.expanduser().resolve(strict=True)
        if path.is_symlink() or not resolved.is_file():
            raise OSError("catalog is not a regular non-link file")
        before = resolved.read_bytes()
        before_sha256 = hashlib.sha256(before).hexdigest()
        catalog = ElectricalSymbolCatalog.load(resolved)
        after = resolved.read_bytes()
    except (OSError, ElectricalSymbolCatalogError):
        _add(
            findings,
            "catalog.unavailable",
            "The symbol catalog could not be loaded as a trusted immutable snapshot.",
        )
        return None, "0" * 64
    if before != after:
        _add(
            findings,
            "catalog.changed_during_validation",
            "The symbol catalog changed during validation.",
        )
        return None, hashlib.sha256(after).hexdigest()
    return catalog, before_sha256


def _validate_identities(
    proposal: DraftingProposal,
    model: ElectricalProjectModel,
    findings: list[DraftingValidationFinding],
) -> None:
    proposed = (
        [(item.layer_id, "layer") for item in proposal.layers]
        + [(item.device_id, "device") for item in proposal.devices]
        + [(item.terminal_id, "terminal") for item in proposal.terminals]
        + [(item.circuit_id, "circuit") for item in proposal.circuits]
        + [(item.route_id, "route") for item in proposal.routes]
    )
    seen: dict[str, str] = {}
    for identity, kind in proposed:
        if identity in seen:
            _add(
                findings,
                "identity.cross_kind_duplicate",
                "A proposed identity is reused by more than one object kind.",
                identity,
            )
        seen[identity] = kind

    existing = _existing_object_ids(model)
    existing_layers = {item.layer_id for item in model.layers}
    for identity, kind in proposed:
        if kind == "layer" and identity in existing_layers:
            continue
        if identity in existing:
            _add(
                findings,
                "identity.source_collision",
                "A proposed identity already exists in the source model.",
                identity,
            )

    known_issue_targets = set(seen) | {item.layer_id for item in proposal.layers}
    for issue in proposal.input_issues:
        for identity in issue.object_ids:
            if identity not in known_issue_targets:
                _add(
                    findings,
                    "identity.issue_target_unknown",
                    "An input issue references an unknown proposed object.",
                    issue.issue_id,
                    identity,
                )


def _existing_object_ids(model: ElectricalProjectModel) -> set[str]:
    values = {
        *(item.drawing_id for item in model.drawings),
        *(item.layer_id for item in model.layers),
        *(item.device_id for item in model.devices),
        *(item.panel_id for item in model.panels),
        *(item.circuit_id for item in model.circuits),
        *(item.protection_id for item in model.protection_devices),
        *(item.cable_id for item in model.cables),
        *(item.conductor_id for item in model.conductors),
        *(item.load_id for item in model.loads),
        *(item.route_id for item in model.routes),
        *(item.unknown_id for item in model.unknown_objects),
    }
    for device in model.devices:
        values.update(item.terminal_id for item in device.terminals)
    return values


def _validate_input_issues(
    proposal: DraftingProposal, findings: list[DraftingValidationFinding]
) -> None:
    for issue in proposal.input_issues:
        _add(
            findings,
            f"input.{issue.kind.value}",
            "The proposal contains an unresolved input and cannot be previewed.",
            issue.issue_id,
            *issue.object_ids,
        )


def _validate_layers(
    proposal: DraftingProposal,
    model: ElectricalProjectModel,
    catalog: ElectricalSymbolCatalog | None,
    findings: list[DraftingValidationFinding],
) -> None:
    schemas = {item.layer_id: item for item in proposal.layers}
    existing_by_id = {item.layer_id: item for item in model.layers}
    existing_by_name = {item.name.casefold(): item for item in model.layers}
    used = {item.layer_id for item in proposal.devices} | {
        item.layer_id for item in proposal.routes
    }
    for layer_id in sorted(used - set(schemas)):
        _add(
            findings,
            "layer.schema_missing",
            "A used proposal layer has no exact proposed schema.",
            layer_id,
        )
    for layer_id in sorted(set(schemas) - used):
        _add(
            findings,
            "layer.schema_unused",
            "A proposed layer schema is not used by any drafting object.",
            layer_id,
        )
    for schema in proposal.layers:
        existing = existing_by_id.get(schema.layer_id)
        same_name = existing_by_name.get(schema.name.casefold())
        if existing is None:
            if same_name is not None:
                _add(
                    findings,
                    "layer.identity_conflict",
                    "A source layer has the proposed name under a different identity.",
                    schema.layer_id,
                )
            if not schema.create_if_missing:
                _add(
                    findings,
                    "layer.missing",
                    "A missing layer was not explicitly marked for creation.",
                    schema.layer_id,
                )
            continue
        if schema.create_if_missing:
            _add(
                findings,
                "layer.unexpected_create",
                "An existing source layer cannot be marked for creation.",
                schema.layer_id,
            )
        if (
            schema.name != existing.name
            or schema.color_index != existing.color_index
            or schema.linetype.casefold() != existing.linetype.casefold()
        ):
            _add(
                findings,
                "layer.schema_mismatch",
                "The proposed layer schema differs from the exact source layer.",
                schema.layer_id,
            )
        if existing.is_off or existing.is_frozen or existing.is_locked:
            _add(
                findings,
                "layer.state_not_writable",
                "The exact source layer is off, frozen, or locked.",
                schema.layer_id,
            )

    if catalog is None:
        return
    symbols = {item.symbol_id: item for item in catalog.symbols}
    for device in proposal.devices:
        symbol = symbols.get(device.symbol_id)
        schema = schemas.get(device.layer_id)
        if symbol is None or schema is None:
            continue
        if (
            schema.name != symbol.insertion_layer
            or schema.color_index != symbol.layer_color
            or schema.linetype.casefold() != symbol.linetype.casefold()
        ):
            _add(
                findings,
                "layer.catalog_schema_mismatch",
                "The device layer schema differs from its reviewed symbol definition.",
                device.device_id,
                schema.layer_id,
            )


def _validate_devices(
    proposal: DraftingProposal,
    model: ElectricalProjectModel,
    catalog: ElectricalSymbolCatalog | None,
    findings: list[DraftingValidationFinding],
) -> None:
    if catalog is None:
        return
    symbols = {item.symbol_id: item for item in catalog.symbols}
    existing_blocks = {item.block_name.casefold(): item for item in model.devices}
    for device in proposal.devices:
        symbol = symbols.get(device.symbol_id)
        if symbol is None:
            _add(
                findings,
                "catalog.symbol_unknown",
                "A proposed device symbol is not in the reviewed catalog.",
                device.device_id,
            )
            continue
        _validate_device_symbol(device, symbol, proposal.binding.unit_system, findings)
        existing = existing_blocks.get(device.block_name.casefold())
        if existing is not None and existing.symbol_id != device.symbol_id:
            _add(
                findings,
                "catalog.block_conflict",
                "The block name already represents a different source-model symbol.",
                device.device_id,
                existing.device_id,
            )


def _validate_device_symbol(
    device: Any,
    symbol: ElectricalSymbol,
    unit_system: UnitSystem,
    findings: list[DraftingValidationFinding],
) -> None:
    for actual, expected, code in (
        (device.block_name, symbol.block_name, "catalog.block_mismatch"),
        (
            device.symbol_definition_sha256,
            symbol.definition_sha256,
            "catalog.definition_hash_mismatch",
        ),
    ):
        if actual != expected:
            _add(
                findings,
                code,
                "A proposed device differs from its reviewed catalog symbol.",
                device.device_id,
            )
    if _CATALOG_UNITS.get(symbol.units) is not unit_system:
        _add(
            findings,
            "unit.catalog_mismatch",
            "The symbol units do not exactly match the drawing unit system.",
            device.device_id,
        )
    proposed_tags = tuple(key for key, _value in device.attributes)
    if set(proposed_tags) != set(symbol.attribute_tags):
        _add(
            findings,
            "catalog.attribute_schema_mismatch",
            "Device attributes do not exactly match the reviewed symbol tags.",
            device.device_id,
        )


def _validate_geometry(
    proposal: DraftingProposal, findings: list[DraftingValidationFinding]
) -> None:
    millimeters_per_unit = _MILLIMETERS_PER_UNIT.get(proposal.binding.unit_system)
    if millimeters_per_unit is None:
        return
    coordinate_limit = _MAX_PHYSICAL_MILLIMETERS / millimeters_per_unit
    for device in proposal.devices:
        if not _point_in_bounds(device.insertion_point, coordinate_limit):
            _add(
                findings,
                "geometry.device_out_of_bounds",
                "A device insertion point exceeds the physical geometry bound.",
                device.device_id,
            )
        if not 0.0 <= float(device.rotation_degrees) < 360.0:
            _add(
                findings,
                "geometry.rotation_not_normalized",
                "Device rotation must be normalized to [0, 360) degrees.",
                device.device_id,
            )
        if any(not 0.001 <= float(value) <= 1000.0 for value in device.scale):
            _add(
                findings,
                "geometry.scale_out_of_bounds",
                "Device scale must be positive and within the supported bound.",
                device.device_id,
            )
    for terminal in proposal.terminals:
        if terminal.position is None:
            _add(
                findings,
                "endpoint.position_missing",
                "Every proposed terminal used for routing requires an explicit position.",
                terminal.terminal_id,
            )
        elif not _point_in_bounds(terminal.position, coordinate_limit):
            _add(
                findings,
                "geometry.terminal_out_of_bounds",
                "A terminal position exceeds the physical geometry bound.",
                terminal.terminal_id,
            )
    for route in proposal.routes:
        if len(route.points) < 2:
            _add(
                findings,
                "geometry.route_too_short",
                "A route requires at least two explicit points.",
                route.route_id,
            )
            continue
        if any(not _point_in_bounds(point, coordinate_limit) for point in route.points):
            _add(
                findings,
                "geometry.route_out_of_bounds",
                "A route point exceeds the physical geometry bound.",
                route.route_id,
            )
        length = 0.0
        for start, end in zip(route.points[:-1], route.points[1:], strict=True):
            segment = math.dist(start, end)
            if segment == 0.0:
                _add(
                    findings,
                    "geometry.zero_length_segment",
                    "A route contains a zero-length segment.",
                    route.route_id,
                )
            length += segment
        if length * millimeters_per_unit > _MAX_PHYSICAL_MILLIMETERS:
            _add(
                findings,
                "geometry.route_length_exceeded",
                "A route exceeds the supported physical length bound.",
                route.route_id,
            )


def _validate_topology(
    proposal: DraftingProposal, findings: list[DraftingValidationFinding]
) -> None:
    devices = {item.device_id for item in proposal.devices}
    terminals = {item.terminal_id: item for item in proposal.terminals}
    routes = {item.route_id: item for item in proposal.routes}
    circuits = {item.circuit_id: item for item in proposal.circuits}
    for terminal in proposal.terminals:
        if terminal.device_id not in devices:
            _add(
                findings,
                "topology.terminal_device_unknown",
                "A terminal references an unknown proposed device.",
                terminal.terminal_id,
                terminal.device_id,
            )
    route_membership: dict[str, list[str]] = {}
    terminal_membership: dict[str, list[str]] = {}
    for circuit in proposal.circuits:
        _validate_circuit(
            circuit,
            terminals,
            routes,
            route_membership,
            terminal_membership,
            proposal.binding.unit_system,
            findings,
        )
    for route in proposal.routes:
        if route.circuit_id not in circuits:
            _add(
                findings,
                "topology.route_circuit_unknown",
                "A route references an unknown proposed circuit.",
                route.route_id,
                route.circuit_id,
            )
        memberships = route_membership.get(route.route_id, [])
        if len(memberships) != 1:
            _add(
                findings,
                "topology.route_membership_invalid",
                "Every route must belong to exactly one circuit route list.",
                route.route_id,
            )
    for terminal_id, memberships in terminal_membership.items():
        if len(memberships) > 1:
            _add(
                findings,
                "topology.terminal_multiple_circuits",
                "A terminal cannot belong to more than one proposed circuit.",
                terminal_id,
            )


def _validate_circuit(
    circuit: ProposedCircuit,
    terminals: dict[str, Any],
    routes: dict[str, ProposedRoute],
    route_membership: dict[str, list[str]],
    terminal_membership: dict[str, list[str]],
    unit_system: UnitSystem,
    findings: list[DraftingValidationFinding],
) -> None:
    if len(circuit.terminal_ids) < 2:
        _add(
            findings,
            "topology.circuit_terminals_insufficient",
            "A circuit requires at least two explicit terminals.",
            circuit.circuit_id,
        )
    if not circuit.route_ids:
        _add(
            findings,
            "topology.circuit_routes_missing",
            "A circuit requires at least one explicit route.",
            circuit.circuit_id,
        )
    terminal_set = set(circuit.terminal_ids)
    for terminal_id in circuit.terminal_ids:
        terminal_membership.setdefault(terminal_id, []).append(circuit.circuit_id)
        if terminal_id not in terminals:
            _add(
                findings,
                "topology.circuit_terminal_unknown",
                "A circuit references an unknown proposed terminal.",
                circuit.circuit_id,
                terminal_id,
            )
    edges: list[tuple[str, str]] = []
    for route_id in circuit.route_ids:
        route_membership.setdefault(route_id, []).append(circuit.circuit_id)
        route = routes.get(route_id)
        if route is None:
            _add(
                findings,
                "topology.circuit_route_unknown",
                "A circuit references an unknown proposed route.",
                circuit.circuit_id,
                route_id,
            )
            continue
        if route.circuit_id != circuit.circuit_id:
            _add(
                findings,
                "topology.route_circuit_mismatch",
                "A route binding differs from the circuit that lists it.",
                circuit.circuit_id,
                route.route_id,
            )
        if route.start_terminal_id == route.end_terminal_id:
            _add(
                findings,
                "endpoint.identical",
                "A route must connect two different terminal identities.",
                route.route_id,
            )
        for terminal_id in (route.start_terminal_id, route.end_terminal_id):
            if terminal_id not in terminal_set:
                _add(
                    findings,
                    "endpoint.outside_circuit",
                    "A route endpoint is not listed by its circuit.",
                    route.route_id,
                    terminal_id,
                )
        _validate_route_endpoint_positions(route, terminals, unit_system, findings)
        edges.append((route.start_terminal_id, route.end_terminal_id))
    _validate_tree_topology(circuit, edges, findings)


def _validate_route_endpoint_positions(
    route: ProposedRoute,
    terminals: dict[str, Any],
    unit_system: UnitSystem,
    findings: list[DraftingValidationFinding],
) -> None:
    if len(route.points) < 2:
        return
    millimeters_per_unit = _MILLIMETERS_PER_UNIT.get(unit_system)
    if millimeters_per_unit is None:
        return
    tolerance = _ENDPOINT_TOLERANCE_MILLIMETERS / millimeters_per_unit
    for terminal_id, point, label in (
        (route.start_terminal_id, route.points[0], "start"),
        (route.end_terminal_id, route.points[-1], "end"),
    ):
        terminal = terminals.get(terminal_id)
        if terminal is None or terminal.position is None:
            continue
        if math.dist(point, terminal.position) > tolerance:
            _add(
                findings,
                f"endpoint.{label}_position_mismatch",
                "A route endpoint point does not match its terminal position.",
                route.route_id,
                terminal_id,
            )


def _validate_tree_topology(
    circuit: ProposedCircuit,
    edges: list[tuple[str, str]],
    findings: list[DraftingValidationFinding],
) -> None:
    nodes = set(circuit.terminal_ids)
    usable = [(start, end) for start, end in edges if start in nodes and end in nodes]
    normalized = [tuple(sorted(edge)) for edge in usable]
    if len(normalized) != len(set(normalized)):
        _add(
            findings,
            "topology.duplicate_edge",
            "A circuit contains duplicate terminal-to-terminal routes.",
            circuit.circuit_id,
        )
    parent = {node: node for node in nodes}

    def root(node: str) -> str:
        while parent[node] != node:
            parent[node] = parent[parent[node]]
            node = parent[node]
        return node

    cycle = False
    for start, end in usable:
        start_root = root(start)
        end_root = root(end)
        if start_root == end_root:
            cycle = True
        else:
            parent[start_root] = end_root
    if cycle:
        _add(
            findings,
            "topology.cycle",
            "A circuit route graph must be acyclic for unambiguous drafting.",
            circuit.circuit_id,
        )
    if nodes and (len({root(node) for node in nodes}) != 1 or len(usable) != len(nodes) - 1):
        _add(
            findings,
            "topology.not_connected_tree",
            "A circuit must form one connected unambiguous terminal tree.",
            circuit.circuit_id,
        )


def _point_in_bounds(point: Point3D, limit: float) -> bool:
    return all(abs(float(value)) <= limit for value in point)


def _add(
    findings: list[DraftingValidationFinding],
    code: str,
    message: str,
    *object_ids: str,
) -> None:
    unique_ids = tuple(dict.fromkeys(object_ids))
    finding = DraftingValidationFinding(code, message, unique_ids)
    if finding not in findings:
        findings.append(finding)


def _create_result(
    proposal: DraftingProposal,
    findings: tuple[DraftingValidationFinding, ...],
) -> DraftingValidationResult:
    values: dict[str, Any] = {
        "schema_version": 1,
        "validator_version": _VALIDATOR_VERSION,
        "proposal_id": proposal.proposal_id,
        "proposal_sha256": proposal.proposal_sha256,
        "model_sha256": proposal.binding.model_sha256,
        "symbol_catalog_sha256": proposal.binding.symbol_catalog_sha256,
        "findings": findings,
        "is_valid": not findings,
        "preview_allowed": not findings,
        "drawing_mutation_performed": False,
    }
    digest = _canonical_sha256(values)
    return DraftingValidationResult(
        1,
        _VALIDATOR_VERSION,
        proposal.proposal_id,
        proposal.proposal_sha256,
        proposal.binding.model_sha256,
        proposal.binding.symbol_catalog_sha256,
        findings,
        not findings,
        not findings,
        False,
        digest,
    )


def _result_sha256(result: DraftingValidationResult) -> str:
    value = result.to_dict()
    del value["validation_sha256"]
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
