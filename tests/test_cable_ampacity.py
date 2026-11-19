"""Golden and rejection tests for reviewed cable ampacity derating."""

from __future__ import annotations

import hashlib
from dataclasses import replace

import pytest

from agent.exceptions import CableAmpacityError
from core.cable_ampacity import (
    CALCULATION_VERSION,
    CableAmpacityInput,
    DeratingFactorInput,
    calculate_derated_ampacity,
)
from tests.test_low_voltage_calculations import _model


EVIDENCE_SHA256 = hashlib.sha256(b"lv calculation context").hexdigest()


def _input(**changes: object) -> CableAmpacityInput:
    values = {
        "calculation_id": "calculation:ampacity_test",
        "calculation_version": CALCULATION_VERSION,
        "source_route_id": "route:load-1",
        "cable_designation": "4 mm2 Cu XLPE reviewed candidate",
        "base_ampacity_amperes": 20.0,
        "base_ampacity_basis_reference": "Engineer-reviewed project ampacity schedule row C-01.",
        "derating_factors": (
            DeratingFactorInput(
                "factor:ambient", "Ambient temperature", 0.94,
                "Engineer-reviewed project factor schedule: ambient row.",
            ),
            DeratingFactorInput(
                "factor:grouping", "Circuit grouping", 0.8,
                "Engineer-reviewed project factor schedule: grouping row.",
            ),
        ),
        "reviewed_by": "engineer:gamal",
        "assumptions": ("Factors are applicable to the identified route and cable basis.",),
    }
    values.update(changes)
    return CableAmpacityInput(**values)


def test_ampacity_derating_golden_vector_is_ordered_reproducible_and_source_linked() -> None:
    model = _model()
    result = calculate_derated_ampacity(
        model, _input(), context_evidence_sha256=EVIDENCE_SHA256
    )
    repeated = calculate_derated_ampacity(
        model, _input(), context_evidence_sha256=EVIDENCE_SHA256
    )

    assert result.combined_derating_factor == 0.752
    assert result.derated_ampacity_amperes == 15.04
    assert [(item.sequence, item.input_ampacity_amperes, item.output_ampacity_amperes) for item in result.stages] == [
        (1, 20.0, 18.8), (2, 18.8, 15.04)
    ]
    assert result.route_source.object_id == "B1"
    assert result.cable_selection_performed is False
    assert result.protective_device_selection_performed is False
    assert result.compliance_evaluation_performed is False
    assert result.drawing_mutation_performed is False
    assert result.result_sha256 == repeated.result_sha256
    with pytest.raises(CableAmpacityError, match="does not match"):
        replace(result, result_sha256="f" * 64)


def test_factor_order_is_auditable_and_changes_result_identity() -> None:
    factors = _input().derating_factors
    forward = calculate_derated_ampacity(
        _model(), _input(), context_evidence_sha256=EVIDENCE_SHA256
    )
    reverse = calculate_derated_ampacity(
        _model(), _input(derating_factors=tuple(reversed(factors))),
        context_evidence_sha256=EVIDENCE_SHA256,
    )
    assert reverse.derated_ampacity_amperes == forward.derated_ampacity_amperes
    assert [item.factor_id for item in reverse.stages] == ["factor:grouping", "factor:ambient"]
    assert reverse.result_sha256 != forward.result_sha256


def test_ampacity_rejects_unknown_route_changed_evidence_and_invalid_version() -> None:
    with pytest.raises(CableAmpacityError, match="unknown source route"):
        calculate_derated_ampacity(
            _model(), _input(source_route_id="route:missing"),
            context_evidence_sha256=EVIDENCE_SHA256,
        )
    with pytest.raises(CableAmpacityError, match="provenance"):
        calculate_derated_ampacity(_model(), _input(), context_evidence_sha256="f" * 64)
    with pytest.raises(CableAmpacityError, match="version"):
        _input(calculation_version="m20.cable_ampacity_derating.v2")


def test_ampacity_rejects_uplift_duplicate_or_missing_factor_bases() -> None:
    with pytest.raises(CableAmpacityError, match="derating factor"):
        DeratingFactorInput("factor:uplift", "Unsupported uplift", 1.01, "Reviewed input.")
    duplicate = DeratingFactorInput(
        "factor:ambient", "Duplicate", 0.9, "Another reviewed input."
    )
    with pytest.raises(CableAmpacityError, match="IDs must be unique"):
        _input(derating_factors=(_input().derating_factors[0], duplicate))
    with pytest.raises(CableAmpacityError, match="basis reference"):
        DeratingFactorInput("factor:empty", "Missing basis", 0.9, "")
    with pytest.raises(CableAmpacityError, match="1 through 16"):
        _input(derating_factors=())
