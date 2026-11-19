"""Bounded semantic extraction for explicit AI-Engineer panel and SLD markers."""

from __future__ import annotations

import hashlib
import json
import re
from dataclasses import dataclass, replace
from enum import Enum
from typing import Any

from agent.exceptions import AIEngineerError, PanelSldExtractionError
from core.electrical_drafting import canonical_model_sha256
from core.electrical_model import (
    Circuit,
    ElectricalProjectModel,
    JsonRecord,
    Load,
    Panel,
    ProtectionDevice,
    SourceReference,
)
from core.panel_schedule import PanelScheduleFinding, ScheduleFindingOutcome
from core.panel_sld import (
    LoadAssignmentContract,
    OutgoingWayContract,
    PanelContract,
    PanelPhase,
    PanelSldContract,
    ProtectiveDeviceContract,
    SldEdgeContract,
    SldNodeContract,
    SldNodeKind,
    WayKind,
    create_panel_sld_contract,
)
from integrations.autocad_context import AutoCADContext, ContextEntity


EXTRACTION_VERSION = "m21.panel_sld_extraction.v6"
PANEL_BLOCK = "AIENG_M21_PANEL_V1"
WAY_BLOCK = "AIENG_M21_WAY_V1"
NODE_BLOCK = "AIENG_M21_SLD_NODE_V1"
EDGE_BLOCK = "AIENG_M21_SLD_EDGE_V1"
SUPPORTED_BLOCKS = frozenset({PANEL_BLOCK, WAY_BLOCK, NODE_BLOCK, EDGE_BLOCK})
_IDENTIFIER = re.compile(r"[a-z][a-z0-9_.:-]{0,127}")
_SHA256 = re.compile(r"[0-9a-f]{64}")
_DECIMAL = re.compile(r"(?:0|[1-9][0-9]*)(?:\.[0-9]+)?")


@dataclass(frozen=True, slots=True)
class PanelSldExtractionResult(JsonRecord):
    """Self-hashed extraction result; incomplete observations never create semantics."""

    schema_version: int
    extraction_version: str
    project_id: str
    document_id: str
    context_id: str
    context_evidence_sha256: str
    base_model_sha256: str
    supported_marker_count: int
    reviewed_by: str
    assumptions: tuple[str, ...]
    findings: tuple[PanelScheduleFinding, ...]
    complete: bool
    semantic_model: ElectricalProjectModel | None
    contract: PanelSldContract | None
    semantic_model_sha256: str | None
    existing_schedule_extraction_performed: bool
    provider_egress_performed: bool
    drawing_mutation_performed: bool
    result_sha256: str

    def __post_init__(self) -> None:
        if self.schema_version != 1 or self.extraction_version != EXTRACTION_VERSION:
            raise PanelSldExtractionError("Unsupported panel/SLD extraction schema or version.")
        _identifier(self.project_id, "panel/SLD extraction project ID")
        _identifier(self.document_id, "panel/SLD extraction document ID")
        if re.fullmatch(r"[0-9a-f]{32}", self.context_id) is None:
            raise PanelSldExtractionError("Panel/SLD extraction context ID is invalid.")
        for value, name in (
            (self.context_evidence_sha256, "context evidence hash"),
            (self.base_model_sha256, "base-model hash"),
            (self.result_sha256, "result hash"),
        ):
            if _SHA256.fullmatch(value) is None:
                raise PanelSldExtractionError(f"Panel/SLD extraction {name} must be SHA-256.")
        if (
            not isinstance(self.supported_marker_count, int)
            or isinstance(self.supported_marker_count, bool)
            or not 0 <= self.supported_marker_count <= 512
        ):
            raise PanelSldExtractionError("Panel/SLD supported-marker count is invalid.")
        _text(self.reviewed_by, "panel/SLD extraction reviewer", 128)
        if not isinstance(self.assumptions, tuple) or not 1 <= len(self.assumptions) <= 16:
            raise PanelSldExtractionError(
                "Panel/SLD extraction assumptions require 1 through 16 entries."
            )
        for assumption in self.assumptions:
            _text(assumption, "panel/SLD extraction assumption", 512)
        if not isinstance(self.findings, tuple) or any(
            not isinstance(item, PanelScheduleFinding) for item in self.findings
        ):
            raise PanelSldExtractionError("Panel/SLD extraction findings are invalid.")
        if len({item.finding_id for item in self.findings}) != len(self.findings):
            raise PanelSldExtractionError("Panel/SLD extraction finding IDs must be unique.")
        if self.complete is not (not self.findings):
            raise PanelSldExtractionError("Panel/SLD extraction completeness conflicts with findings.")
        if self.complete:
            if not isinstance(self.semantic_model, ElectricalProjectModel) or not isinstance(
                self.contract, PanelSldContract
            ):
                raise PanelSldExtractionError("Complete extraction requires a model and contract.")
            if self.semantic_model_sha256 != canonical_model_sha256(self.semantic_model):
                raise PanelSldExtractionError("Extracted semantic-model hash is invalid.")
            if self.contract.model_sha256 != self.semantic_model_sha256:
                raise PanelSldExtractionError("Extracted contract does not bind the semantic model.")
        elif any(
            value is not None
            for value in (self.semantic_model, self.contract, self.semantic_model_sha256)
        ):
            raise PanelSldExtractionError(
                "Incomplete extraction cannot publish partial semantic model data."
            )
        if self.existing_schedule_extraction_performed is not True:
            raise PanelSldExtractionError("Extraction result must declare observation processing.")
        if self.provider_egress_performed or self.drawing_mutation_performed:
            raise PanelSldExtractionError("Panel/SLD extraction cannot claim egress or mutation.")
        payload = self.to_dict()
        del payload["result_sha256"]
        if self.result_sha256 != _canonical_sha256(payload):
            raise PanelSldExtractionError("Panel/SLD extraction SHA-256 does not match content.")


