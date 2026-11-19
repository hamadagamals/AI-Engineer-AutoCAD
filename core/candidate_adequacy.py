"""Reviewed project-rule evaluation for one cable and protective-device candidate."""

from __future__ import annotations

import hashlib
import json
import math
import re
from dataclasses import dataclass
from enum import Enum
from typing import Any

from agent.exceptions import CandidateAdequacyError
from core.cable_ampacity import CableAmpacityResult
from core.electrical_drafting import canonical_model_sha256
from core.electrical_model import ElectricalProjectModel, JsonRecord
from core.low_voltage_calculations import LowVoltageCalculationResult
from core.short_circuit import ShortCircuitResult


RULE_SET_VERSION = "m20.candidate_adequacy.v1"
_IDENTIFIER = re.compile(r"[a-z][a-z0-9_.:-]{0,127}")
_SHA256 = re.compile(r"[0-9a-f]{64}")


class FindingOutcome(str, Enum):
    PASS = "pass"
    FAIL = "fail"


@dataclass(frozen=True, slots=True)
class CandidateAdequacyRuleSet(JsonRecord):
    rule_set_id: str
    rule_set_version: str
    candidate_id: str
    protective_device_designation: str
    protective_device_rated_current_amperes: float
    protective_device_breaking_capacity_amperes: float
    required_ampacity_multiplier: float
    maximum_voltage_drop_percent: float
    basis_reference: str
    reviewed_by: str
    assumptions: tuple[str, ...]

    def __post_init__(self) -> None:
        _identifier(self.rule_set_id, "adequacy rule-set ID")
        if self.rule_set_version != RULE_SET_VERSION:
            raise CandidateAdequacyError("Unsupported candidate-adequacy rule-set version.")
        _identifier(self.candidate_id, "candidate ID")
        _text(self.protective_device_designation, "protective-device designation", 256)
        _number(self.protective_device_rated_current_amperes, "device rated current", 0.000001)
        _number(self.protective_device_breaking_capacity_amperes, "device breaking capacity", 0.000001)
        _number(self.required_ampacity_multiplier, "required ampacity multiplier", 1.0, 10.0)
        _number(self.maximum_voltage_drop_percent, "maximum voltage drop", 0.000001, 100.0)
        _text(self.basis_reference, "adequacy rule basis reference", 512)
        _text(self.reviewed_by, "adequacy reviewer", 128)
        if not isinstance(self.assumptions, tuple) or not 1 <= len(self.assumptions) <= 16:
            raise CandidateAdequacyError("Adequacy assumptions must contain 1 through 16 entries.")
        for assumption in self.assumptions:
            _text(assumption, "adequacy assumption", 512)


@dataclass(frozen=True, slots=True)
class AdequacyFinding(JsonRecord):
    finding_id: str
    outcome: FindingOutcome
    expression: str
    actual_value: float
    required_value: float
    unit: str

    def __post_init__(self) -> None:
        _identifier(self.finding_id, "adequacy finding ID")
        if not isinstance(self.outcome, FindingOutcome):
            raise CandidateAdequacyError("Adequacy finding outcome is invalid.")
        _text(self.expression, "adequacy finding expression", 512)
        _number(self.actual_value, "adequacy actual value", 0.0)
        _number(self.required_value, "adequacy required value", 0.0)
        _text(self.unit, "adequacy finding unit", 32)


