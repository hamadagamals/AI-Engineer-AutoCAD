from pathlib import Path

import pytest

from integrations.revit_school_lighting_distribution import build_preview


ROOT = Path(__file__).resolve().parents[1]

pytestmark = pytest.mark.skipif(
    not (ROOT / "workspace/revit-school/School-Architectural-readonly-inventory.json").is_file()
    or not (ROOT / "workspace/revit-school/outputs/School-Electrical-Distribution-Preview.xlsx").is_file(),
    reason="Field-project Revit lighting distribution assets are absent in this checkout (gitignored workspace/revit-school).",
)


def test_l4_gym_preview_is_bounded_and_non_executable() -> None:
    preview = build_preview(
        ROOT / "workspace/revit-school/School-Architectural-readonly-inventory.json",
        ROOT / "workspace/revit-school/outputs/School-Electrical-Distribution-Preview.xlsx",
    )

    points = preview["placement"]["points"]
    room = preview["room"]

    assert preview["execution_authorized"] is False
    assert preview["engineer_decision"]["approved_active_power_w"] == 67
    assert preview["calculation"]["required_quantity"] == 45
    assert preview["calculation"]["active_connected_load_w"] == 3015
    assert preview["calculation"]["apparent_load_va"] is None
    assert preview["placement"]["grid"] == "9x5"
    assert len(points) == 45
    assert preview["calculation"]["achieved_lux_lumen_method"] == pytest.approx(306.7)
    assert all(point["mounting_z_mm"] is None for point in points)
    assert all(room["bbox_min"]["x_mm"] < point["x_mm"] < room["bbox_max"]["x_mm"] for point in points)
    assert all(room["bbox_min"]["y_mm"] < point["y_mm"] < room["bbox_max"]["y_mm"] for point in points)
