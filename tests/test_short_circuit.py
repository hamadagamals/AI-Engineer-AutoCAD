"""Benchmark and rejection tests for the bounded short-circuit boundary."""

from __future__ import annotations

import hashlib
from dataclasses import replace

import pytest

from agent.exceptions import ShortCircuitCalculationError
from core.short_circuit import (
    CALCULATION_VERSION,
    FaultArrangement,
    ShortCircuitInput,
    calculate_short_circuit,
)
from tests.test_low_voltage_calculations import _model


EVIDENCE_SHA256 = hashlib.sha256(b"lv calculation context").hexdigest()


def _input(**changes: object) -> ShortCircuitInput:
    values = {
        "calculation_id": "calculation:short_circuit_test",
        "calculation_version": CALCULATION_VERSION,
        "source_route_id": "route:load-1",
        "fault_arrangement": FaultArrangement.BALANCED_THREE_PHASE,
        "nominal_voltage_volts": 400.0,
        "voltage_multiplier": 1.0,
        "source_resistance_ohms": 0.01,
        "source_reactance_ohms": 0.05,
        "path_resistance_ohms": 0.02,
        "path_reactance_ohms": 0.04,
        "impedance_basis_reference": "Engineer-reviewed source and path impedance schedule.",
        "reviewed_by": "engineer:gamal",
        "assumptions": ("Bolted symmetrical fault at the identified route location.",),
    }
    values.update(changes)
    return ShortCircuitInput(**values)


def test_three_phase_benchmark_is_reproducible_and_source_linked() -> None:
    model = _model()
    result = calculate_short_circuit(
        model, _input(), context_evidence_sha256=EVIDENCE_SHA256
    )
    repeated = calculate_short_circuit(
        model, _input(), context_evidence_sha256=EVIDENCE_SHA256
    )

    assert result.total_resistance_ohms == 0.03
    assert result.total_reactance_ohms == 0.09
    assert result.total_impedance_magnitude_ohms == 0.094868329805
    assert result.symmetrical_short_circuit_current_amperes == 2434.322477802057
    assert result.apparent_fault_power_volt_amperes == 1686548.0854240493
    assert result.route_source.object_id == "B1"
    assert result.protective_device_adequacy_evaluated is False
    assert result.peak_current_calculated is False
    assert result.arc_flash_calculated is False
    assert result.compliance_evaluation_performed is False
    assert result.drawing_mutation_performed is False
    assert result.result_sha256 == repeated.result_sha256
    with pytest.raises(ShortCircuitCalculationError, match="does not match"):
        replace(result, result_sha256="f" * 64)


def test_single_phase_loop_benchmark_uses_line_neutral_voltage() -> None:
    result = calculate_short_circuit(
        _model(),
        _input(
            fault_arrangement=FaultArrangement.SINGLE_PHASE_LOOP,
            nominal_voltage_volts=230.0,
            source_resistance_ohms=0.04,
            source_reactance_ohms=0.06,
            path_resistance_ohms=0.06,
            path_reactance_ohms=0.04,
        ),
        context_evidence_sha256=EVIDENCE_SHA256,
    )
    assert result.total_impedance_magnitude_ohms == 0.141421356237
    assert result.symmetrical_short_circuit_current_amperes == 1626.345596732619
    assert result.apparent_fault_power_volt_amperes == 374059.4872485024
    assert "V_LN" in result.formulas[0]
    assert "complete fault loop" in result.warnings[0]


def test_short_circuit_rejects_zero_impedance_unknown_route_and_changed_evidence() -> None:
    with pytest.raises(ShortCircuitCalculationError, match="impedance must be positive"):
        _input(
            source_resistance_ohms=0.0,
            source_reactance_ohms=0.0,
            path_resistance_ohms=0.0,
            path_reactance_ohms=0.0,
        )
    with pytest.raises(ShortCircuitCalculationError, match="unknown source route"):
        calculate_short_circuit(
            _model(), _input(source_route_id="route:missing"),
            context_evidence_sha256=EVIDENCE_SHA256,
        )
    with pytest.raises(ShortCircuitCalculationError, match="provenance"):
        calculate_short_circuit(_model(), _input(), context_evidence_sha256="f" * 64)


def test_short_circuit_rejects_invalid_version_multiplier_and_assumptions() -> None:
    with pytest.raises(ShortCircuitCalculationError, match="version"):
        _input(calculation_version="m20.short_circuit_source_equivalent.v2")
    with pytest.raises(ShortCircuitCalculationError, match="voltage multiplier"):
        _input(voltage_multiplier=0.0)
    with pytest.raises(ShortCircuitCalculationError, match="assumptions"):
        _input(assumptions=())
