"""Tests for complete paged Revit electrical-system aggregation."""

from __future__ import annotations

from dataclasses import replace

import pytest

from agent.exceptions import RevitIntegrationError
from integrations.revit_electrical_system_inventory import _classify, _merge_pages
from integrations.revit_electrical_system_query import RevitElectricalSystemQuery


def _entry(system_id: int, category: str, *, members: int = 1) -> dict[str, object]:
    return {
        "circuit": {
            "system_id": system_id,
            "system_type": "PowerCircuit",
            "load_name": "Load",
            "circuit_notes": "",
            "panel_name": "P1",
        },
        "load_classifications": "",
        "member_count": members,
        "member_category_counts": (
            [] if members == 0 else [{"category_key": category, "count": members}]
        ),
    }


def _page(
    entries: tuple[dict[str, object], ...], *, start: int = 0, next_after: int = 0,
    truncated: bool = False, total: int | None = None,
) -> RevitElectricalSystemQuery:
    return RevitElectricalSystemQuery(
        request_id=f"request-{start}", document_title="Fixture",
        start_after_element_id=start, next_start_after_element_id=next_after,
        total_system_count=len(entries) if total is None else total,
        returned_system_count=len(entries), truncated=truncated, systems=entries,
    )


def test_merge_pages_and_classify_supported_circuit_groups() -> None:
    lighting = _entry(1, "lighting_fixtures")
    receptacle = _entry(2, "electrical_fixtures")
    feeder = _entry(3, "electrical_equipment")
    empty = _entry(4, "other", members=0)
    first = _page((lighting, receptacle), next_after=2, truncated=True, total=4)
    second = _page((feeder, empty), start=2, total=4)
    merged = _merge_pages((first, second))
    assert [item["circuit"]["system_id"] for item in merged] == [1, 2, 3, 4]
    assert _classify(lighting) == "lighting"
    assert _classify(receptacle) == "receptacles"
    assert _classify(feeder) == "feeder_or_panel"
    assert _classify(empty) == "spare_or_empty"


def test_merge_pages_rejects_gap_duplicate_and_incomplete_chain() -> None:
    first = _page((_entry(1, "lighting_fixtures"),), next_after=1, truncated=True, total=2)
    second = _page((_entry(2, "electrical_fixtures"),), start=1, total=2)
    with pytest.raises(RevitIntegrationError, match="inconsistent"):
        _merge_pages((first, replace(second, start_after_element_id=9)))
    with pytest.raises(RevitIntegrationError, match="duplicate"):
        _merge_pages((first, replace(second, systems=(_entry(1, "electrical_fixtures"),))))
    with pytest.raises(RevitIntegrationError, match="incomplete"):
        _merge_pages((first,))
