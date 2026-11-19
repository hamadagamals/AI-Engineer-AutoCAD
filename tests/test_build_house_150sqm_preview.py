"""Tests for the isolated 150 m2 house concept preview."""

from pathlib import Path

import ezdxf

from integrations.build_house_150sqm_preview import build


def test_house_preview_is_metric_bounded_and_reviewable(tmp_path: Path) -> None:
    result = build(tmp_path)
    drawing = ezdxf.readfile(result["output_dxf"])

    assert result["gross_area_m2"] == 150.0
    assert result["external_dimensions_mm"] == [15000, 10000]
    assert result["wall_thickness_mm"] == 200
    assert result["proposed_clear_height_m"] >= 2.7
    assert result["minimum_room_and_opening_checks_pass"] is True
    assert result["existing_drawing_mutation_performed"] is False
    assert len(result["room_schedule"]) == 6
    assert drawing.header["$INSUNITS"] == 4
    assert {"A-WALL", "A-DOOR", "A-WINDOW", "A-TEXT", "A-DIMS", "A-FIXTURE", "A-NOTE"} <= {
        layer.dxf.name for layer in drawing.layers
    }
    assert sum(entity.dxftype() == "ARC" for entity in drawing.modelspace()) == 9
    assert len(drawing.modelspace()) > 80