def extract_panel_sld_observations(
    context: AutoCADContext,
    base_model: ElectricalProjectModel,
    *,
    context_evidence_sha256: str,
    reviewed_by: str,
    assumptions: tuple[str, ...],
) -> PanelSldExtractionResult:
    """Extract only fixed marker blocks from one exact read-only AutoCAD context."""

    if not isinstance(context, AutoCADContext) or not isinstance(
        base_model, ElectricalProjectModel
    ):
        raise PanelSldExtractionError("Panel/SLD extraction requires typed context and model.")
    if (
        context.context_id != base_model.context_id
        or context_evidence_sha256 != base_model.provenance.evidence_sha256
        or "autocad:" + context.document_id.replace("-", "") != base_model.document_id
    ):
        raise PanelSldExtractionError("Panel/SLD extraction evidence does not match the model.")
    if any(
        (base_model.panels, base_model.circuits, base_model.protection_devices, base_model.loads)
    ):
        raise PanelSldExtractionError(
            "Panel/SLD extraction requires an unclassified base semantic model."
        )
    all_markers = tuple(
        item
        for item in context.entities
        if item.entity_type == "INSERT"
        and item.block_name is not None
        and item.block_name.upper() in SUPPORTED_BLOCKS
    )
    findings: list[PanelScheduleFinding] = []
    markers = all_markers[:512]
    if len(all_markers) > len(markers):
        findings.append(
            PanelScheduleFinding(
                "finding:0001",
                ScheduleFindingOutcome.UNKNOWN,
                "panel:extraction",
                "Supported marker count exceeds the bounded 512-marker extraction limit.",
            )
        )
    if context.truncated:
        findings.append(
            PanelScheduleFinding(
                f"finding:{len(findings) + 1:04d}",
                ScheduleFindingOutcome.UNKNOWN,
                "panel:extraction",
                "AutoCAD context is truncated, so panel/SLD extraction cannot be complete.",
            )
        )
    panels: list[tuple[PanelContract, Panel]] = []
    ways: list[OutgoingWayContract] = []
    protection_contracts: list[ProtectiveDeviceContract] = []
    protection_model: list[ProtectionDevice] = []
    circuits: list[Circuit] = []
    assignments: list[LoadAssignmentContract] = []
    loads: list[Load] = []
    nodes: list[SldNodeContract] = []
    edges: list[SldEdgeContract] = []
    devices_by_source_object = {
        item.source.object_id.upper(): item.device_id for item in base_model.devices
    }

    for entity in markers:
        try:
            attributes = _attributes(entity)
            block_name = entity.block_name.upper()  # type: ignore[union-attr]
            source = SourceReference(
                "autocad", base_model.document_id, entity.object_id, context.context_id
            )
            if block_name == PANEL_BLOCK:
                _exact_keys(attributes, {"PANEL_ID", "PANEL_NAME"}, entity)
                panel = PanelContract(attributes["PANEL_ID"], attributes["PANEL_NAME"], source)
                panels.append((panel, Panel(panel.panel_id, panel.name, source)))
            elif block_name == WAY_BLOCK:
                _parse_way(
                    attributes,
                    entity,
                    source,
                    ways,
                    protection_contracts,
                    protection_model,
                    circuits,
                    assignments,
                    loads,
                    devices_by_source_object,
                )
            elif block_name == NODE_BLOCK:
                _exact_keys(
                    attributes, {"NODE_ID", "NODE_KIND", "REF_ID", "CIRCUIT_ID"}, entity
                )
                circuit_id = attributes["CIRCUIT_ID"] or None
                nodes.append(
                    SldNodeContract(
                        attributes["NODE_ID"],
                        SldNodeKind(attributes["NODE_KIND"]),
                        attributes["REF_ID"],
                        circuit_id,
                        source,
                    )
                )
            else:
                _exact_keys(
                    attributes,
                    {"EDGE_ID", "FROM_NODE_ID", "TO_NODE_ID", "CIRCUIT_ID"},
                    entity,
                )
                edges.append(
                    SldEdgeContract(
                        attributes["EDGE_ID"],
                        attributes["FROM_NODE_ID"],
                        attributes["TO_NODE_ID"],
                        attributes["CIRCUIT_ID"],
                        source,
                    )
                )
        except (KeyError, ValueError, AIEngineerError) as exc:
            # Domain constructors intentionally report bounded messages; never retain input values.
            findings.append(
                _finding(
                    len(findings) + 1,
                    ScheduleFindingOutcome.UNKNOWN,
                    entity,
                    f"Supported {entity.block_name} marker is incomplete or invalid ({type(exc).__name__}).",
                )
            )

    if len(panels) != 1:
        findings.append(
            PanelScheduleFinding(
                f"finding:{len(findings) + 1:04d}",
                ScheduleFindingOutcome.CONFLICT if len(panels) > 1 else ScheduleFindingOutcome.UNKNOWN,
                "panel:extraction",
                "Extraction requires exactly one supported panel marker.",
            )
        )
    for collection, label in ((ways, "outgoing-way"), (nodes, "SLD-node"), (edges, "SLD-edge")):
        if not collection:
            findings.append(
                PanelScheduleFinding(
                    f"finding:{len(findings) + 1:04d}",
                    ScheduleFindingOutcome.UNKNOWN,
                    "panel:extraction",
                    f"Extraction found no valid supported {label} markers.",
                )
            )

    semantic_model: ElectricalProjectModel | None = None
    contract: PanelSldContract | None = None
    semantic_hash: str | None = None
    if not findings:
        try:
            semantic_model = replace(
                base_model,
                panels=(panels[0][1],),
                circuits=tuple(circuits),
                protection_devices=tuple(protection_model),
                loads=tuple(loads),
            )
            contract = create_panel_sld_contract(
                semantic_model,
                panels[0][0],
                tuple(ways),
                tuple(protection_contracts),
                tuple(assignments),
                tuple(nodes),
                tuple(edges),
                context_evidence_sha256=context_evidence_sha256,
                reviewed_by=reviewed_by,
                assumptions=assumptions,
            )
            semantic_hash = canonical_model_sha256(semantic_model)
        except AIEngineerError as exc:
            findings.append(
                PanelScheduleFinding(
                    "finding:0001",
                    ScheduleFindingOutcome.CONFLICT,
                    "panel:extraction",
                    f"Supported observations conflict and cannot form one semantic topology ({type(exc).__name__}).",
                )
            )
            semantic_model = None
            contract = None
            semantic_hash = None

    payload: dict[str, Any] = {
        "schema_version": 1,
        "extraction_version": EXTRACTION_VERSION,
        "project_id": base_model.project_id,
        "document_id": base_model.document_id,
        "context_id": context.context_id,
        "context_evidence_sha256": context_evidence_sha256,
        "base_model_sha256": canonical_model_sha256(base_model),
        "supported_marker_count": len(markers),
        "reviewed_by": reviewed_by,
        "assumptions": assumptions,
        "findings": tuple(findings),
        "complete": not findings,
        "semantic_model": semantic_model,
        "contract": contract,
        "semantic_model_sha256": semantic_hash,
        "existing_schedule_extraction_performed": True,
        "provider_egress_performed": False,
        "drawing_mutation_performed": False,
    }
    return PanelSldExtractionResult(**payload, result_sha256=_canonical_sha256(payload))


