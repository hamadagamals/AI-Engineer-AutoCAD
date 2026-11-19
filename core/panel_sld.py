"""Exact-evidence-bound contracts for linked panel schedules and SLD topology."""

from __future__ import annotations

import hashlib
import json
import math
import re
from dataclasses import dataclass
from enum import Enum
from typing import Any

from agent.exceptions import PanelSldContractError
from core.electrical_drafting import canonical_model_sha256
from core.electrical_model import ElectricalProjectModel, JsonRecord, SourceReference


CONTRACT_VERSION = "m21.panel_sld_contract.v1"
_IDENTIFIER = re.compile(r"[a-z][a-z0-9_.:-]{0,127}")
_SHA256 = re.compile(r"[0-9a-f]{64}")


class WayKind(str, Enum):
    """Explicit outgoing-way purpose; spare ways never silently imply a load."""

    LOAD = "load"
    SPARE = "spare"


class PanelPhase(str, Enum):
    """Reviewed phase assignment used consistently by a way and its loads."""

    L1 = "l1"
    L2 = "l2"
    L3 = "l3"
    THREE_PHASE = "three_phase"


class SldNodeKind(str, Enum):
    """Supported semantic nodes in the initial bounded SLD topology."""

    PANEL = "panel"
    PROTECTIVE_DEVICE = "protective_device"
    LOAD = "load"


@dataclass(frozen=True, slots=True)
class PanelContract(JsonRecord):
    """One panel identity copied exactly from the shared model."""

    panel_id: str
    name: str
    source: SourceReference | None

    def __post_init__(self) -> None:
        _identifier(self.panel_id, "panel ID")
        _text(self.name, "panel name", 128)
        _optional_source(self.source, "panel source")


@dataclass(frozen=True, slots=True)
class OutgoingWayContract(JsonRecord):
    """One ordered panel way bound to exactly one stable circuit identity."""

    way_id: str
    panel_id: str
    circuit_id: str
    way_number: int
    kind: WayKind
    phase: PanelPhase
    pole_count: int
    protective_device_id: str

    def __post_init__(self) -> None:
        for value, name in (
            (self.way_id, "outgoing-way ID"),
            (self.panel_id, "outgoing-way panel ID"),
            (self.circuit_id, "outgoing-way circuit ID"),
            (self.protective_device_id, "outgoing-way protective-device ID"),
        ):
            _identifier(value, name)
        if not isinstance(self.way_number, int) or isinstance(self.way_number, bool):
            raise PanelSldContractError("Outgoing-way number must be an integer.")
        if not 1 <= self.way_number <= 512:
            raise PanelSldContractError("Outgoing-way number must be between 1 and 512.")
        if not isinstance(self.kind, WayKind):
            raise PanelSldContractError("Outgoing-way kind is invalid.")
        if not isinstance(self.phase, PanelPhase):
            raise PanelSldContractError("Outgoing-way phase is invalid.")
        if (
            not isinstance(self.pole_count, int)
            or isinstance(self.pole_count, bool)
            or not 1 <= self.pole_count <= 4
        ):
            raise PanelSldContractError("Outgoing-way pole count must be between 1 and 4.")
        if self.phase is PanelPhase.THREE_PHASE and self.pole_count < 3:
            raise PanelSldContractError("A three-phase way requires at least three poles.")
        if self.phase is not PanelPhase.THREE_PHASE and self.pole_count > 2:
            raise PanelSldContractError("A single-phase way cannot use more than two poles.")


@dataclass(frozen=True, slots=True)
class ProtectiveDeviceContract(JsonRecord):
    """Reviewed protective-device data linked to one exact model device and circuit."""

    protection_id: str
    circuit_id: str
    kind: str
    designation: str
    rated_current_amperes: float
    breaking_capacity_amperes: float
    source: SourceReference | None

    def __post_init__(self) -> None:
        _identifier(self.protection_id, "protective-device ID")
        _identifier(self.circuit_id, "protective-device circuit ID")
        _text(self.kind, "protective-device kind", 64)
        _text(self.designation, "protective-device designation", 256)
        _number(self.rated_current_amperes, "protective-device rated current", 0.000001)
        _number(
            self.breaking_capacity_amperes,
            "protective-device breaking capacity",
            0.000001,
        )
        _optional_source(self.source, "protective-device source")


