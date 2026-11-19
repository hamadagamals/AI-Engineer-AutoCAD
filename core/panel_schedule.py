"""Deterministic read-only panel-schedule generation from a validated M21 contract."""

from __future__ import annotations

import hashlib
import json
import re
from dataclasses import dataclass
from enum import Enum
from typing import Any

from agent.exceptions import PanelScheduleError, PanelSldContractError
from core.electrical_model import ElectricalProjectModel, JsonRecord, SourceReference
from core.panel_sld import (
    PanelPhase,
    PanelSldContract,
    WayKind,
    validate_panel_sld_contract,
)


SCHEDULE_VERSION = "m21.panel_schedule.v1"
_IDENTIFIER = re.compile(r"[a-z][a-z0-9_.:-]{0,127}")
_SHA256 = re.compile(r"[0-9a-f]{64}")


class ScheduleFindingOutcome(str, Enum):
    """Non-success outcomes reserved for supported existing-schedule extraction."""

    UNKNOWN = "unknown"
    CONFLICT = "conflict"


@dataclass(frozen=True, slots=True)
class PanelScheduleFinding(JsonRecord):
    """One explicit unresolved or conflicting schedule fact."""

    finding_id: str
    outcome: ScheduleFindingOutcome
    subject_id: str
    message: str

    def __post_init__(self) -> None:
        _identifier(self.finding_id, "panel-schedule finding ID")
        if not isinstance(self.outcome, ScheduleFindingOutcome):
            raise PanelScheduleError("Panel-schedule finding outcome is invalid.")
        _identifier(self.subject_id, "panel-schedule finding subject ID")
        _text(self.message, "panel-schedule finding message", 512)


@dataclass(frozen=True, slots=True)
class PanelScheduleRow(JsonRecord):
    """One deterministic outgoing-way row preserving exact circuit identity."""

    way_id: str
    way_number: int
    circuit_id: str
    circuit_name: str
    kind: WayKind
    phase: PanelPhase
    pole_count: int
    protective_device_id: str
    protective_device_kind: str
    protective_device_designation: str
    protective_device_rated_current_amperes: float
    protective_device_breaking_capacity_amperes: float
    load_ids: tuple[str, ...]
    load_names: tuple[str, ...]
    load_sources: tuple[SourceReference | None, ...]

    def __post_init__(self) -> None:
        for value, name in (
            (self.way_id, "panel-schedule way ID"),
            (self.circuit_id, "panel-schedule circuit ID"),
            (self.protective_device_id, "panel-schedule protective-device ID"),
        ):
            _identifier(value, name)
        if (
            not isinstance(self.way_number, int)
            or isinstance(self.way_number, bool)
            or not 1 <= self.way_number <= 512
        ):
            raise PanelScheduleError("Panel-schedule way number is invalid.")
        _text(self.circuit_name, "panel-schedule circuit name", 128)
        if not isinstance(self.kind, WayKind) or not isinstance(self.phase, PanelPhase):
            raise PanelScheduleError("Panel-schedule way kind or phase is invalid.")
        if (
            not isinstance(self.pole_count, int)
            or isinstance(self.pole_count, bool)
            or not 1 <= self.pole_count <= 4
        ):
            raise PanelScheduleError("Panel-schedule pole count is invalid.")
        _text(self.protective_device_kind, "panel-schedule protection kind", 64)
        _text(
            self.protective_device_designation,
            "panel-schedule protection designation",
            256,
        )
        _positive_number(
            self.protective_device_rated_current_amperes,
            "panel-schedule protective-device rated current",
        )
        _positive_number(
            self.protective_device_breaking_capacity_amperes,
            "panel-schedule protective-device breaking capacity",
        )
        if not all(isinstance(values, tuple) for values in (
            self.load_ids,
            self.load_names,
            self.load_sources,
        )):
            raise PanelScheduleError("Panel-schedule load columns must be immutable tuples.")
        if not len(self.load_ids) == len(self.load_names) == len(self.load_sources):
            raise PanelScheduleError("Panel-schedule load columns have conflicting lengths.")
        if len(self.load_ids) != len(set(self.load_ids)):
            raise PanelScheduleError("Panel-schedule row contains duplicate load IDs.")
        for load_id in self.load_ids:
            _identifier(load_id, "panel-schedule load ID")
        for name in self.load_names:
            _text(name, "panel-schedule load name", 128)
        if any(
            source is not None and not isinstance(source, SourceReference)
            for source in self.load_sources
        ):
            raise PanelScheduleError("Panel-schedule load source is invalid.")
        if self.kind is WayKind.LOAD and not self.load_ids:
            raise PanelScheduleError("A generated load way requires at least one load.")
        if self.kind is WayKind.SPARE and self.load_ids:
            raise PanelScheduleError("A generated spare way cannot contain loads.")


