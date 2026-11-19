"""Bounded source-equivalent symmetrical short-circuit calculations."""

from __future__ import annotations

import hashlib
import json
import math
import re
from dataclasses import dataclass
from enum import Enum
from typing import Any

from agent.exceptions import ShortCircuitCalculationError
from core.electrical_drafting import canonical_model_sha256
from core.electrical_model import ElectricalProjectModel, JsonRecord, SourceReference


CALCULATION_VERSION = "m20.short_circuit_source_equivalent.v1"
_IDENTIFIER = re.compile(r"[a-z][a-z0-9_.:-]{0,127}")
_SHA256 = re.compile(r"[0-9a-f]{64}")


class FaultArrangement(str, Enum):
    """Supported voltage/impedance interpretations for a bolted fault."""

    SINGLE_PHASE_LOOP = "single_phase_loop"
    BALANCED_THREE_PHASE = "balanced_three_phase"


@dataclass(frozen=True, slots=True)
class ShortCircuitInput(JsonRecord):
    """Reviewed source-equivalent boundary for one fault location."""

    calculation_id: str
    calculation_version: str
    source_route_id: str
    fault_arrangement: FaultArrangement
    nominal_voltage_volts: float
    voltage_multiplier: float
    source_resistance_ohms: float
    source_reactance_ohms: float
    path_resistance_ohms: float
    path_reactance_ohms: float
    impedance_basis_reference: str
    reviewed_by: str
    assumptions: tuple[str, ...]

    def __post_init__(self) -> None:
        _identifier(self.calculation_id, "short-circuit calculation ID")
        if self.calculation_version != CALCULATION_VERSION:
            raise ShortCircuitCalculationError("Unsupported short-circuit calculation version.")
        _identifier(self.source_route_id, "short-circuit source route ID")
        if not isinstance(self.fault_arrangement, FaultArrangement):
            raise ShortCircuitCalculationError("Short-circuit fault arrangement is invalid.")
        _number(self.nominal_voltage_volts, "nominal voltage", minimum=0.000001)
        _number(self.voltage_multiplier, "voltage multiplier", minimum=0.000001, maximum=10.0)
        for value, name in (
            (self.source_resistance_ohms, "source resistance"),
            (self.source_reactance_ohms, "source reactance"),
            (self.path_resistance_ohms, "path resistance"),
            (self.path_reactance_ohms, "path reactance"),
        ):
            _number(value, name, minimum=0.0)
        total_resistance = self.source_resistance_ohms + self.path_resistance_ohms
        total_reactance = self.source_reactance_ohms + self.path_reactance_ohms
        if total_resistance == 0.0 and total_reactance == 0.0:
            raise ShortCircuitCalculationError(
                "Total reviewed short-circuit impedance must be positive."
            )
        _text(self.impedance_basis_reference, "short-circuit impedance basis reference", 512)
        _text(self.reviewed_by, "short-circuit reviewer", 128)
        if not isinstance(self.assumptions, tuple) or not 1 <= len(self.assumptions) <= 16:
            raise ShortCircuitCalculationError(
                "Short-circuit assumptions must contain 1 through 16 entries."
            )
        for assumption in self.assumptions:
            _text(assumption, "short-circuit assumption", 512)


