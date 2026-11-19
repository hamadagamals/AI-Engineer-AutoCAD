from pathlib import Path

import ezdxf

from integrations.build_house_150sqm_lighting_preview import build, room_schedule
from integrations.build_house_150sqm_preview import build as build_plan


def test_house_lighting_meets_targets_and_preserves_source(tmp_path: Path):
    plan = build_plan(tmp_path / "plan")
    source = Path(plan["output_dxf"])
    before = source.read_bytes()
    result = build(source, tmp_path / "lighting")
    assert result["source_unchanged"] is True
    assert source.read_bytes() == before
    assert result["all_general_targets_met"] is True
    assert result["all_spacing_checks_pass"] is True
    assert result["general_luminaires"] == 31
    assert result["task_luminaires"] == 3
    assert result["total_luminaires"] == 34
    assert result["total_connected_w"] == 505
    assert result["lighting_power_density_w_m2"] == 3.37
    assert all(room.achieved_lux >= room.target_lux for room in room_schedule())
    drawing = ezdxf.readfile(result["output_dxf"])
    audit = drawing.audit()
    assert not audit.has_errors
    assert len(drawing.modelspace().query("*[layer=='E-LIGHT-GENERAL']")) > 0
    assert len(drawing.modelspace().query("*[layer=='E-LIGHT-TASK']")) > 0
    assert len(drawing.modelspace().query("LINE[layer=='E-LIGHT-TABLE']")) >= 35
    table_text = {
        entity.dxf.text
        for entity in drawing.modelspace().query("TEXT[layer=='E-LIGHT-TABLE-TEXT']")
    }
    assert "ROOM ILLUMINANCE SCHEDULE - MAINTAINED LUX" in table_text
    assert "LUMINAIRE LEGEND AND SPECIFICATION" in table_text
    assert {"L1", "L2", "L3", "T1", "M1"}.issubset(table_text)
    assert {"TARGET lx", "CALC. lx", "W", "lm", "CCT K", "CRI", "IP", "QTY"}.issubset(table_text)