@dataclass(frozen=True, slots=True)
class CandidateAdequacyResult(JsonRecord):
    schema_version: int
    project_id: str
    document_id: str
    context_id: str
    context_evidence_sha256: str
    model_sha256: str
    rule_set: CandidateAdequacyRuleSet
    low_voltage_result_sha256: str
    ampacity_result_sha256: str
    short_circuit_result_sha256: str
    findings: tuple[AdequacyFinding, ...]
    candidate_adequate_under_reviewed_rules: bool
    warnings: tuple[str, ...]
    automatic_selection_performed: bool
    compliance_evaluation_performed: bool
    drawing_mutation_performed: bool
    result_sha256: str

    def __post_init__(self) -> None:
        if self.schema_version != 1:
            raise CandidateAdequacyError("Candidate adequacy result schema must be 1.")
        _identifier(self.project_id, "adequacy result project ID")
        _identifier(self.document_id, "adequacy result document ID")
        if re.fullmatch(r"[0-9a-f]{32}", self.context_id) is None:
            raise CandidateAdequacyError("Candidate adequacy context ID is invalid.")
        for value, name in (
            (self.context_evidence_sha256, "context evidence hash"),
            (self.model_sha256, "model hash"),
            (self.low_voltage_result_sha256, "low-voltage result hash"),
            (self.ampacity_result_sha256, "ampacity result hash"),
            (self.short_circuit_result_sha256, "short-circuit result hash"),
            (self.result_sha256, "result hash"),
        ):
            if _SHA256.fullmatch(value) is None:
                raise CandidateAdequacyError(f"Candidate adequacy {name} must be SHA-256.")
        if not isinstance(self.rule_set, CandidateAdequacyRuleSet):
            raise CandidateAdequacyError("Candidate adequacy rule set is invalid.")
        if len(self.findings) != 5 or any(not isinstance(item, AdequacyFinding) for item in self.findings):
            raise CandidateAdequacyError("Candidate adequacy requires five typed findings.")
        if self.candidate_adequate_under_reviewed_rules != all(
            item.outcome is FindingOutcome.PASS for item in self.findings
        ):
            raise CandidateAdequacyError("Candidate adequacy summary conflicts with findings.")
        if not isinstance(self.warnings, tuple) or len(self.warnings) < 2:
            raise CandidateAdequacyError("Candidate adequacy warnings are incomplete.")
        for warning in self.warnings:
            _text(warning, "candidate adequacy warning", 512)
        if self.automatic_selection_performed or self.compliance_evaluation_performed or self.drawing_mutation_performed:
            raise CandidateAdequacyError("Candidate adequacy cannot claim selection, compliance, or mutation.")
        value = self.to_dict()
        del value["result_sha256"]
        if self.result_sha256 != _canonical_sha256(value):
            raise CandidateAdequacyError("Candidate adequacy result SHA-256 does not match its content.")


