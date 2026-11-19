"""Reviewed adiabatic PE/earthing-conductor sizing with explicit unresolved outcomes."""

from __future__ import annotations

import hashlib
import json
import math
import re
from dataclasses import dataclass
from enum import Enum
from typing import Any

from agent.exceptions import ProtectiveConductorSizingError
from core.electrical_drafting import canonical_model_sha256
from core.electrical_model import ElectricalProjectModel, JsonRecord, SourceReference
from core.short_circuit import ShortCircuitResult


CALCULATION_VERSION = "m20.protective_conductor_adiabatic.v1"
_IDENTIFIER = re.compile(r"[a-z][a-z0-9_.:-]{0,127}")
_SHA256 = re.compile(r"[0-9a-f]{64}")


class RuleApplicability(str, Enum):
    APPLICABLE = "applicable"
    NOT_APPLICABLE = "not_applicable"
    UNKNOWN = "unknown"


class DisconnectionBasis(str, Enum):
    REVIEWED_PROTECTIVE_DEVICE_TIME = "reviewed_protective_device_time"
    REVIEWED_UPSTREAM_CLEARING_TIME = "reviewed_upstream_clearing_time"
    UNKNOWN = "unknown"


class SizingOutcome(str, Enum):
    CALCULATED = "calculated"
    NOT_APPLICABLE = "not_applicable"
    UNKNOWN = "unknown"


@dataclass(frozen=True, slots=True)
class ProtectiveConductorRuleInput(JsonRecord):
    calculation_id: str
    calculation_version: str
    source_route_id: str
    applicability: RuleApplicability
    applicability_basis_reference: str
    disconnection_basis: DisconnectionBasis
    disconnection_basis_reference: str
    disconnection_time_seconds: float | None
    conductor_factor_amperes_sqrt_second_per_square_millimeter: float | None
    rule_source_id: str
    rule_source_title: str
    rule_source_version: str
    rule_source_locator: str
    rule_source_sha256: str
    reviewed_by: str
    assumptions: tuple[str, ...]

    def __post_init__(self) -> None:
        _identifier(self.calculation_id, "protective-conductor calculation ID")
        if self.calculation_version != CALCULATION_VERSION:
            raise ProtectiveConductorSizingError(
                "Unsupported protective-conductor calculation version."
            )
        _identifier(self.source_route_id, "protective-conductor source route ID")
        if not isinstance(self.applicability, RuleApplicability):
            raise ProtectiveConductorSizingError("Rule applicability is invalid.")
        if not isinstance(self.disconnection_basis, DisconnectionBasis):
            raise ProtectiveConductorSizingError("Disconnection basis is invalid.")
        _text(self.applicability_basis_reference, "applicability basis", 512)
        _text(self.disconnection_basis_reference, "disconnection basis reference", 512)
        _identifier(self.rule_source_id, "rule source ID")
        _text(self.rule_source_title, "rule source title", 256)
        _text(self.rule_source_version, "rule source version", 128)
        _text(self.rule_source_locator, "rule source locator", 512)
        if _SHA256.fullmatch(self.rule_source_sha256) is None:
            raise ProtectiveConductorSizingError("Rule source identity must be SHA-256.")
        _text(self.reviewed_by, "protective-conductor reviewer", 128)
        if not isinstance(self.assumptions, tuple) or not 1 <= len(self.assumptions) <= 16:
            raise ProtectiveConductorSizingError(
                "Protective-conductor assumptions must contain 1 through 16 entries."
            )
        for assumption in self.assumptions:
            _text(assumption, "protective-conductor assumption", 512)

        if self.applicability is RuleApplicability.APPLICABLE:
            if self.disconnection_basis is DisconnectionBasis.UNKNOWN:
                raise ProtectiveConductorSizingError(
                    "Applicable sizing requires an explicit reviewed disconnection basis."
                )
            _number(self.disconnection_time_seconds, "disconnection time", 0.000001)
            _number(
                self.conductor_factor_amperes_sqrt_second_per_square_millimeter,
                "reviewed conductor factor",
                0.000001,
            )
        elif self.disconnection_time_seconds is not None or (
            self.conductor_factor_amperes_sqrt_second_per_square_millimeter is not None
        ):
            raise ProtectiveConductorSizingError(
                "Unknown or not-applicable sizing cannot carry calculation numbers."
            )