@dataclass(frozen=True, slots=True)
class PanelScheduleResult(JsonRecord):
    """Self-hashed generated schedule with no drawing-write authority."""

    schema_version: int
    schedule_version: str
    project_id: str
    document_id: str
    context_id: str
    context_evidence_sha256: str
    model_sha256: str
    source_contract_sha256: str
    panel_id: str
    panel_name: str
    rows: tuple[PanelScheduleRow, ...]
    findings: tuple[PanelScheduleFinding, ...]
    complete: bool
    outgoing_way_count: int
    load_way_count: int
    spare_way_count: int
    assigned_load_count: int
    warnings: tuple[str, ...]
    schedule_generation_performed: bool
    existing_schedule_extraction_performed: bool
    drawing_mutation_performed: bool
    result_sha256: str

    def __post_init__(self) -> None:
        if self.schema_version != 1 or self.schedule_version != SCHEDULE_VERSION:
            raise PanelScheduleError("Unsupported panel-schedule schema or version.")
        _identifier(self.project_id, "panel-schedule project ID")
        _identifier(self.document_id, "panel-schedule document ID")
        _identifier(self.panel_id, "panel-schedule panel ID")
        _text(self.panel_name, "panel-schedule panel name", 128)
        if re.fullmatch(r"[0-9a-f]{32}", self.context_id) is None:
            raise PanelScheduleError("Panel-schedule context ID is invalid.")
        for value, name in (
            (self.context_evidence_sha256, "context evidence hash"),
            (self.model_sha256, "model hash"),
            (self.source_contract_sha256, "source contract hash"),
            (self.result_sha256, "result hash"),
        ):
            if _SHA256.fullmatch(value) is None:
                raise PanelScheduleError(f"Panel-schedule {name} must be SHA-256.")
        if not isinstance(self.rows, tuple) or not 1 <= len(self.rows) <= 512 or any(
            not isinstance(item, PanelScheduleRow) for item in self.rows
        ):
            raise PanelScheduleError("Panel schedule requires 1 through 512 typed rows.")
        if tuple(item.way_number for item in self.rows) != tuple(
            sorted(item.way_number for item in self.rows)
        ):
            raise PanelScheduleError("Panel-schedule rows must be ordered by way number.")
        if len({item.way_id for item in self.rows}) != len(self.rows) or len(
            {item.circuit_id for item in self.rows}
        ) != len(self.rows):
            raise PanelScheduleError("Panel-schedule way and circuit identities must be unique.")
        if not isinstance(self.findings, tuple) or any(
            not isinstance(item, PanelScheduleFinding) for item in self.findings
        ):
            raise PanelScheduleError("Panel-schedule findings must be an immutable typed tuple.")
        if len({item.finding_id for item in self.findings}) != len(self.findings):
            raise PanelScheduleError("Panel-schedule finding IDs must be unique.")
        if self.complete is not (not self.findings):
            raise PanelScheduleError("Panel-schedule completeness conflicts with its findings.")
        expected_counts = (
            len(self.rows),
            sum(item.kind is WayKind.LOAD for item in self.rows),
            sum(item.kind is WayKind.SPARE for item in self.rows),
            sum(len(item.load_ids) for item in self.rows),
        )
        supplied_counts = (
            self.outgoing_way_count,
            self.load_way_count,
            self.spare_way_count,
            self.assigned_load_count,
        )
        if any(not isinstance(item, int) or isinstance(item, bool) for item in supplied_counts):
            raise PanelScheduleError("Panel-schedule summary counts must be integers.")
        if supplied_counts != expected_counts:
            raise PanelScheduleError("Panel-schedule summary counts conflict with its rows.")
        if not isinstance(self.warnings, tuple) or len(self.warnings) < 2:
            raise PanelScheduleError("Panel-schedule safety warnings are incomplete.")
        for warning in self.warnings:
            _text(warning, "panel-schedule warning", 512)
        if self.schedule_generation_performed is not True:
            raise PanelScheduleError("Generated panel schedule must declare generation.")
        if self.existing_schedule_extraction_performed or self.drawing_mutation_performed:
            raise PanelScheduleError(
                "Generated panel schedule cannot claim extraction or drawing mutation."
            )
        payload = self.to_dict()
        del payload["result_sha256"]
        if self.result_sha256 != _canonical_sha256(payload):
            raise PanelScheduleError("Panel-schedule result SHA-256 does not match its content.")