@dataclass(frozen=True, slots=True)
class LoadAssignmentContract(JsonRecord):
    """One model load assigned once to one circuit and an explicit phase."""

    assignment_id: str
    circuit_id: str
    load_id: str
    phase: PanelPhase
    source: SourceReference | None

    def __post_init__(self) -> None:
        _identifier(self.assignment_id, "load-assignment ID")
        _identifier(self.circuit_id, "load-assignment circuit ID")
        _identifier(self.load_id, "load-assignment load ID")
        if not isinstance(self.phase, PanelPhase):
            raise PanelSldContractError("Load-assignment phase is invalid.")
        _optional_source(self.source, "load-assignment source")


@dataclass(frozen=True, slots=True)
class SldNodeContract(JsonRecord):
    """One typed SLD node bound to a panel, protective device, or load."""

    node_id: str
    kind: SldNodeKind
    referenced_object_id: str
    circuit_id: str | None
    source: SourceReference | None = None

    def __post_init__(self) -> None:
        _identifier(self.node_id, "SLD node ID")
        if not isinstance(self.kind, SldNodeKind):
            raise PanelSldContractError("SLD node kind is invalid.")
        _identifier(self.referenced_object_id, "SLD referenced-object ID")
        if self.kind is SldNodeKind.PANEL:
            if self.circuit_id is not None:
                raise PanelSldContractError("The panel SLD node cannot claim one circuit.")
        elif self.circuit_id is None:
            raise PanelSldContractError("Non-panel SLD nodes require an exact circuit ID.")
        else:
            _identifier(self.circuit_id, "SLD node circuit ID")
        _optional_source(self.source, "SLD node source")


@dataclass(frozen=True, slots=True)
class SldEdgeContract(JsonRecord):
    """One directed SLD relationship retaining the exact circuit identity."""

    edge_id: str
    from_node_id: str
    to_node_id: str
    circuit_id: str
    source: SourceReference | None = None

    def __post_init__(self) -> None:
        for value, name in (
            (self.edge_id, "SLD edge ID"),
            (self.from_node_id, "SLD edge source-node ID"),
            (self.to_node_id, "SLD edge target-node ID"),
            (self.circuit_id, "SLD edge circuit ID"),
        ):
            _identifier(value, name)
        if self.from_node_id == self.to_node_id:
            raise PanelSldContractError("An SLD edge cannot connect a node to itself.")
        _optional_source(self.source, "SLD edge source")