@dataclass(frozen=True, slots=True)
class ProtectiveConductorSizingResult(JsonRecord):
    schema_version: int
    project_id: str
    document_id: str
    context_id: str
    context_evidence_sha256: str
    model_sha256: str
    input: ProtectiveConductorRuleInput
    route_source: SourceReference
    outcome: SizingOutcome
    short_circuit_result_sha256: str | None
    fault_current_amperes: float | None
    required_cross_section_square_millimeters: float | None
    fault_current_unit: str
    disconnection_time_unit: str
    conductor_factor_unit: str
    cross_section_unit: str
    formula: str | None
    warnings: tuple[str, ...]
    conductor_selection_performed: bool
    compliance_evaluation_performed: bool
    drawing_mutation_performed: bool
    result_sha256: str

    def __post_init__(self) -> None:
        if self.schema_version != 1:
            raise ProtectiveConductorSizingError("Protective-conductor result schema must be 1.")
        _identifier(self.project_id, "protective-conductor result project ID")
        _identifier(self.document_id, "protective-conductor result document ID")
        if re.fullmatch(r"[0-9a-f]{32}", self.context_id) is None:
            raise ProtectiveConductorSizingError("Protective-conductor context ID is invalid.")
        for value, name in (
            (self.context_evidence_sha256, "context evidence hash"),
            (self.model_sha256, "model hash"),
            (self.result_sha256, "result hash"),
        ):
            if _SHA256.fullmatch(value) is None:
                raise ProtectiveConductorSizingError(
                    f"Protective-conductor {name} must be SHA-256."
                )
        if not isinstance(self.input, ProtectiveConductorRuleInput):
            raise ProtectiveConductorSizingError("Protective-conductor result input is invalid.")
        if not isinstance(self.route_source, SourceReference):
            raise ProtectiveConductorSizingError("Protective-conductor route source is invalid.")
        if not isinstance(self.outcome, SizingOutcome):
            raise ProtectiveConductorSizingError("Protective-conductor outcome is invalid.")
        for value, expected, name in (
            (self.fault_current_unit, "A", "fault-current unit"),
            (self.disconnection_time_unit, "s", "disconnection-time unit"),
            (self.conductor_factor_unit, "A*sqrt(s)/mm2", "conductor-factor unit"),
            (self.cross_section_unit, "mm2", "cross-section unit"),
        ):
            if value != expected:
                raise ProtectiveConductorSizingError(f"{name} is invalid.")
        if self.outcome is SizingOutcome.CALCULATED:
            if self.short_circuit_result_sha256 is None or (
                _SHA256.fullmatch(self.short_circuit_result_sha256) is None
            ):
                raise ProtectiveConductorSizingError(
                    "Calculated sizing requires a short-circuit result hash."
                )
            _number(self.fault_current_amperes, "fault current", 0.000001)
            _number(self.required_cross_section_square_millimeters, "required cross section", 0.0)
            _text(self.formula, "protective-conductor formula", 512)
        elif any(
            value is not None
            for value in (
                self.short_circuit_result_sha256,
                self.fault_current_amperes,
                self.required_cross_section_square_millimeters,
                self.formula,
            )
        ):
            raise ProtectiveConductorSizingError(
                "Unresolved sizing result cannot contain calculated values."
            )
        if not isinstance(self.warnings, tuple) or len(self.warnings) < 2:
            raise ProtectiveConductorSizingError("Protective-conductor warnings are incomplete.")
        for warning in self.warnings:
            _text(warning, "protective-conductor warning", 512)
        if any(
            (
                self.conductor_selection_performed,
                self.compliance_evaluation_performed,
                self.drawing_mutation_performed,
            )
        ):
            raise ProtectiveConductorSizingError(
                "Protective-conductor sizing cannot claim selection, compliance, or mutation."
            )
        value = self.to_dict()
        del value["result_sha256"]
        if self.result_sha256 != _canonical_sha256(value):
            raise ProtectiveConductorSizingError(
                "Protective-conductor result SHA-256 does not match its content."
            )


