from pathlib import Path

import ezdxf

from integrations.build_apartment_96sqm_privacy_preview import build


def test_privacy_plan_is_metric_and_keeps_rooms_on_separate_layers(tmp_path: Path):
    result = build(tmp_path)
    drawing = ezdxf.readfile(result["output_dxf"])

    assert result["gross_area_m2"] == 96.0
    assert result["existing_drawing_mutation_performed"] is False
    assert drawing.header["$INSUNITS"] == 4
    assert len(result["privacy_features"]) == 5
    assert sum(entity.dxftype() == "ARC" for entity in drawing.modelspace()) == 9
