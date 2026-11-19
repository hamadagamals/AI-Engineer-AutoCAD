from __future__ import annotations

from dataclasses import replace

import pytest

from agent.exceptions import RevitIntegrationError
from integrations.revit_lighting_circuit_query import RevitLightingCircuitQuery
from integrations.revit_lighting_project_inventory import merge_lighting_circuit_pages


def _circuit() -> dict[str, object]:
    return {
        "system_id": 100,
        "system_type": "PowerCircuit",
        "circuit_number": "5",
        "panel_name": "P109",
        "load_name": "LIGHTING",
        "phase_label": "A",
        "poles": 1,
        "voltage_v": 120.0,
        "apparent_load_va": 100.0,
        "true_load_w": 95.0,
        "power_factor": 0.95,
        "apparent_current_a": 0.833333,
        "rating_a": 20.0,
        "frame_a": 20.0,
        "voltage_drop_percent": 1.2,
        "wire_size": "1-#12, 1-#12, 1-#12",
        "length_mm": 10_000.0,
        "runs": 1,
        "ways": 1,
        "hot_conductors": 1,
        "neutral_conductors": 1,
        "ground_conductors": 1,
        "panel": {
            "element_id": 200,
            "family_name": "Panelboard",
            "type_name": "225 A",
        },
    }


def _fixture(element_id: int) -> dict[str, object]:
    return {
        "element_id": element_id,
        "family_name": "Recessed Lamp",
        "type_name": "LED",
        "circuits": [_circuit()],
    }


def test_merge_complete_pages_deduplicates_circuits_and_calculates_lengths() -> None:
    first = RevitLightingCircuitQuery(
        request_id="page-1",
        document_title="Fixture",
        requested_element_id=0,
        start_after_element_id=0,
        next_start_after_element_id=1,
        total_fixture_count=2,
        returned_fixture_count=1,
        truncated=True,
        fixtures=(_fixture(1),),
    )
    second = RevitLightingCircuitQuery(
        request_id="page-2",
        document_title="Fixture",
        requested_element_id=0,
        start_after_element_id=1,
        next_start_after_element_id=0,
        total_fixture_count=2,
        returned_fixture_count=1,
        truncated=False,
        fixtures=(_fixture(2),),
    )

    report = merge_lighting_circuit_pages((first, second))

    assert report["fixture_count"] == 2
    assert report["unique_circuit_count"] == 1
    assert report["unique_panel_count"] == 1
    assert report["circuits"][0]["fixture_count"] == 2
    assert report["cable_quantities"][0]["total_conductor_length_m"] == 30.0

    with pytest.raises(RevitIntegrationError, match="chain is inconsistent"):
        merge_lighting_circuit_pages((first, replace(second, start_after_element_id=99)))

    with pytest.raises(RevitIntegrationError, match="incomplete"):
        merge_lighting_circuit_pages((first, replace(second, truncated=True)))