@dataclass(frozen=True, slots=True)
class ShortCircuitResult(JsonRecord):
    """Self-hashed symmetrical RMS result with no equipment adequacy conclusion."""

    schema_version: int
    project_id: str
    document_id: str
    context_id: str
    context_evidence_sha256: str
    model_sha256: str
    input: ShortCircuitInput
    route_source: SourceReference
    total_resistance_ohms: float
    total_reactance_ohms: float
    total_impedance_magnitude_ohms: float
    symmetrical_short_circuit_current_amperes: float
    apparent_fault_power_volt_amperes: float
    formulas: tuple[str, ...]
    warnings: tuple[str, ...]
    protective_device_adequacy_evaluated: bool
    peak_current_calculated: bool
    arc_flash_calculated: bool
    compliance_evaluation_performed: bool
    drawing_mutation_performed: bool
    result_sha256: str

    def __post_init__(self) -> None:
        if self.schema_version != 1:
            raise ShortCircuitCalculationError("Short-circuit result schema must be 1.")
        _identifier(self.project_id, "short-circuit result project ID")
        _identifier(self.document_id, "short-circuit result document ID")
        if re.fullmatch(r"[0-9a-f]{32}", self.context_id) is None:
            raise ShortCircuitCalculationError("Short-circuit result context ID is invalid.")
        for value, name in (
            (self.context_evidence_sha256, "context evidence hash"),
            (self.model_sha256, "model hash"),
            (self.result_sha256, "result hash"),
        ):
            if _SHA256.fullmatch(value) is None:
                raise ShortCircuitCalculationError(f"Short-circuit {name} must be SHA-256.")
        if not isinstance(self.input, ShortCircuitInput):
            raise ShortCircuitCalculationError("Short-circuit result input is invalid.")
        if not isinstance(self.route_source, SourceReference):
            raise ShortCircuitCalculationError("Short-circuit route source is invalid.")
        for value, name in (
            (self.total_resistance_ohms, "total resistance"),
            (self.total_reactance_ohms, "total reactance"),
            (self.total_impedance_magnitude_ohms, "impedance magnitude"),
            (self.symmetrical_short_circuit_current_amperes, "symmetrical current"),
            (self.apparent_fault_power_volt_amperes, "apparent fault power"),
        ):
            _number(value, name, minimum=0.0)
        if self.total_impedance_magnitude_ohms <= 0 or self.symmetrical_short_circuit_current_amperes <= 0:
            raise ShortCircuitCalculationError("Short-circuit result impedance/current must be positive.")
        if len(self.formulas) != 2 or any(not isinstance(item, str) for item in self.formulas):
            raise ShortCircuitCalculationError("Short-circuit formulas are incomplete.")
        if len(self.warnings) < 3 or any(not isinstance(item, str) for item in self.warnings):
            raise ShortCircuitCalculationError("Short-circuit warnings are incomplete.")
        for formula in self.formulas:
            _text(formula, "short-circuit formula", 512)
        for warning in self.warnings:
            _text(warning, "short-circuit warning", 512)
        if any((
            self.protective_device_adequacy_evaluated,
            self.peak_current_calculated,
            self.arc_flash_calculated,
            self.compliance_evaluation_performed,
            self.drawing_mutation_performed,
        )):
            raise ShortCircuitCalculationError(
                "Short-circuit boundary cannot claim equipment, peak, arc-flash, compliance, or mutation results."
            )
        value = self.to_dict()
        del value["result_sha256"]
        if self.result_sha256 != _canonical_sha256(value):
            raise ShortCircuitCalculationError("Short-circuit result SHA-256 does not match its content.")