@dataclass(frozen=True, slots=True)
class PanelSldContract(JsonRecord):
    """Self-hashed read-only contract joining one panel schedule to one SLD."""

    schema_version: int
    contract_version: str
    project_id: str
    document_id: str
    context_id: str
    context_evidence_sha256: str
    model_sha256: str
    panel: PanelContract
    outgoing_ways: tuple[OutgoingWayContract, ...]
    protective_devices: tuple[ProtectiveDeviceContract, ...]
    load_assignments: tuple[LoadAssignmentContract, ...]
    sld_nodes: tuple[SldNodeContract, ...]
    sld_edges: tuple[SldEdgeContract, ...]
    reviewed_by: str
    assumptions: tuple[str, ...]
    schedule_generation_performed: bool
    drawing_mutation_performed: bool
    contract_sha256: str

    def __post_init__(self) -> None:
        if self.schema_version != 1 or self.contract_version != CONTRACT_VERSION:
            raise PanelSldContractError("Unsupported panel/SLD contract schema or version.")
        _identifier(self.project_id, "panel/SLD project ID")
        _identifier(self.document_id, "panel/SLD document ID")
        if re.fullmatch(r"[0-9a-f]{32}", self.context_id) is None:
            raise PanelSldContractError("Panel/SLD context ID is invalid.")
        for value, name in (
            (self.context_evidence_sha256, "context evidence hash"),
            (self.model_sha256, "model hash"),
            (self.contract_sha256, "contract hash"),
        ):
            if _SHA256.fullmatch(value) is None:
                raise PanelSldContractError(f"Panel/SLD {name} must be SHA-256.")
        if not isinstance(self.panel, PanelContract):
            raise PanelSldContractError("Panel/SLD panel record is invalid.")
        _typed_records(self.outgoing_ways, OutgoingWayContract, "outgoing ways", 1, 512)
        _typed_records(
            self.protective_devices,
            ProtectiveDeviceContract,
            "protective devices",
            1,
            512,
        )
        _typed_records(
            self.load_assignments,
            LoadAssignmentContract,
            "load assignments",
            0,
            2048,
        )
        _typed_records(self.sld_nodes, SldNodeContract, "SLD nodes", 2, 4096)
        _typed_records(self.sld_edges, SldEdgeContract, "SLD edges", 1, 8192)
        _unique(tuple(item.way_id for item in self.outgoing_ways), "Outgoing-way IDs")
        _unique(tuple(item.way_number for item in self.outgoing_ways), "Outgoing-way numbers")
        _unique(tuple(item.circuit_id for item in self.outgoing_ways), "Outgoing-way circuit IDs")
        _unique(
            tuple(item.protective_device_id for item in self.outgoing_ways),
            "Outgoing-way protective-device IDs",
        )
        _unique(
            tuple(item.protection_id for item in self.protective_devices),
            "Protective-device IDs",
        )
        _unique(
            tuple(item.assignment_id for item in self.load_assignments),
            "Load-assignment IDs",
        )
        _unique(tuple(item.load_id for item in self.load_assignments), "Assigned load IDs")
        _unique(tuple(item.node_id for item in self.sld_nodes), "SLD node IDs")
        _unique(tuple(item.edge_id for item in self.sld_edges), "SLD edge IDs")
        _unique(
            tuple((item.from_node_id, item.to_node_id, item.circuit_id) for item in self.sld_edges),
            "SLD relationships",
        )
        _text(self.reviewed_by, "panel/SLD reviewer", 128)
        if not isinstance(self.assumptions, tuple) or not 1 <= len(self.assumptions) <= 16:
            raise PanelSldContractError("Panel/SLD assumptions require 1 through 16 entries.")
        for assumption in self.assumptions:
            _text(assumption, "panel/SLD assumption", 512)
        if self.schedule_generation_performed or self.drawing_mutation_performed:
            raise PanelSldContractError(
                "The contract layer cannot claim schedule generation or drawing mutation."
            )
        payload = self.to_dict()
        del payload["contract_sha256"]
        if self.contract_sha256 != _canonical_sha256(payload):
            raise PanelSldContractError("Panel/SLD contract SHA-256 does not match its content.")


def create_panel_sld_contract(
    model: ElectricalProjectModel,
    panel: PanelContract,
    outgoing_ways: tuple[OutgoingWayContract, ...],
    protective_devices: tuple[ProtectiveDeviceContract, ...],
    load_assignments: tuple[LoadAssignmentContract, ...],
    sld_nodes: tuple[SldNodeContract, ...],
    sld_edges: tuple[SldEdgeContract, ...],
    *,
    context_evidence_sha256: str,
    reviewed_by: str,
    assumptions: tuple[str, ...],
) -> PanelSldContract:
    """Create and validate a read-only panel/SLD identity contract."""

    if not isinstance(model, ElectricalProjectModel):
        raise PanelSldContractError("Panel/SLD creation requires a typed electrical model.")
    if context_evidence_sha256 != model.provenance.evidence_sha256:
        raise PanelSldContractError("Panel/SLD context evidence does not match the model.")
    payload: dict[str, Any] = {
        "schema_version": 1,
        "contract_version": CONTRACT_VERSION,
        "project_id": model.project_id,
        "document_id": model.document_id,
        "context_id": model.context_id,
        "context_evidence_sha256": context_evidence_sha256,
        "model_sha256": canonical_model_sha256(model),
        "panel": panel,
        "outgoing_ways": outgoing_ways,
        "protective_devices": protective_devices,
        "load_assignments": load_assignments,
        "sld_nodes": sld_nodes,
        "sld_edges": sld_edges,
        "reviewed_by": reviewed_by,
        "assumptions": assumptions,
        "schedule_generation_performed": False,
        "drawing_mutation_performed": False,
    }
    contract = PanelSldContract(**payload, contract_sha256=_canonical_sha256(payload))
    validate_panel_sld_contract(model, contract)
    return contract


