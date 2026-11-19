"""Golden and rejection tests for deterministic Milestone 20 LV calculations."""

from __future__ import annotations

import hashlib
from dataclasses import replace

import pytest

from agent.exceptions import LowVoltageCalculationError
from core.electrical_model import (
    Confidence,
    Device,
    Drawing,
    DrawingLayer,
    ElectricalProjectModel,
    PrivacyClassification,
    Provenance,
    Route,
    SourceKind,
    SourceReference,
    UnitSystem,
)
from core.low_voltage_calculations import (
    CALCULATION_VERSION,
    LowVoltageCalculationInput,
    PhaseSystem,
    calculate_low_voltage,
)


EVIDENCE_SHA256 = hashlib.sha256(b"lv calculation context").hexdigest()
CONTEXT_ID = "d" * 32
DOCUMENT_ID = "autocad:123456781234123412341234567890ab"


def _source(object_id: str) -> SourceReference:
    return SourceReference("autocad", DOCUMENT_ID, object_id, CONTEXT_ID)


def _model(
    *,
    unit: UnitSystem = UnitSystem.METER,
    route_length: float = 100.0,
) -> ElectricalProjectModel:
    device = Device(
        "device:load-1",
        "socket_single",
        "AIENG_SOCKET",
        "layer:device",
        (0.0, 0.0, 0.0),
        0.0,
        (1.0, 1.0, 1.0),
        (),
        (),
        _source("A1"),
    )
    route = Route(
        "route:load-1",
        "layer:wire",
        ((0.0, 0.0, 0.0), (route_length, 0.0, 0.0)),
        _source("B1"),
    )
    return ElectricalProjectModel(
        schema_version=1,
        project_id="project:test",
        document_id=DOCUMENT_ID,
        context_id=CONTEXT_ID,
        unit_system=unit,
        provenance=Provenance(
            SourceKind.AUTOCAD_SELECTION,
            "2026-08-10T10:00:00Z",
            Confidence.OBSERVED,
            PrivacyClassification.PROJECT_CONFIDENTIAL,
            False,
            EVIDENCE_SHA256,
        ),
        drawings=(
            Drawing(
                DOCUMENT_ID,
                "calculation.dwg",
                unit,
                "Model",
                ("layer:device", "layer:wire"),
                0,
            ),
        ),
        layers=(
            DrawingLayer("layer:device", "E-DEVICE", 2, "CONTINUOUS", False, False, False),
            DrawingLayer("layer:wire", "E-WIRE", 3, "CONTINUOUS", False, False, False),
        ),
        devices=(device,),
        panels=(),
        circuits=(),
        protection_devices=(),
        cables=(),
        conductors=(),
        loads=(),
        routes=(route,),
        unknown_objects=(),
        warnings=(),
    )


def _input(
    phase: PhaseSystem = PhaseSystem.SINGLE_PHASE,
    **changes: object,
) -> LowVoltageCalculationInput:
    values = {
        "calculation_id": "calculation:test",
        "calculation_version": CALCULATION_VERSION,
        "phase_system": phase,
        "load_output_power_watts": 1000.0,
        "demand_factor": 1.0,
        "nominal_voltage_volts": 230.0,
        "power_factor": 0.9,
        "efficiency": 0.95,
        "conductor_ac_resistance_ohm_per_km": 7.41,
        "conductor_reactance_ohm_per_km": 0.08,
        "load_object_ids": ("device:load-1",),
        "route_ids": ("route:load-1",),
        "reviewed_by": "engineer:gamal",
        "assumptions": ("Balanced sinusoidal steady-state load with lagging power factor.",),
    }
    values.update(changes)
    return LowVoltageCalculationInput(**values)


def test_single_phase_golden_vector_is_reproducible_and_source_linked() -> None:
    model = _model()
    calculation_input = _input()

    result = calculate_low_voltage(
        model, calculation_input, context_evidence_sha256=EVIDENCE_SHA256
    )
    repeated = calculate_low_voltage(
        model, calculation_input, context_evidence_sha256=EVIDENCE_SHA256
    )

    assert result.route_length_meters == 100.0
    assert result.connected_output_power_watts == 1000.0
    assert result.demand_output_power_watts == 1000.0
    assert result.apparent_input_power_volt_amperes == 1169.590643274854
    assert result.calculated_current_amperes == 5.085176709891
    assert result.voltage_drop_volts == 6.818073929874
    assert result.voltage_drop_percent == 2.96437996951
    assert result.load_sources[0].object_id == "A1"
    assert result.route_sources[0].object_id == "B1"
    assert result.formulas[0].formula_id == "formula:single_phase_load_current_v1"
    assert result.compliance_evaluation_performed is False
    assert result.drawing_mutation_performed is False
    assert result.result_sha256 == repeated.result_sha256
    with pytest.raises(LowVoltageCalculationError, match="does not match"):
        replace(result, result_sha256="f" * 64)