def calculate_short_circuit(
    model: ElectricalProjectModel,
    calculation_input: ShortCircuitInput,
    *,
    context_evidence_sha256: str,
) -> ShortCircuitResult:
    """Calculate bolted symmetrical RMS current from reviewed source/path R and X."""
    if not isinstance(model, ElectricalProjectModel) or not isinstance(
        calculation_input, ShortCircuitInput
    ):
        raise ShortCircuitCalculationError(
            "Short-circuit calculation requires a typed model and input."
        )
    if _SHA256.fullmatch(context_evidence_sha256) is None:
        raise ShortCircuitCalculationError("Context evidence hash must be SHA-256.")
    if model.provenance.evidence_sha256 != context_evidence_sha256:
        raise ShortCircuitCalculationError("Model provenance does not match context evidence.")
    routes = {item.route_id: item for item in model.routes}
    try:
        route = routes[calculation_input.source_route_id]
    except KeyError as exc:
        raise ShortCircuitCalculationError(
            f"Short-circuit input references an unknown source route: {calculation_input.source_route_id}."
        ) from exc

    resistance = _normalized(
        calculation_input.source_resistance_ohms + calculation_input.path_resistance_ohms
    )
    reactance = _normalized(
        calculation_input.source_reactance_ohms + calculation_input.path_reactance_ohms
    )
    impedance = _normalized(math.hypot(resistance, reactance))
    voltage = calculation_input.nominal_voltage_volts * calculation_input.voltage_multiplier
    if calculation_input.fault_arrangement is FaultArrangement.SINGLE_PHASE_LOOP:
        current = _normalized(voltage / impedance)
        apparent_power = _normalized(calculation_input.nominal_voltage_volts * current)
        current_formula = "Ik_rms=c*V_LN/sqrt((R_source+R_path)^2+(X_source+X_path)^2)"
        power_formula = "S_fault=V_LN*Ik_rms"
        voltage_warning = (
            "Nominal voltage is line-to-neutral and reviewed path impedance must represent the complete fault loop."
        )
    else:
        current = _normalized(voltage / (math.sqrt(3.0) * impedance))
        apparent_power = _normalized(
            math.sqrt(3.0) * calculation_input.nominal_voltage_volts * current
        )
        current_formula = "Ik_rms=c*V_LL/(sqrt(3)*sqrt((R_source+R_path)^2+(X_source+X_path)^2))"
        power_formula = "S_fault=sqrt(3)*V_LL*Ik_rms"
        voltage_warning = "Nominal voltage is line-to-line for a balanced three-phase bolted fault."

    payload: dict[str, Any] = {
        "schema_version": 1,
        "project_id": model.project_id,
        "document_id": model.document_id,
        "context_id": model.context_id,
        "context_evidence_sha256": context_evidence_sha256,
        "model_sha256": canonical_model_sha256(model),
        "input": calculation_input,
        "route_source": route.source,
        "total_resistance_ohms": resistance,
        "total_reactance_ohms": reactance,
        "total_impedance_magnitude_ohms": impedance,
        "symmetrical_short_circuit_current_amperes": current,
        "apparent_fault_power_volt_amperes": apparent_power,
        "formulas": (current_formula, power_formula),
        "warnings": (
            voltage_warning,
            "Source/path R and X plus voltage multiplier are reviewed inputs; external evidence and applicability are not verified.",
            "Generator, motor, converter, transformer-tap, sequence-network, transient/peak, arc impedance, arc-flash, and protective-device effects are not calculated.",
            "No equipment rating, interruption capacity, coordination, or standards-compliance conclusion is performed.",
        ),
        "protective_device_adequacy_evaluated": False,
        "peak_current_calculated": False,
        "arc_flash_calculated": False,
        "compliance_evaluation_performed": False,
        "drawing_mutation_performed": False,
    }
    return ShortCircuitResult(**payload, result_sha256=_canonical_sha256(payload))


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
    encoded = json.dumps(convert(value), ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest()


def _normalized(value: float) -> float:
    normalized = round(float(value), 12)
    return 0.0 if normalized == 0.0 else normalized


def _identifier(value: str, name: str) -> None:
    if not isinstance(value, str) or _IDENTIFIER.fullmatch(value) is None:
        raise ShortCircuitCalculationError(f"{name} must be a bounded lowercase identifier.")


def _text(value: str, name: str, maximum: int) -> None:
    if not isinstance(value, str) or not value or len(value.encode("utf-8")) > maximum:
        raise ShortCircuitCalculationError(f"{name} must be non-empty and at most {maximum} bytes.")
    if any(ord(character) < 32 and character != "\t" for character in value):
        raise ShortCircuitCalculationError(f"{name} contains unsupported control characters.")


def _number(value: float, name: str, *, minimum: float, maximum: float = 1_000_000_000.0) -> None:
    if (
        not isinstance(value, (int, float))
        or isinstance(value, bool)
        or not math.isfinite(value)
        or not minimum <= float(value) <= maximum
    ):
        raise ShortCircuitCalculationError(f"{name} is outside its supported numeric range.")
