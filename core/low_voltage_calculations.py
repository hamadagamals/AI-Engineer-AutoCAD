"""Pure, versioned balanced load-current and approximate voltage-drop calculations."""

from __future__ import annotations

import hashlib
import json
import math
import re
from dataclasses import dataclass
from enum import Enum
from typing import Any

from agent.exceptions import LowVoltageCalculationError
from core.electrical_drafting import canonical_model_sha256
from core.electrical_model import (
    ElectricalProjectModel,
    JsonRecord,
    SourceReference,
    UnitSystem,
)


CALCULATION_VERSION = "m20.lv_current_voltage_drop.v1"
_IDENTIFIER = re.compile(r"[a-z][a-z0-9_.:-]{0,127}")
_SHA256 = re.compile(r"[0-9a-f]{64}")
_METERS_PER_UNIT = {
    UnitSystem.INCH: 0.0254,
    UnitSystem.FOOT: 0.3048,
    UnitSystem.MILLIMETER: 0.001,
    UnitSystem.CENTIMETER: 0.01,
    UnitSystem.METER: 1.0,
}


class PhaseSystem(str, Enum):
    """Supported balanced supply arrangements for calculation version 1."""

    SINGLE_PHASE = "single_phase"
    BALANCED_THREE_PHASE = "balanced_three_phase"


@dataclass(frozen=True, slots=True)
class FormulaReference(JsonRecord):
    """Stable formula identity and human-readable expression."""

    formula_id: str
    expression: str

    def __post_init__(self) -> None:
        _identifier(self.formula_id, "formula ID")
        _text(self.expression, "formula expression", 512)


@dataclass(frozen=True, slots=True)
class LowVoltageCalculationInput(JsonRecord):
    """Explicit engineer-reviewed inputs for one bounded steady-state calculation."""

    calculation_id: str
    calculation_version: str
    phase_system: PhaseSystem
    load_output_power_watts: float
    demand_factor: float
    nominal_voltage_volts: float
    power_factor: float
    efficiency: float
    conductor_ac_resistance_ohm_per_km: float
    conductor_reactance_ohm_per_km: float
    load_object_ids: tuple[str, ...]
    route_ids: tuple[str, ...]
    reviewed_by: str
    assumptions: tuple[str, ...]

    def __post_init__(self) -> None:
        _identifier(self.calculation_id, "calculation ID")
        if self.calculation_version != CALCULATION_VERSION:
            raise LowVoltageCalculationError("Unsupported low-voltage calculation version.")
        if not isinstance(self.phase_system, PhaseSystem):
            raise LowVoltageCalculationError("Low-voltage phase system is invalid.")
        _number(self.load_output_power_watts, "load output power", minimum=0.000001)
        _number(self.demand_factor, "demand factor", minimum=0.000001, maximum=1.0)
        _number(self.nominal_voltage_volts, "nominal voltage", minimum=0.000001)
        _number(self.power_factor, "power factor", minimum=0.000001, maximum=1.0)
        _number(self.efficiency, "efficiency", minimum=0.000001, maximum=1.0)
        _number(
            self.conductor_ac_resistance_ohm_per_km,
            "conductor AC resistance",
            minimum=0.0,
        )
        _number(
            self.conductor_reactance_ohm_per_km,
            "conductor reactance",
            minimum=0.0,
        )
        if (
            self.conductor_ac_resistance_ohm_per_km == 0
            and self.conductor_reactance_ohm_per_km == 0
        ):
            raise LowVoltageCalculationError(
                "At least one conductor impedance component must be positive."
            )
        _identifiers(self.load_object_ids, "load object IDs", minimum=1, maximum=256)
        _identifiers(self.route_ids, "route IDs", minimum=1, maximum=256)
        _text(self.reviewed_by, "calculation reviewer", 128)
        if not isinstance(self.assumptions, tuple) or not 1 <= len(self.assumptions) <= 16:
            raise LowVoltageCalculationError(
                "Calculation assumptions must contain 1 through 16 reviewed entries."
            )
        for assumption in self.assumptions:
            _text(assumption, "calculation assumption", 512)


