"""Tests for deterministic read-only panel-schedule generation."""

from __future__ import annotations

from dataclasses import replace

import pytest

from agent.exceptions import PanelScheduleError
from core.panel_schedule import (
    PanelScheduleFinding,
    ScheduleFindingOutcome,
    generate_panel_schedule,
)
from core.panel_sld import create_panel_sld_contract
from tests.test_panel_sld import EVIDENCE_SHA256, _contract, _model, _records


def test_generated_schedule_is_ordered_traceable_and_read_only() -> None:
    records = _records()
    ways = records["outgoing_ways"]
    protection = records["protective_devices"]
    assert isinstance(ways, tuple)
    assert isinstance(protection, tuple)
    contract = create_panel_sld_contract(
        _model(),
        records["panel"],  # type: ignore[arg-type]
        tuple(reversed(ways)),
        tuple(reversed(protection)),
        records["load_assignments"],  # type: ignore[arg-type]
        records["sld_nodes"],  # type: ignore[arg-type]
        records["sld_edges"],  # type: ignore[arg-type]
        context_evidence_sha256=EVIDENCE_SHA256,
        reviewed_by="engineer:gamal",
        assumptions=("Reviewed.",),
    )

    result = generate_panel_schedule(_model(), contract)

    assert [item.way_number for item in result.rows] == [1, 2]
    assert result.rows[0].circuit_id == "circuit:1"
    assert result.rows[0].load_ids == ("load:1",)
    assert result.rows[0].load_sources[0].object_id == "40"
    assert result.rows[1].kind.value == "spare"
    assert result.rows[1].load_ids == ()
    assert result.outgoing_way_count == 2
    assert result.load_way_count == 1
    assert result.spare_way_count == 1
    assert result.assigned_load_count == 1
    assert result.complete is True
    assert result.findings == ()
    assert result.schedule_generation_performed is True
    assert result.existing_schedule_extraction_performed is False
    assert result.drawing_mutation_performed is False
    assert len(result.result_sha256) == 64


def test_generation_rejects_changed_model_or_tampered_result() -> None:
    contract = _contract()
    with pytest.raises(PanelScheduleError, match="exact model context"):
        generate_panel_schedule(replace(_model(), warnings=("changed",)), contract)

    result = generate_panel_schedule(_model(), contract)
    with pytest.raises(PanelScheduleError, match="summary counts"):
        replace(result, assigned_load_count=2)
    with pytest.raises(PanelScheduleError, match="does not match"):
        replace(result, result_sha256="f" * 64)
    with pytest.raises(PanelScheduleError, match="cannot claim"):
        replace(result, drawing_mutation_performed=True)
    finding = PanelScheduleFinding(
        "finding:missing-way",
        ScheduleFindingOutcome.UNKNOWN,
        "panel:db1",
        "An observed way has no stable circuit identity.",
    )
    with pytest.raises(PanelScheduleError, match="completeness"):
        replace(result, findings=(finding,))
    with pytest.raises(PanelScheduleError, match="must be integers"):
        replace(result, outgoing_way_count=True)
