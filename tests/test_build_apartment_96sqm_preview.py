"""Tests for the isolated apartment concept drawing."""

from pathlib import Path

import ezdxf

from integrations.build_apartment_96sqm_preview import build


def test_apartment_preview_is_metric_96_square_metres_and_editable(tmp_path: Path):
    result = build(tmp_path)
    document = ezdxf.readfile(result["output_dxf"])

    assert result["gross_area_m2"] == 96.0
    assert result["external_dimensions_mm"] == [12000, 8000]
    assert result["existing_drawing_mutation_performed"] is False
    assert document.header["$INSUNITS"] == 4
    assert {"A-WALL", "A-DOOR", "A-WINDOW", "A-TEXT", "A-DIMS", "A-FIXTURE"} <= {
        layer.dxf.name for layer in document.layers
    }
    assert len(document.modelspace()) > 40