def _parse_way(
    attributes: dict[str, str],
    entity: ContextEntity,
    source: SourceReference,
    ways: list[OutgoingWayContract],
    protection_contracts: list[ProtectiveDeviceContract],
    protection_model: list[ProtectionDevice],
    circuits: list[Circuit],
    assignments: list[LoadAssignmentContract],
    loads: list[Load],
    devices_by_source_object: dict[str, str],
) -> None:
    common_keys = {
        "WAY_ID", "PANEL_ID", "CIRCUIT_ID", "CIRCUIT_NAME", "WAY_NUMBER",
        "WAY_KIND", "PHASE", "POLE_COUNT", "PROTECTION_ID", "PROTECTION_KIND",
        "PROTECTION_DESIGNATION", "RATED_CURRENT_A", "BREAKING_CAPACITY_A",
    }
    way_kind = WayKind(attributes.get("WAY_KIND", ""))
    keys = common_keys | ({"LOAD_BINDINGS", "LOAD_NAMES"} if way_kind is WayKind.LOAD else set())
    _exact_keys(attributes, keys, entity)
    bindings = _list(attributes.get("LOAD_BINDINGS", ""), "LOAD_BINDINGS")
    load_ids, source_object_ids = _load_bindings(bindings)
    load_names = _list(attributes.get("LOAD_NAMES", ""), "LOAD_NAMES", unique=False)
    if not len(load_ids) == len(source_object_ids) == len(load_names):
        raise PanelSldExtractionError("Way load columns have conflicting lengths.")
    device_ids = tuple(
        _device_id_for_source_object(item, devices_by_source_object)
        for item in source_object_ids
    )
    phase = PanelPhase(attributes["PHASE"])
    way = OutgoingWayContract(
        attributes["WAY_ID"], attributes["PANEL_ID"], attributes["CIRCUIT_ID"],
        _integer(attributes["WAY_NUMBER"], "WAY_NUMBER"), way_kind,
        phase, _integer(attributes["POLE_COUNT"], "POLE_COUNT"), attributes["PROTECTION_ID"],
    )
    protection = ProtectiveDeviceContract(
        attributes["PROTECTION_ID"], attributes["CIRCUIT_ID"],
        attributes["PROTECTION_KIND"], attributes["PROTECTION_DESIGNATION"],
        _number(attributes["RATED_CURRENT_A"], "RATED_CURRENT_A"),
        _number(attributes["BREAKING_CAPACITY_A"], "BREAKING_CAPACITY_A"), source,
    )
    ways.append(way)
    protection_contracts.append(protection)
    protection_model.append(ProtectionDevice(protection.protection_id, protection.circuit_id,
                                               protection.kind, source))
    circuits.append(Circuit(way.circuit_id, way.panel_id, attributes["CIRCUIT_NAME"], tuple(device_ids)))
    for index, (load_id, device_id, load_name) in enumerate(zip(load_ids, device_ids, load_names, strict=True), 1):
        loads.append(Load(load_id, device_id, load_name, source))
        assignments.append(LoadAssignmentContract(
            f"assignment:{way.way_id.split(':')[-1]}-{index}", way.circuit_id, load_id, phase, source
        ))


