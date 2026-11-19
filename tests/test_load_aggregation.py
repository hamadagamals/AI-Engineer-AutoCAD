"""Golden and rejection tests for Milestone 20 load aggregation."""

from __future__ import annotations

import hashlib
from dataclasses import replace

import pytest

from agent.exceptions import LoadAggregationError
from core.load_aggregation import (
    RULE_SET_VERSION,
    DiversityGroup,
    LoadAggregationRuleSet,
    LoadAllocation,
    LoadPhase,
    aggregate_loads,
)
from tests.test_low_voltage_calculations import CONTEXT_ID, DOCUMENT_ID, _model, _source
from core.electrical_model import Device


EVIDENCE_SHA256 = hashlib.sha256(b"lv calculation context").hexdigest()


def _four_load_model():
    model = _model()
    devices = [model.devices[0]]
    for index, object_id in enumerate(("A2", "A3", "A4"), start=2):
        devices.append(Device(
            f"device:load-{index}", "socket_single", "AIENG_SOCKET", "layer:device",
            (float(index), 0.0, 0.0), 0.0, (1.0, 1.0, 1.0), (), (), _source(object_id),
        ))
    return replace(model, devices=tuple(devices))


def _rule_set(**changes: object) -> LoadAggregationRuleSet:
    values = {
        "rule_set_id": "ruleset:project_test",
        "rule_set_version": RULE_SET_VERSION,
        "allocations": (
            LoadAllocation("device:load-1", 1000.0, 1.0, LoadPhase.L1),
            LoadAllocation("device:load-2", 2000.0, 0.8, LoadPhase.L2),
            LoadAllocation("device:load-3", 3000.0, 0.6, LoadPhase.L3),
            LoadAllocation("device:load-4", 1200.0, 0.5, LoadPhase.THREE_PHASE),
        ),
        "diversity_groups": (
            DiversityGroup("group:general", ("device:load-1", "device:load-2"), 0.75),
            DiversityGroup("group:critical", ("device:load-3", "device:load-4"), 1.0),
        ),
        "reviewed_by": "engineer:gamal",
        "assumptions": ("Reviewed project load schedule and explicit phase allocation.",),
    }
    values.update(changes)
    return LoadAggregationRuleSet(**values)


def test_load_aggregation_golden_vector_is_reproducible_and_source_linked() -> None:
    model = _four_load_model()
    result = aggregate_loads(model, _rule_set(), context_evidence_sha256=EVIDENCE_SHA256)
    repeated = aggregate_loads(model, _rule_set(), context_evidence_sha256=EVIDENCE_SHA256)

    assert result.connected_output_power_watts == 7200.0
    assert result.demand_before_diversity_watts == 5000.0
    assert result.demand_after_diversity_watts == 4350.0
    assert [item.demand_after_diversity_watts for item in result.diversity_group_results] == [1950.0, 2400.0]
    assert [item.demand_output_power_watts for item in result.phase_results] == [950.0, 1400.0, 2000.0]
    assert result.phase_average_demand_watts == 1450.0
    assert result.phase_maximum_spread_watts == 1050.0
    assert result.phase_maximum_deviation_percent == 37.931034482759
    assert [item.object_id for item in result.load_sources] == ["A1", "A2", "A3", "A4"]
    assert result.compliance_evaluation_performed is False
    assert result.drawing_mutation_performed is False
    assert result.result_sha256 == repeated.result_sha256
    with pytest.raises(LoadAggregationError, match="does not match"):
        replace(result, result_sha256="f" * 64)


def test_exactly_balanced_allocations_report_zero_deviation() -> None:
    model = _four_load_model()
    allocations = tuple(
        LoadAllocation(f"device:load-{index}", 900.0, 1.0, phase)
        for index, phase in enumerate((LoadPhase.L1, LoadPhase.L2, LoadPhase.L3), start=1)
    ) + (LoadAllocation("device:load-4", 900.0, 1.0, LoadPhase.THREE_PHASE),)
    result = aggregate_loads(
        model,
        _rule_set(
            allocations=allocations,
            diversity_groups=(DiversityGroup("group:all", tuple(item.load_object_id for item in allocations), 1.0),),
        ),
        context_evidence_sha256=EVIDENCE_SHA256,
    )
    assert [item.demand_output_power_watts for item in result.phase_results] == [1200.0] * 3
    assert result.phase_maximum_spread_watts == 0.0
    assert result.phase_maximum_deviation_percent == 0.0


def test_rule_set_rejects_missing_duplicate_or_unknown_load_membership() -> None:
    allocations = _rule_set().allocations
    with pytest.raises(LoadAggregationError, match="cover every"):
        _rule_set(diversity_groups=(DiversityGroup("group:partial", ("device:load-1",), 1.0),))
    with pytest.raises(LoadAggregationError, match="exactly one diversity"):
        _rule_set(diversity_groups=(
            DiversityGroup("group:a", ("device:load-1", "device:load-2"), 1.0),
            DiversityGroup("group:b", ("device:load-2", "device:load-3", "device:load-4"), 1.0),
        ))
    with pytest.raises(LoadAggregationError, match="exactly one allocation"):
        _rule_set(allocations=allocations + (allocations[0],))
    with pytest.raises(LoadAggregationError, match="unknown or unsourced"):
        aggregate_loads(
            _four_load_model(),
            _rule_set(
                allocations=(replace(allocations[0], load_object_id="device:missing"),) + allocations[1:],
                diversity_groups=(DiversityGroup("group:all", ("device:missing",) + tuple(item.load_object_id for item in allocations[1:]), 1.0),),
            ),
            context_evidence_sha256=EVIDENCE_SHA256,
        )


def test_rule_set_rejects_invalid_factors_version_and_evidence() -> None:
    with pytest.raises(LoadAggregationError, match="demand factor"):
        LoadAllocation("device:load-1", 1.0, 1.1, LoadPhase.L1)
    with pytest.raises(LoadAggregationError, match="diversity factor"):
        DiversityGroup("group:test", ("device:load-1",), 0.0)
    with pytest.raises(LoadAggregationError, match="version"):
        _rule_set(rule_set_version="m20.load_aggregation.v2")
    with pytest.raises(LoadAggregationError, match="provenance"):
        aggregate_loads(_four_load_model(), _rule_set(), context_evidence_sha256="f" * 64)
