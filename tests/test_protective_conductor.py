"""Tests for reviewed PE/earthing-conductor rule outcomes."""

from __future__ import annotations

import hashlib
from dataclasses import replace

import pytest

from agent.exceptions import ProtectiveConductorSizingError
from core.protective_conductor import (
    DisconnectionBasis,
    ProtectiveConductorRuleInput,
    RuleApplicability,
    SizingOutcome,
    size_protective_conductor,
)
from core.short_circuit import FaultArrangement, ShortCircuitInput, calculate_short_circuit
from tests.test_low_voltage_calculations import _model


EVIDENCE = hashlib.sha256(b"lv calculation context").hexdigest()
SOURCE_HASH = hashlib.sha256(b"reviewed protective conductor rule source").hexdigest()


def _short_circuit(model):
    return calculate_short_circuit(
        model,
        ShortCircuitInput(
            "calculation:pe_fault",
            "m20.short_circuit_source_equivalent.v1",
            "route:load-1",
            FaultArrangement.BALANCED_THREE_PHASE,
            400.0,
            1.0,
            0.01,
            0.05,
            0.02,
            0.04,
            "Reviewed impedance basis.",
            "engineer:gamal",
            ("Bolted symmetrical fault.",),
        ),
        context_evidence_sha256=EVIDENCE,
    )


def _input(**changes: object) -> ProtectiveConductorRuleInput:
    values = dict(
        calculation_id="calculation:pe",
        calculation_version="m20.protective_conductor_adiabatic.v1",
        source_route_id="route:load-1",
        applicability=RuleApplicability.APPLICABLE,
        applicability_basis_reference="Reviewed project applicability decision.",
        disconnection_basis=DisconnectionBasis.REVIEWED_PROTECTIVE_DEVICE_TIME,
        disconnection_basis_reference="Reviewed protective-device clearing time.",
        disconnection_time_seconds=0.2,
        conductor_factor_amperes_sqrt_second_per_square_millimeter=115.0,
        rule_source_id="source:pe_rule",
        rule_source_title="Reviewed PE adiabatic rule source",
        rule_source_version="project-reviewed-2026-08-11",
        rule_source_locator="project://electrical/rules/pe-adiabatic",
        rule_source_sha256=SOURCE_HASH,
        reviewed_by="engineer:gamal",
        assumptions=("Fault duration and conductor factor apply to this route.",),
    )
    values.update(changes)
    return ProtectiveConductorRuleInput(**values)


def test_calculated_outcome_uses_exact_fault_result_and_explicit_units() -> None:
    model = _model(route_length=2.0)
    fault = _short_circuit(model)
    result = size_protective_conductor(
        model, _input(), fault, context_evidence_sha256=EVIDENCE
    )

    assert result.outcome is SizingOutcome.CALCULATED
    assert result.fault_current_amperes == 2434.322477802057
    assert result.required_cross_section_square_millimeters == 9.466627025254
    assert result.short_circuit_result_sha256 == fault.result_sha256
    assert result.cross_section_unit == "mm2"
    assert result.conductor_selection_performed is False
    assert result.compliance_evaluation_performed is False
    assert result.drawing_mutation_performed is False
    with pytest.raises(ProtectiveConductorSizingError, match="does not match"):
        replace(result, result_sha256="f" * 64)


@pytest.mark.parametrize(
    ("applicability", "expected"),
    [
        (RuleApplicability.UNKNOWN, SizingOutcome.UNKNOWN),
        (RuleApplicability.NOT_APPLICABLE, SizingOutcome.NOT_APPLICABLE),
    ],
)
def test_unresolved_outcomes_do_not_emit_calculated_values(
    applicability: RuleApplicability, expected: SizingOutcome
) -> None:
    model = _model(route_length=2.0)
    result = size_protective_conductor(
        model,
        _input(
            applicability=applicability,
            disconnection_basis=DisconnectionBasis.UNKNOWN,
            disconnection_time_seconds=None,
            conductor_factor_amperes_sqrt_second_per_square_millimeter=None,
        ),
        None,
        context_evidence_sha256=EVIDENCE,
    )

    assert result.outcome is expected
    assert result.short_circuit_result_sha256 is None
    assert result.fault_current_amperes is None
    assert result.required_cross_section_square_millimeters is None
    assert result.formula is None


def test_rule_rejects_missing_basis_numbers_and_mismatched_fault_context() -> None:
    model = _model(route_length=2.0)
    with pytest.raises(ProtectiveConductorSizingError, match="disconnection basis"):
        _input(disconnection_basis=DisconnectionBasis.UNKNOWN)
    with pytest.raises(ProtectiveConductorSizingError, match="cannot carry"):
        _input(applicability=RuleApplicability.UNKNOWN)
    with pytest.raises(ProtectiveConductorSizingError, match="exact model context"):
        size_protective_conductor(
            replace(model, warnings=("changed",)),
            _input(),
            _short_circuit(model),
            context_evidence_sha256=EVIDENCE,
        )