def _attributes(entity: ContextEntity) -> dict[str, str]:
    return {item.tag.upper(): item.value.strip() for item in entity.attributes}


def _exact_keys(values: dict[str, str], expected: set[str], entity: ContextEntity) -> None:
    if set(values) != expected:
        raise PanelSldExtractionError(f"{entity.block_name} attribute schema is incomplete.")


def _list(value: str, name: str, *, unique: bool = True) -> tuple[str, ...]:
    if not value:
        return ()
    items = tuple(item.strip() for item in value.split("|") if item.strip())
    if not items or len(items) > 256 or (unique and len(items) != len(set(items))):
        raise PanelSldExtractionError(f"{name} list is invalid or contains duplicates.")
    return items


def _device_id_for_source_object(
    value: str, devices_by_source_object: dict[str, str]
) -> str:
    """Resolve one stable AutoCAD handle without depending on selection ordering."""
    normalized = value.upper()
    if re.fullmatch(r"[0-9A-F]{1,32}", normalized) is None:
        raise PanelSldExtractionError(
            "LOAD_BINDINGS contains an invalid AutoCAD object handle."
        )
    try:
        return devices_by_source_object[normalized]
    except KeyError as exc:
        raise PanelSldExtractionError(
            "LOAD_BINDINGS references no recognized device in the exact context."
        ) from exc