def generate_panel_schedule(
    model: ElectricalProjectModel, contract: PanelSldContract
) -> PanelScheduleResult:
    """Generate ordered schedule rows from one revalidated exact contract."""

    try:
        validate_panel_sld_contract(model, contract)
    except PanelSldContractError as exc:
        raise PanelScheduleError(
            "Panel schedule requires a valid contract for the exact model context."
        ) from exc
    circuits = {item.circuit_id: item for item in model.circuits}
    protection = {item.protection_id: item for item in contract.protective_devices}
    model_loads = {item.load_id: item for item in model.loads}
    assignments_by_circuit: dict[str, list[str]] = {
        item.circuit_id: [] for item in contract.outgoing_ways
    }
    for assignment in contract.load_assignments:
        assignments_by_circuit[assignment.circuit_id].append(assignment.load_id)

    rows: list[PanelScheduleRow] = []
    for way in sorted(contract.outgoing_ways, key=lambda item: item.way_number):
        device = protection[way.protective_device_id]
        load_ids = tuple(sorted(assignments_by_circuit[way.circuit_id]))
        rows.append(
            PanelScheduleRow(
                way_id=way.way_id,
                way_number=way.way_number,
                circuit_id=way.circuit_id,
                circuit_name=circuits[way.circuit_id].name,
                kind=way.kind,
                phase=way.phase,
                pole_count=way.pole_count,
                protective_device_id=device.protection_id,
                protective_device_kind=device.kind,
                protective_device_designation=device.designation,
                protective_device_rated_current_amperes=device.rated_current_amperes,
                protective_device_breaking_capacity_amperes=(
                    device.breaking_capacity_amperes
                ),
                load_ids=load_ids,
                load_names=tuple(model_loads[item].name for item in load_ids),
                load_sources=tuple(model_loads[item].source for item in load_ids),
            )
        )
    immutable_rows = tuple(rows)
    payload: dict[str, Any] = {
        "schema_version": 1,
        "schedule_version": SCHEDULE_VERSION,
        "project_id": contract.project_id,
        "document_id": contract.document_id,
        "context_id": contract.context_id,
        "context_evidence_sha256": contract.context_evidence_sha256,
        "model_sha256": contract.model_sha256,
        "source_contract_sha256": contract.contract_sha256,
        "panel_id": contract.panel.panel_id,
        "panel_name": contract.panel.name,
        "rows": immutable_rows,
        "findings": (),
        "complete": True,
        "outgoing_way_count": len(immutable_rows),
        "load_way_count": sum(item.kind is WayKind.LOAD for item in immutable_rows),
        "spare_way_count": sum(item.kind is WayKind.SPARE for item in immutable_rows),
        "assigned_load_count": sum(len(item.load_ids) for item in immutable_rows),
        "warnings": (
            "This schedule is generated only from the exact reviewed panel/SLD contract; it is not a standards-compliance approval.",
            "No existing drawing schedule was extracted and no AutoCAD drawing mutation was performed.",
        ),
        "schedule_generation_performed": True,
        "existing_schedule_extraction_performed": False,
        "drawing_mutation_performed": False,
    }
    return PanelScheduleResult(**payload, result_sha256=_canonical_sha256(payload))


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

    content = json.dumps(
        convert(value), ensure_ascii=False, sort_keys=True, separators=(",", ":")
    ).encode("utf-8")
    return hashlib.sha256(content).hexdigest()


def _identifier(value: str, name: str) -> None:
    if not isinstance(value, str) or _IDENTIFIER.fullmatch(value) is None:
        raise PanelScheduleError(f"{name} must be a bounded lowercase identifier.")


def _text(value: str, name: str, maximum: int) -> None:
    if not isinstance(value, str) or not value or len(value.encode("utf-8")) > maximum:
        raise PanelScheduleError(f"{name} must be non-empty and at most {maximum} bytes.")


def _positive_number(value: float, name: str) -> None:
    if (
        not isinstance(value, (int, float))
        or isinstance(value, bool)
        or not 0.0 < float(value) <= 1_000_000_000.0
    ):
        raise PanelScheduleError(f"{name} is outside its supported numeric range.")