def test_balanced_three_phase_golden_vector_uses_line_voltage() -> None:
    model = _model(route_length=250.0)
    calculation_input = _input(
        PhaseSystem.BALANCED_THREE_PHASE,
        load_output_power_watts=10000.0,
        demand_factor=0.8,
        nominal_voltage_volts=400.0,
        power_factor=0.85,
        efficiency=0.92,
        conductor_ac_resistance_ohm_per_km=0.641,
    )

    result = calculate_low_voltage(
        model, calculation_input, context_evidence_sha256=EVIDENCE_SHA256
    )

    assert result.demand_output_power_watts == 8000.0
    assert result.calculated_current_amperes == 14.765991539377
    assert result.voltage_drop_volts == 3.753149712349
    assert result.voltage_drop_percent == 0.938287428087
    assert result.formulas[0].formula_id == (
        "formula:balanced_three_phase_load_current_v1"
    )
    assert "line-to-line" in result.warnings[1]


@pytest.mark.parametrize(
    ("unit", "coordinate"),
    [
        (UnitSystem.INCH, 3937.007874015748),
        (UnitSystem.FOOT, 328.0839895013123),
        (UnitSystem.MILLIMETER, 100000.0),
        (UnitSystem.CENTIMETER, 10000.0),
        (UnitSystem.METER, 100.0),
    ],
)
def test_route_geometry_converts_explicit_supported_units(
    unit: UnitSystem, coordinate: float
) -> None:
    result = calculate_low_voltage(
        _model(unit=unit, route_length=coordinate),
        _input(),
        context_evidence_sha256=EVIDENCE_SHA256,
    )

    assert result.route_length_meters == 100.0


def test_calculation_rejects_unknown_sources_routes_units_and_evidence() -> None:
    model = _model()
    with pytest.raises(LowVoltageCalculationError, match="unknown or unsourced"):
        calculate_low_voltage(
            model,
            _input(load_object_ids=("device:missing",)),
            context_evidence_sha256=EVIDENCE_SHA256,
        )
    with pytest.raises(LowVoltageCalculationError, match="unknown route"):
        calculate_low_voltage(
            model,
            _input(route_ids=("route:missing",)),
            context_evidence_sha256=EVIDENCE_SHA256,
        )
    with pytest.raises(LowVoltageCalculationError, match="explicit supported"):
        calculate_low_voltage(
            _model(unit=UnitSystem.UNITLESS),
            _input(),
            context_evidence_sha256=EVIDENCE_SHA256,
        )
    with pytest.raises(LowVoltageCalculationError, match="provenance"):
        calculate_low_voltage(model, _input(), context_evidence_sha256="f" * 64)


def test_calculation_rejects_incomplete_geometry_and_invalid_engineering_inputs() -> None:
    model = _model()
    invalid_route = replace(
        model.routes[0],
        points=((0.0, 0.0, 0.0), (0.0, 0.0, 0.0)),
    )
    with pytest.raises(LowVoltageCalculationError, match="zero-length"):
        calculate_low_voltage(
            replace(model, routes=(invalid_route,)),
            _input(),
            context_evidence_sha256=EVIDENCE_SHA256,
        )
    with pytest.raises(LowVoltageCalculationError, match="power factor"):
        _input(power_factor=0.0)
    with pytest.raises(LowVoltageCalculationError, match="demand factor"):
        _input(demand_factor=1.1)
    with pytest.raises(LowVoltageCalculationError, match="impedance"):
        _input(
            conductor_ac_resistance_ohm_per_km=0.0,
            conductor_reactance_ohm_per_km=0.0,
        )
    with pytest.raises(LowVoltageCalculationError, match="assumptions"):
        _input(assumptions=())