def _load_bindings(bindings: tuple[str, ...]) -> tuple[tuple[str, ...], tuple[str, ...]]:
    """Split atomic ``load-id@AutoCAD-handle`` values without inferring identity."""
    load_ids: list[str] = []
    handles: list[str] = []
    for binding in bindings:
        if binding.count("@") != 1:
            raise PanelSldExtractionError(
                "LOAD_BINDINGS must contain canonical load-id@handle values."
            )
        load_id, handle = binding.split("@", 1)
        if not load_id or not handle:
            raise PanelSldExtractionError(
                "LOAD_BINDINGS must contain canonical load-id@handle values."
            )
        load_ids.append(load_id)
        handles.append(handle)
    if len(load_ids) != len(set(load_ids)) or len(handles) != len(set(handles)):
        raise PanelSldExtractionError("LOAD_BINDINGS contains duplicate identities.")
    return tuple(load_ids), tuple(handles)


def _integer(value: str, name: str) -> int:
    if not value.isascii() or not value.isdigit():
        raise PanelSldExtractionError(f"{name} must be a canonical integer.")
    return int(value)


def _number(value: str, name: str) -> float:
    if _DECIMAL.fullmatch(value) is None:
        raise PanelSldExtractionError(f"{name} must be a canonical positive decimal.")
    return float(value)


def _finding(number: int, outcome: ScheduleFindingOutcome, entity: ContextEntity, message: str) -> PanelScheduleFinding:
    return PanelScheduleFinding(
        f"finding:{number:04d}", outcome, f"autocad_object:{entity.object_id.lower()}", message
    )


def _canonical_sha256(value: object) -> str:
    def convert(item: object) -> object:
        if isinstance(item, Enum): return item.value
        if isinstance(item, JsonRecord): return item.to_dict()
        if isinstance(item, tuple): return [convert(child) for child in item]
        if isinstance(item, dict): return {str(key): convert(child) for key, child in item.items()}
        return item
    return hashlib.sha256(json.dumps(convert(value), ensure_ascii=False, sort_keys=True,
        separators=(",", ":")).encode("utf-8")).hexdigest()


def _identifier(value: str, name: str) -> None:
    if not isinstance(value, str) or _IDENTIFIER.fullmatch(value) is None:
        raise PanelSldExtractionError(f"{name} must be a bounded lowercase identifier.")


def _text(value: str, name: str, maximum: int) -> None:
    if not isinstance(value, str) or not value or len(value.encode("utf-8")) > maximum:
        raise PanelSldExtractionError(f"{name} must be non-empty and at most {maximum} bytes.")
