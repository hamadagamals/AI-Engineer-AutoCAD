"""Tests for the reviewed right-occurrence DB3-GA2 SLD preview."""

from integrations.build_db3_ga2_right_group_sld_preview import build_preview
from integrations.build_panel_sld_preview import render_svg


def _load(handle: str, description: str, watts: float | None, pf: float | None):
    return {
        "handle": handle,
        "description": description,
        "watts": watts,
        "power_factor": pf,
        "va": None if watts is None or pf is None else round(watts / pf, 6),
    }


def test_builds_exact_seven_way_right_group_with_blank_ways_and_partial_seven():
    groups = {
        "2": ("C2", ["B2"]),
        "4": ("C4", ["B4"]),
        "6": ("C6", ["B6", "1A1C2F5B", "1A1C33E2", "1A1C33E3", "1A1C33E4"]),
        "7": ("C7", ["B7A", "B7B"]),
    }
    evidence = {
        "result_sha256": "6d940d635251846b5cd6a565cf21de38c4afb5de9550e7b97173808262ca111f",
        "circuits": [
            {"panel": "DB3-GA2", "circuit": "2", "occurrences": [{"component": "C2", "loads": [_load("B2", "Light", 35, .9)]}]},
            {"panel": "DB3-GA2", "circuit": "4", "occurrences": [{"component": "C4", "loads": [_load("B4", "Fan", 40, .8)]}]},
            {"panel": "DB3-GA2", "circuit": "6", "occurrences": [{"component": "C6", "loads": [_load("B6", "Light", 35, .9)]}]},
            {"panel": "DB3-GA2", "circuit": "7", "occurrences": [{"component": "C7", "loads": [_load("B7A", "L04", None, None), _load("B7B", "LED tape", None, None)]}]},
        ],
    }
    right_group = {
        "preview_sha256": "afa1bb24cfc5b26d7971bfab7f13e471710487f3200ce474ea6f6c4dbee03097",
        "source_drawing_sha256": "1d08ccb0557e740f7d471e006d5accad0675d54b82e45d9aabc64e239ecfb117",
        "absent_circuits": [1, 3, 5],
        "groups": [{"circuit": key, "component": value[0], "block_handles": value[1]} for key, value in groups.items()],
    }
    inventory = {
        "source_drawing_sha256": "1d08ccb0557e740f7d471e006d5accad0675d54b82e45d9aabc64e239ecfb117",
        "inventory_sha256": "a" * 64,
        "entities": [
            {"object_id": f"DUMMY-{index}", "entity_type": "LINE"} for index in range(4844)
        ] + [
            {"object_id": handle, "entity_type": "INSERT", "block_name": "LIGHTING FIXTURE- SPOT_L03_"}
            for handle in sorted({"1A1C2F5B", "1A1C33E2", "1A1C33E3", "1A1C33E4"})
        ],
    }

    preview = build_preview(evidence, right_group, inventory)

    assert [item["observed_in_lighting_drawing"] for item in preview["geometry"]] == [False, True, False, True, False, True, True]
    assert preview["geometry"][5]["connected_w"] == 147.0
    assert preview["geometry"][6]["connected_w"] is None
    assert preview["geometry"][6]["unresolved_load_count"] == 2
    assert "TOTAL TBD (2 loads)" in render_svg(preview)
    assert preview["drawing_mutation_performed"] is False
    assert len(preview["preview_sha256"]) == 64