def evaluate_candidate_adequacy(
    model: ElectricalProjectModel,
    low_voltage: LowVoltageCalculationResult,
    ampacity: CableAmpacityResult,
    short_circuit: ShortCircuitResult,
    rule_set: CandidateAdequacyRuleSet,
) -> CandidateAdequacyResult:
    """Evaluate one candidate against explicit project rules and verified result objects."""
    if not isinstance(model, ElectricalProjectModel) or not isinstance(rule_set, CandidateAdequacyRuleSet):
        raise CandidateAdequacyError("Candidate adequacy requires a typed model and rule set.")
    if not isinstance(low_voltage, LowVoltageCalculationResult) or not isinstance(
        ampacity, CableAmpacityResult
    ) or not isinstance(short_circuit, ShortCircuitResult):
        raise CandidateAdequacyError("Candidate adequacy requires typed calculation results.")
    model_hash = canonical_model_sha256(model)
    for result, name in (
        (low_voltage, "low-voltage"),
        (ampacity, "ampacity"),
        (short_circuit, "short-circuit"),
    ):
        if (
            result.context_id != model.context_id
            or result.context_evidence_sha256 != model.provenance.evidence_sha256
            or result.model_sha256 != model_hash
        ):
            raise CandidateAdequacyError(f"{name} result does not match the exact model context.")
    route_id = ampacity.input.source_route_id
    if short_circuit.input.source_route_id != route_id or route_id not in low_voltage.input.route_ids:
        raise CandidateAdequacyError("Candidate calculations must reference one consistent route.")

    design_current = low_voltage.calculated_current_amperes
    derated_ampacity = ampacity.derated_ampacity_amperes
    device_rating = rule_set.protective_device_rated_current_amperes
    fault_current = short_circuit.symmetrical_short_circuit_current_amperes
    required_ampacity = _normalized(design_current * rule_set.required_ampacity_multiplier)
    findings = (
        _finding("finding:cable_ampacity", derated_ampacity >= required_ampacity,
                 "Iz_derated >= Ib*required_ampacity_multiplier", derated_ampacity, required_ampacity, "A"),
        _finding("finding:voltage_drop", low_voltage.voltage_drop_percent <= rule_set.maximum_voltage_drop_percent,
                 "voltage_drop_percent <= reviewed_maximum_voltage_drop_percent",
                 low_voltage.voltage_drop_percent, rule_set.maximum_voltage_drop_percent, "%"),
        _finding("finding:device_not_below_load", device_rating >= design_current,
                 "In >= Ib", device_rating, design_current, "A"),
        _finding("finding:device_not_above_cable", device_rating <= derated_ampacity,
                 "In <= Iz_derated", device_rating, derated_ampacity, "A"),
        _finding("finding:breaking_capacity", rule_set.protective_device_breaking_capacity_amperes >= fault_current,
                 "Icu_reviewed >= Ik_rms", rule_set.protective_device_breaking_capacity_amperes,
                 fault_current, "A"),
    )
    payload: dict[str, Any] = {
        "schema_version": 1,
        "project_id": model.project_id,
        "document_id": model.document_id,
        "context_id": model.context_id,
        "context_evidence_sha256": model.provenance.evidence_sha256,
        "model_sha256": model_hash,
        "rule_set": rule_set,
        "low_voltage_result_sha256": low_voltage.result_sha256,
        "ampacity_result_sha256": ampacity.result_sha256,
        "short_circuit_result_sha256": short_circuit.result_sha256,
        "findings": findings,
        "candidate_adequate_under_reviewed_rules": all(
            item.outcome is FindingOutcome.PASS for item in findings
        ),
        "warnings": (
            "PASS means only that this one candidate satisfies the explicit reviewed project inputs in this result; it is not a code-compliance approval.",
            "No product search, automatic cable/protective-device selection, coordination curve, let-through energy, PE sizing, or manufacturer verification is performed.",
        ),
        "automatic_selection_performed": False,
        "compliance_evaluation_performed": False,
        "drawing_mutation_performed": False,
    }
    return CandidateAdequacyResult(**payload, result_sha256=_canonical_sha256(payload))


def _finding(finding_id: str, passed: bool, expression: str, actual: float, required: float, unit: str) -> AdequacyFinding:
    return AdequacyFinding(finding_id, FindingOutcome.PASS if passed else FindingOutcome.FAIL,
                           expression, _normalized(actual), _normalized(required), unit)


def _canonical_sha256(value: object) -> str:
    def convert(item: object) -> object:
        if isinstance(item, Enum): return item.value
        if isinstance(item, JsonRecord): return item.to_dict()
        if isinstance(item, tuple): return [convert(child) for child in item]
        if isinstance(item, dict): return {str(key): convert(child) for key, child in item.items()}
        return item
    return hashlib.sha256(json.dumps(convert(value), ensure_ascii=False, sort_keys=True,
        separators=(",", ":")).encode("utf-8")).hexdigest()


def _normalized(value: float) -> float:
    normalized = round(float(value), 12)
    return 0.0 if normalized == 0.0 else normalized


def _identifier(value: str, name: str) -> None:
    if not isinstance(value, str) or _IDENTIFIER.fullmatch(value) is None:
        raise CandidateAdequacyError(f"{name} must be a bounded lowercase identifier.")


def _text(value: str, name: str, maximum: int) -> None:
    if not isinstance(value, str) or not value or len(value.encode("utf-8")) > maximum:
        raise CandidateAdequacyError(f"{name} must be non-empty and at most {maximum} bytes.")


def _number(value: float, name: str, minimum: float, maximum: float = 1_000_000_000.0) -> None:
    if not isinstance(value, (int, float)) or isinstance(value, bool) or not math.isfinite(value) or not minimum <= float(value) <= maximum:
        raise CandidateAdequacyError(f"{name} is outside its supported numeric range.")
