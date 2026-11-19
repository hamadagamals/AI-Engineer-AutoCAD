from pathlib import Path

from integrations.build_apartment_96sqm_lighting_preview import build, schedule
from integrations.build_apartment_96sqm_privacy_preview import build as build_plan


def test_lumen_method_schedule_meets_provisional_targets(tmp_path: Path):
    plan = build_plan(tmp_path / "plan")
    result = build(Path(plan["output_dxf"]), tmp_path / "lighting")

    assert result["total_luminaires"] == 23
    assert result["total_connected_w"] == 300
    assert result["drawing_mutation_performed"] is False
    assert all(row.achieved_lux >= row.target_lux for row in schedule())