@dataclass(frozen=True, slots=True)
class LowVoltageCalculationResult(JsonRecord):
    """Self-hashed reproducible output with source and formula traceability."""

    schema_version: int
    project_id: str
    document_id: str
    context_id: str
    context_evidence_sha256: str
    model_sha256: str
    input: LowVoltageCalculationInput
    load_sources: tuple[SourceReference, ...]
    route_sources: tuple[SourceReference, ...]
    route_length_meters: float
    connected_output_power_watts: float
    demand_output_power_watts: float
    apparent_input_power_volt_amperes: float
    calculated_current_amperes: float
    voltage_drop_volts: float
    voltage_drop_percent: float
    formulas: tuple[FormulaReference, ...]
    warnings: tuple[str, ...]
    compliance_evaluation_performed: bool
    drawing_mutation_performed: bool
    result_sha256: str

    def __post_init__(self) -> None:
        if self.schema_version != 1:
            raise LowVoltageCalculationError("Low-voltage result schema must be 1.")
        _identifier(self.project_id, "result project ID")
        _identifier(self.document_id, "result document ID")
        if re.fullmatch(r"[0-9a-f]{32}", self.context_id) is None:
            raise LowVoltageCalculationError("Low-voltage result context ID is invalid.")
        for value, name in (
            (self.context_evidence_sha256, "context evidence hash"),
            (self.model_sha256, "model hash"),
            (self.result_sha256, "result hash"),
        ):
            if _SHA256.fullmatch(value) is None:
                raise LowVoltageCalculationError(f"Low-voltage {name} must be SHA-256.")
        if not isinstance(self.input, LowVoltageCalculationInput):
            raise LowVoltageCalculationError("Low-voltage result input is invalid.")
        _sources(self.load_sources, "load sources")
        _sources(self.route_sources, "route sources")
        for value, name in (
            (self.route_length_meters, "route length"),
            (self.connected_output_power_watts, "connected output power"),
            (self.demand_output_power_watts, "demand output power"),
            (self.apparent_input_power_volt_amperes, "apparent input power"),
            (self.calculated_current_amperes, "calculated current"),
            (self.voltage_drop_volts, "voltage drop"),
            (self.voltage_drop_percent, "voltage-drop percent"),
        ):
            _number(value, name, minimum=0.0)
        if self.route_length_meters <= 0 or self.calculated_current_amperes <= 0:
            raise LowVoltageCalculationError("Low-voltage result must have positive length and current.")
        if not isinstance(self.formulas, tuple) or len(self.formulas) != 2 or any(
            not isinstance(item, FormulaReference) for item in self.formulas
        ):
            raise LowVoltageCalculationError("Low-voltage result must retain two formulas.")
        if not isinstance(self.warnings, tuple) or len(self.warnings) < 2:
            raise LowVoltageCalculationError("Low-voltage result must retain limitations.")
        for warning in self.warnings:
            _text(warning, "calculation warning", 512)
        if self.compliance_evaluation_performed is not False:
            raise LowVoltageCalculationError("This calculation cannot claim compliance evaluation.")
        if self.drawing_mutation_performed is not False:
            raise LowVoltageCalculationError("This calculation cannot claim drawing mutation.")
        value = self.to_dict()
        del value["result_sha256"]
        if self.result_sha256 != _canonical_sha256(value):
            raise LowVoltageCalculationError("Low-voltage result SHA-256 does not match its content.")


