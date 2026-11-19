"""Tests for explicit candidate adequacy rules over verified calculation results."""

from __future__ import annotations

import hashlib
from dataclasses import replace

import pytest

from agent.exceptions import CandidateAdequacyError
from core.cable_ampacity import CableAmpacityInput, DeratingFactorInput, calculate_derated_ampacity
from core.candidate_adequacy import CandidateAdequacyRuleSet, FindingOutcome, evaluate_candidate_adequacy
from core.low_voltage_calculations import LowVoltageCalculationInput, PhaseSystem, calculate_low_voltage
from core.short_circuit import FaultArrangement, ShortCircuitInput, calculate_short_circuit
from tests.test_low_voltage_calculations import _model


EVIDENCE = hashlib.sha256(b"lv calculation context").hexdigest()


def _results():
    model = _model(route_length=2.0)
    lv = calculate_low_voltage(model, LowVoltageCalculationInput(
        "calculation:lv", "m20.lv_current_voltage_drop.v1", PhaseSystem.SINGLE_PHASE,
        1000.0, 1.0, 230.0, 0.9, 0.95, 7.41, 0.08,
        ("device:load-1",), ("route:load-1",), "engineer:gamal", ("Reviewed load.",)
    ), context_evidence_sha256=EVIDENCE)
    ampacity = calculate_derated_ampacity(model, CableAmpacityInput(
        "calculation:ampacity", "m20.cable_ampacity_derating.v1", "route:load-1",
        "Reviewed candidate", 20.0, "Reviewed basis.",
        (DeratingFactorInput("factor:ambient", "Ambient", 0.94, "Reviewed ambient."),
         DeratingFactorInput("factor:grouping", "Grouping", 0.8, "Reviewed grouping.")),
        "engineer:gamal", ("Factors apply.",)
    ), context_evidence_sha256=EVIDENCE)
    fault = calculate_short_circuit(model, ShortCircuitInput(
        "calculation:fault", "m20.short_circuit_source_equivalent.v1", "route:load-1",
        FaultArrangement.BALANCED_THREE_PHASE, 400.0, 1.0, 0.01, 0.05, 0.02, 0.04,
        "Reviewed impedance.", "engineer:gamal", ("Bolted fault.",)
    ), context_evidence_sha256=EVIDENCE)
    return model, lv, ampacity, fault


def _rules(**changes: object) -> CandidateAdequacyRuleSet:
    values = dict(rule_set_id="ruleset:candidate", rule_set_version="m20.candidate_adequacy.v1",
        candidate_id="candidate:test", protective_device_designation="10 A / 6 kA candidate",
        protective_device_rated_current_amperes=10.0,
        protective_device_breaking_capacity_amperes=6000.0,
        required_ampacity_multiplier=1.25, maximum_voltage_drop_percent=5.0,
        basis_reference="Reviewed project design rule set.", reviewed_by="engineer:gamal",
        assumptions=("Candidate values are reviewed inputs.",))
    values.update(changes)
    return CandidateAdequacyRuleSet(**values)


def test_candidate_passes_all_explicit_rules_without_selection_or_compliance() -> None:
    model, lv, ampacity, fault = _results()
    result = evaluate_candidate_adequacy(model, lv, ampacity, fault, _rules())
    assert result.candidate_adequate_under_reviewed_rules is True
    assert [item.outcome for item in result.findings] == [FindingOutcome.PASS] * 5
    assert result.automatic_selection_performed is False
    assert result.compliance_evaluation_performed is False
    assert result.drawing_mutation_performed is False
    with pytest.raises(CandidateAdequacyError, match="does not match"):
        replace(result, result_sha256="f" * 64)


def test_candidate_failures_are_independent_and_summary_fails() -> None:
    model, lv, ampacity, fault = _results()
    result = evaluate_candidate_adequacy(model, lv, ampacity, fault, _rules(
        protective_device_rated_current_amperes=30.0,
        protective_device_breaking_capacity_amperes=1000.0,
        required_ampacity_multiplier=4.0,
        maximum_voltage_drop_percent=0.001,
    ))
    assert result.candidate_adequate_under_reviewed_rules is False
    assert sum(item.outcome is FindingOutcome.FAIL for item in result.findings) == 4


def test_candidate_rejects_mismatched_context_and_invalid_rules() -> None:
    model, lv, ampacity, fault = _results()
    with pytest.raises(CandidateAdequacyError, match="exact model context"):
        evaluate_candidate_adequacy(replace(model, warnings=("changed",)), lv, ampacity, fault, _rules())
    with pytest.raises(CandidateAdequacyError, match="multiplier"):
        _rules(required_ampacity_multiplier=0.9)