def validate_panel_sld_contract(
    model: ElectricalProjectModel, contract: PanelSldContract
) -> None:
    """Revalidate exact model bindings and lossless schedule/SLD circuit identity."""

    if not isinstance(model, ElectricalProjectModel) or not isinstance(
        contract, PanelSldContract
    ):
        raise PanelSldContractError("Panel/SLD validation requires typed inputs.")
    if (
        contract.project_id != model.project_id
        or contract.document_id != model.document_id
        or contract.context_id != model.context_id
        or contract.context_evidence_sha256 != model.provenance.evidence_sha256
        or contract.model_sha256 != canonical_model_sha256(model)
    ):
        raise PanelSldContractError("Panel/SLD contract does not match the exact model context.")
    sources = (
        (contract.panel.source,)
        + tuple(item.source for item in contract.protective_devices)
        + tuple(item.source for item in contract.load_assignments)
        + tuple(item.source for item in contract.sld_nodes)
        + tuple(item.source for item in contract.sld_edges)
    )
    if any(
        source is not None
        and (
            source.application != "autocad"
            or source.document_id != model.document_id
            or source.context_id != model.context_id
        )
        for source in sources
    ):
        raise PanelSldContractError(
            "A panel/SLD source reference conflicts with the exact AutoCAD context."
        )

    model_panels = {item.panel_id: item for item in model.panels}
    model_panel = model_panels.get(contract.panel.panel_id)
    if model_panel is None:
        raise PanelSldContractError("Panel/SLD panel is missing from the exact model.")
    if model_panel.name != contract.panel.name or model_panel.source != contract.panel.source:
        raise PanelSldContractError("Panel/SLD panel identity conflicts with the exact model.")

    ways_by_circuit = {item.circuit_id: item for item in contract.outgoing_ways}
    panel_circuits = {
        item.circuit_id: item for item in model.circuits if item.panel_id == contract.panel.panel_id
    }
    if not panel_circuits:
        raise PanelSldContractError("The selected panel has no explicit model circuits.")
    if set(ways_by_circuit) != set(panel_circuits):
        raise PanelSldContractError(
            "Outgoing ways must cover every selected-panel circuit exactly once."
        )
    if any(item.panel_id != contract.panel.panel_id for item in contract.outgoing_ways):
        raise PanelSldContractError("An outgoing way references a conflicting panel identity.")

    model_protection = {item.protection_id: item for item in model.protection_devices}
    contract_protection = {item.protection_id: item for item in contract.protective_devices}
    expected_protection_ids = {item.protective_device_id for item in contract.outgoing_ways}
    if set(contract_protection) != expected_protection_ids:
        raise PanelSldContractError(
            "Protective-device contracts must cover every outgoing way exactly once."
        )
    for protection in contract.protective_devices:
        model_item = model_protection.get(protection.protection_id)
        if model_item is None:
            raise PanelSldContractError("A protective device is missing from the exact model.")
        if (
            model_item.circuit_id != protection.circuit_id
            or model_item.kind != protection.kind
            or model_item.source != protection.source
            or ways_by_circuit.get(protection.circuit_id) is None
            or ways_by_circuit[protection.circuit_id].protective_device_id
            != protection.protection_id
        ):
            raise PanelSldContractError(
                "Protective-device circuit identity conflicts with the model or outgoing way."
            )

    device_circuits: dict[str, str] = {}
    for circuit in panel_circuits.values():
        for device_id in circuit.device_ids:
            previous = device_circuits.setdefault(device_id, circuit.circuit_id)
            if previous != circuit.circuit_id:
                raise PanelSldContractError(
                    "A model device belongs to multiple selected-panel circuits."
                )
    relevant_loads = {
        item.load_id: item
        for item in model.loads
        if item.device_id is not None and item.device_id in device_circuits
    }
    assignments = {item.load_id: item for item in contract.load_assignments}
    if set(assignments) != set(relevant_loads):
        raise PanelSldContractError(
            "Load assignments must cover every source-linked panel load exactly once."
        )
    assignments_by_circuit: dict[str, list[LoadAssignmentContract]] = {
        circuit_id: [] for circuit_id in panel_circuits
    }
    for assignment in contract.load_assignments:
        load = relevant_loads[assignment.load_id]
        expected_circuit = device_circuits[load.device_id]  # type: ignore[index]
        way = ways_by_circuit.get(assignment.circuit_id)
        if (
            assignment.circuit_id != expected_circuit
            or assignment.source != load.source
            or way is None
            or assignment.phase is not way.phase
        ):
            raise PanelSldContractError(
                "A load assignment conflicts with its source load, circuit, or way phase."
            )
        assignments_by_circuit[assignment.circuit_id].append(assignment)
    for circuit_id, way in ways_by_circuit.items():
        count = len(assignments_by_circuit[circuit_id])
        if (way.kind is WayKind.LOAD and count == 0) or (
            way.kind is WayKind.SPARE and count != 0
        ):
            raise PanelSldContractError(
                "Load ways require assignments and spare ways must not contain loads."
            )

    nodes = {item.node_id: item for item in contract.sld_nodes}
    panel_nodes = [
        item
        for item in contract.sld_nodes
        if item.kind is SldNodeKind.PANEL
        and item.referenced_object_id == contract.panel.panel_id
    ]
    if len(panel_nodes) != 1:
        raise PanelSldContractError("SLD topology requires exactly one selected-panel node.")
    panel_node = panel_nodes[0]
    expected_node_bindings = {(SldNodeKind.PANEL, contract.panel.panel_id, None)}
    expected_node_bindings.update(
        (SldNodeKind.PROTECTIVE_DEVICE, item.protection_id, item.circuit_id)
        for item in contract.protective_devices
    )
    expected_node_bindings.update(
        (SldNodeKind.LOAD, item.load_id, item.circuit_id)
        for item in contract.load_assignments
    )
    actual_node_bindings = {
        (item.kind, item.referenced_object_id, item.circuit_id)
        for item in contract.sld_nodes
    }
    if actual_node_bindings != expected_node_bindings or len(actual_node_bindings) != len(
        contract.sld_nodes
    ):
        raise PanelSldContractError(
            "SLD nodes must represent the exact panel, protection, and load identities once."
        )

    protection_nodes = {
        item.referenced_object_id: item
        for item in contract.sld_nodes
        if item.kind is SldNodeKind.PROTECTIVE_DEVICE
    }
    load_nodes = {
        item.referenced_object_id: item
        for item in contract.sld_nodes
        if item.kind is SldNodeKind.LOAD
    }
    expected_edges: set[tuple[str, str, str]] = set()
    for circuit_id, way in ways_by_circuit.items():
        protection_node = protection_nodes[way.protective_device_id]
        expected_edges.add((panel_node.node_id, protection_node.node_id, circuit_id))
        for assignment in assignments_by_circuit[circuit_id]:
            expected_edges.add(
                (protection_node.node_id, load_nodes[assignment.load_id].node_id, circuit_id)
            )
    actual_edges = {
        (item.from_node_id, item.to_node_id, item.circuit_id) for item in contract.sld_edges
    }
    if actual_edges != expected_edges:
        raise PanelSldContractError(
            "SLD edges conflict with the exact panel-to-protection-to-load topology."
        )
    if any(
        edge.from_node_id not in nodes or edge.to_node_id not in nodes
        for edge in contract.sld_edges
    ):
        raise PanelSldContractError("An SLD edge references a missing node.")


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