def size_protective_conductor(
    model: ElectricalProjectModel,
    rule_input: ProtectiveConductorRuleInput,
    short_circuit: ShortCircuitResult | None,
    *,
    context_evidence_sha256: str,
) -> ProtectiveConductorSizingResult:
    """Apply a reviewed adiabatic rule or return an explicit unresolved outcome."""
    if not isinstance(model, ElectricalProjectModel) or not isinstance(
        rule_input, ProtectiveConductorRuleInput
    ):
        raise ProtectiveConductorSizingError(
            "Protective-conductor sizing requires a typed model and rule input."
        )
    if _SHA256.fullmatch(context_evidence_sha256) is None or (
        model.provenance.evidence_sha256 != context_evidence_sha256
    ):
        raise ProtectiveConductorSizingError(
            "Model provenance does not match protective-conductor context evidence."
        )
    routes = {item.route_id: item for item in model.routes}
    try:
        route = routes[rule_input.source_route_id]
    except KeyError as exc:
        raise ProtectiveConductorSizingError(
            f"Protective-conductor input references an unknown route: {rule_input.source_route_id}."
        ) from exc

    outcome: SizingOutcome
    short_hash: str | None = None
    fault_current: float | None = None
    required_cross_section: float | None = None
    formula: str | None = None
    if rule_input.applicability is RuleApplicability.UNKNOWN:
        if short_circuit is not None:
            raise ProtectiveConductorSizingError(
                "Unknown applicability must not consume a short-circuit result."
            )
        outcome = SizingOutcome.UNKNOWN
    elif rule_input.applicability is RuleApplicability.NOT_APPLICABLE:
        if short_circuit is not None:
            raise ProtectiveConductorSizingError(
                "Not-applicable sizing must not consume a short-circuit result."
            )
        outcome = SizingOutcome.NOT_APPLICABLE
    else:
        if not isinstance(short_circuit, ShortCircuitResult):
            raise ProtectiveConductorSizingError(
                "Applicable sizing requires an exact typed short-circuit result."
            )
        model_hash = canonical_model_sha256(model)
        if (
            short_circuit.context_id != model.context_id
            or short_circuit.context_evidence_sha256 != context_evidence_sha256
            or short_circuit.model_sha256 != model_hash
            or short_circuit.input.source_route_id != rule_input.source_route_id
        ):
            raise ProtectiveConductorSizingError(
                "Short-circuit result does not match the exact model context and route."
            )
        outcome = SizingOutcome.CALCULATED
        short_hash = short_circuit.result_sha256
        fault_current = short_circuit.symmetrical_short_circuit_current_amperes
        required_cross_section = _normalized(
            fault_current
            * math.sqrt(float(rule_input.disconnection_time_seconds))
            / float(
                rule_input.conductor_factor_amperes_sqrt_second_per_square_millimeter
            )
        )
        formula = "S_required_mm2=Ik_rms_A*sqrt(t_seconds)/k_A_sqrt_s_per_mm2"

    payload: dict[str, Any] = {
        "schema_version": 1,
        "project_id": model.project_id,
        "document_id": model.document_id,
        "context_id": model.context_id,
        "context_evidence_sha256": context_evidence_sha256,
        "model_sha256": canonical_model_sha256(model),
        "input": rule_input,
        "route_source": route.source,
        "outcome": outcome,
        "short_circuit_result_sha256": short_hash,
        "fault_current_amperes": fault_current,
        "required_cross_section_square_millimeters": required_cross_section,
        "fault_current_unit": "A",
        "disconnection_time_unit": "s",
        "conductor_factor_unit": "A*sqrt(s)/mm2",
        "cross_section_unit": "mm2",
        "formula": formula,
        "warnings": (
            "The rule source, applicability, disconnection time, and conductor factor are reviewed project inputs; their external authority and applicability are not verified.",
            "The result is a required adiabatic cross-section value only; no standard size, conductor, bonding path, protective device, or product is selected.",
            "No minimum-size rule, thermal initial/final temperature verification, mechanical constraint, touch voltage, loop impedance, coordination, or standards-compliance conclusion is performed.",
        ),
        "conductor_selection_performed": False,
        "compliance_evaluation_performed": False,
        "drawing_mutation_performed": False,
    }
    return ProtectiveConductorSizingResult(
        **payload, result_sha256=_canonical_sha256(payload)
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

    return hashlib.sha256(
        json.dumps(
            convert(value), ensure_ascii=False, sort_keys=True, separators=(",", ":")
        ).encode("utf-8")
    ).hexdigest()


def _normalized(value: float) -> float:
    normalized = round(float(value), 12)
    return 0.0 if normalized == 0.0 else normalized


def _identifier(value: str, name: str) -> None:
    if not isinstance(value, str) or _IDENTIFIER.fullmatch(value) is None:
        raise ProtectiveConductorSizingError(
            f"{name} must be a bounded lowercase identifier."
        )


def _text(value: object, name: str, maximum: int) -> None:
    if not isinstance(value, str) or not value or len(value.encode("utf-8")) > maximum:
        raise ProtectiveConductorSizingError(
            f"{name} must be non-empty and at most {maximum} bytes."
        )


def _number(
    value: object, name: str, minimum: float, maximum: float = 1_000_000_000.0
) -> None:
    if (
        not isinstance(value, (int, float))
        or isinstance(value, bool)
        or not math.isfinite(value)
        or not minimum <= float(value) <= maximum
    ):
        raise ProtectiveConductorSizingError(
            f"{name} is outside its supported numeric range."
        )
