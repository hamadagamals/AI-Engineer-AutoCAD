"""Tests for the registered reusable panel-schedule workbook."""

from pathlib import Path

import pytest

from integrations.panel_schedule_template import (
    PanelScheduleTemplateError,
    build_generation_plan,
    validate_registration,
)


PROJECT_ROOT = Path(__file__).resolve().parents[1]


def test_registered_workbook_identity_and_structure_are_valid() -> None:
    registration = validate_registration(PROJECT_ROOT)

    assert registration.template_id == "panels-sch-8-station"
    assert registration.workbook_sha256 == (
        "1aebe05f922d46aff6a41da816239cdf33ad5730d668671bd8630c88c7182a3e"
    )
    assert [sheet.name for sheet in registration.sheets] == [
        "DB-LCP1",
        "DB-LCP2",
        "DB-LCP3",
        "DB-LCP4",
        "DB-LCP5",
        "DB-LCP6",
        "DB-FOP",
    ]


def test_generation_plan_creates_one_unique_sheet_per_panel() -> None:
    plan = build_generation_plan(
        PROJECT_ROOT,
        ["DB-LCP1", "DB-LCP2", "DB-LCP3", "DB-LCP4", "DB-LCP5", "DB-LCP6"],
    )

    assert len(plan.panels) == 6
    assert [item.output_sheet_name for item in plan.panels] == [
        "DB-LCP1",
        "DB-LCP2",
        "DB-LCP3",
        "DB-LCP4",
        "DB-LCP5",
        "DB-LCP6",
    ]
    assert all(item.exemplar_sheet == "DB-LCP1" for item in plan.panels)
    assert all(item.panel_name_cell == "D3" for item in plan.panels)


@pytest.mark.parametrize("panel_ids", [[""], ["DB/INVALID"], ["DB-1", "db-1"]])
def test_generation_plan_rejects_unsafe_or_duplicate_sheet_names(
    panel_ids: list[str],
) -> None:
    with pytest.raises(PanelScheduleTemplateError):
        build_generation_plan(PROJECT_ROOT, panel_ids)