def _identifier(value: str, name: str) -> None:
    if not isinstance(value, str) or _IDENTIFIER.fullmatch(value) is None:
        raise PanelSldContractError(f"{name} must be a bounded lowercase identifier.")


def _text(value: str, name: str, maximum: int) -> None:
    if not isinstance(value, str) or not value or len(value.encode("utf-8")) > maximum:
        raise PanelSldContractError(f"{name} must be non-empty and at most {maximum} bytes.")
    if any(ord(character) < 32 and character not in "\t" for character in value):
        raise PanelSldContractError(f"{name} contains unsupported control characters.")


def _number(value: float, name: str, minimum: float) -> None:
    if (
        not isinstance(value, (int, float))
        or isinstance(value, bool)
        or not math.isfinite(value)
        or not minimum <= float(value) <= 1_000_000_000.0
    ):
        raise PanelSldContractError(f"{name} is outside its supported numeric range.")


def _optional_source(value: object, name: str) -> None:
    if value is not None and not isinstance(value, SourceReference):
        raise PanelSldContractError(f"{name} is invalid.")


def _typed_records(
    values: object, record_type: type, name: str, minimum: int, maximum: int
) -> None:
    if (
        not isinstance(values, tuple)
        or not minimum <= len(values) <= maximum
        or any(not isinstance(item, record_type) for item in values)
    ):
        raise PanelSldContractError(
            f"Panel/SLD {name} must contain {minimum} through {maximum} typed records."
        )


def _unique(values: tuple[object, ...], name: str) -> None:
    if len(values) != len(set(values)):
        raise PanelSldContractError(f"{name} must be unique.")