def calculate_low_voltage(
    model: ElectricalProjectModel,
    calculation_input: LowVoltageCalculationInput,
    *,
    context_evidence_sha256: str,
) -> LowVoltageCalculationResult:
    """Calculate balanced load current and approximate steady-state voltage drop."""
    if not isinstance(model, ElectricalProjectModel):
        raise LowVoltageCalculationError("Low-voltage calculation requires a typed model.")
    if not isinstance(calculation_input, LowVoltageCalculationInput):
        raise LowVoltageCalculationError("Low-voltage calculation input must be typed.")
    if _SHA256.fullmatch(context_evidence_sha256) is None:
        raise LowVoltageCalculationError("Context evidence hash must be SHA-256.")
    if model.provenance.evidence_sha256 != context_evidence_sha256:
        raise LowVoltageCalculationError("Model provenance does not match context evidence.")
    drawing = model.drawings[0]
    if drawing.unit_system is not model.unit_system:
        raise LowVoltageCalculationError("Drawing and model unit systems do not match.")
    try:
        meters_per_unit = _METERS_PER_UNIT[model.unit_system]
    except KeyError as exc:
        raise LowVoltageCalculationError(
            "Low-voltage calculation requires an explicit supported drawing length unit."
        ) from exc

    load_sources_by_id = {
        **{item.device_id: item.source for item in model.devices},
        **{item.load_id: item.source for item in model.loads if item.source is not None},
    }
    missing_loads = sorted(set(calculation_input.load_object_ids) - load_sources_by_id.keys())
    if missing_loads:
        raise LowVoltageCalculationError(
            f"Calculation references an unknown or unsourced load object: {missing_loads[0]}."
        )
    routes_by_id = {item.route_id: item for item in model.routes}
    missing_routes = sorted(set(calculation_input.route_ids) - routes_by_id.keys())
    if missing_routes:
        raise LowVoltageCalculationError(
            f"Calculation references an unknown route: {missing_routes[0]}."
        )

    route_lengths: list[float] = []
    for route_id in calculation_input.route_ids:
        route = routes_by_id[route_id]
        for start, end in zip(route.points, route.points[1:]):
            segment = math.dist(start, end) * meters_per_unit
            if segment <= 0:
                raise LowVoltageCalculationError(
                    f"Route {route_id} contains an incomplete zero-length segment."
                )
            route_lengths.append(segment)
    route_length = _normalized(math.fsum(route_lengths))
    connected_power = _normalized(calculation_input.load_output_power_watts)
    demand_power = _normalized(connected_power * calculation_input.demand_factor)
    apparent_input = _normalized(
        demand_power / (calculation_input.power_factor * calculation_input.efficiency)
    )
    phase_multiplier = (
        1.0
        if calculation_input.phase_system is PhaseSystem.SINGLE_PHASE
        else math.sqrt(3.0)
    )
    current = _normalized(
        apparent_input / (phase_multiplier * calculation_input.nominal_voltage_volts)
    )
    reactive_factor = math.sqrt(max(0.0, 1.0 - calculation_input.power_factor**2))
    impedance_component = (
        calculation_input.conductor_ac_resistance_ohm_per_km
        * calculation_input.power_factor
        + calculation_input.conductor_reactance_ohm_per_km * reactive_factor
    )
    drop_multiplier = (
        2.0
        if calculation_input.phase_system is PhaseSystem.SINGLE_PHASE
        else math.sqrt(3.0)
    )
    voltage_drop = _normalized(
        drop_multiplier * current * (route_length / 1000.0) * impedance_component
    )
    voltage_drop_percent = _normalized(
        100.0 * voltage_drop / calculation_input.nominal_voltage_volts
    )
    if calculation_input.phase_system is PhaseSystem.SINGLE_PHASE:
        current_formula = FormulaReference(
            "formula:single_phase_load_current_v1",
            "I=Pout*demand_factor/(V_LN*power_factor*efficiency)",
        )
        drop_formula = FormulaReference(
            "formula:single_phase_voltage_drop_v1",
            "dV=2*I*L_km*(R_ac*power_factor+X*sqrt(1-power_factor^2))",
        )
        voltage_warning = "Nominal voltage is interpreted as line-to-neutral voltage."
    else:
        current_formula = FormulaReference(
            "formula:balanced_three_phase_load_current_v1",
            "I=Pout*demand_factor/(sqrt(3)*V_LL*power_factor*efficiency)",
        )
        drop_formula = FormulaReference(
            "formula:balanced_three_phase_voltage_drop_v1",
            "dV=sqrt(3)*I*L_km*(R_ac*power_factor+X*sqrt(1-power_factor^2))",
        )
        voltage_warning = "Nominal voltage is interpreted as line-to-line voltage."

    payload: dict[str, Any] = {
        "schema_version": 1,
        "project_id": model.project_id,
        "document_id": model.document_id,
        "context_id": model.context_id,
        "context_evidence_sha256": context_evidence_sha256,
        "model_sha256": canonical_model_sha256(model),
        "input": calculation_input,
        "load_sources": tuple(load_sources_by_id[item] for item in calculation_input.load_object_ids),
        "route_sources": tuple(routes_by_id[item].source for item in calculation_input.route_ids),
        "route_length_meters": route_length,
        "connected_output_power_watts": connected_power,
        "demand_output_power_watts": demand_power,
        "apparent_input_power_volt_amperes": apparent_input,
        "calculated_current_amperes": current,
        "voltage_drop_volts": voltage_drop,
        "voltage_drop_percent": voltage_drop_percent,
        "formulas": (current_formula, drop_formula),
        "warnings": (
            "Steady-state calculation assumes one lagging, sinusoidal, balanced load basis.",
            voltage_warning,
            "Resistance and reactance are reviewed inputs; no temperature, harmonic, grouping, installation, or derating correction is applied.",
            "No cable sizing, ampacity, protection, short-circuit, or standards-compliance conclusion is performed.",
        ),
        "compliance_evaluation_performed": False,
        "drawing_mutation_performed": False,
    }
    return LowVoltageCalculationResult(
        **payload,
        result_sha256=_canonical_sha256(payload),
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


def _normalized(value: float) -> float:
    normalized = round(float(value), 12)
    return 0.0 if normalized == 0.0 else normalized


def _identifier(value: str, name: str) -> None:
    if not isinstance(value, str) or _IDENTIFIER.fullmatch(value) is None:
        raise LowVoltageCalculationError(f"{name} must be a bounded lowercase identifier.")


def _identifiers(
    values: tuple[str, ...], name: str, *, minimum: int, maximum: int
) -> None:
    if not isinstance(values, tuple) or not minimum <= len(values) <= maximum:
        raise LowVoltageCalculationError(
            f"{name} must contain {minimum} through {maximum} identifiers."
        )
    for value in values:
        _identifier(value, name)
    if len(values) != len(set(values)):
        raise LowVoltageCalculationError(f"{name} must be unique.")


def _sources(values: tuple[SourceReference, ...], name: str) -> None:
    if not isinstance(values, tuple) or not values or any(
        not isinstance(value, SourceReference) for value in values
    ):
        raise LowVoltageCalculationError(f"Low-voltage {name} are invalid.")


def _text(value: str, name: str, maximum: int) -> None:
    if not isinstance(value, str) or not value or len(value.encode("utf-8")) > maximum:
        raise LowVoltageCalculationError(f"{name} must be non-empty and at most {maximum} bytes.")
    if any(ord(character) < 32 and character != "\t" for character in value):
        raise LowVoltageCalculationError(f"{name} contains unsupported control characters.")


def _number(
    value: float,
    name: str,
    *,
    minimum: float,
    maximum: float = 1_000_000_000.0,
) -> None:
    if (
        not isinstance(value, (int, float))
        or isinstance(value, bool)
        or not math.isfinite(value)
        or not minimum <= float(value) <= maximum
    ):
        raise LowVoltageCalculationError(f"{name} is outside its supported numeric range.")
