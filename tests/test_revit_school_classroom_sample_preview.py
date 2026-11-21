from datetime import datetime, timedelta
import json
from pathlib import Path

import pytest

from integrations.revit_school_classroom_sample_preview import build_preview, load_preview, write_preview


ROOT = Path(__file__).resolve().parents[1]
BASE = ROOT / "workspace/revit-school"

pytestmark = pytest.mark.skipif(
    not (BASE / "School-Architectural-readonly-inventory.json").is_file()
    or not (BASE / "scratch/School-Architectural-electrical-L4-Complete.rvt").is_file(),
    reason="Field-project Revit school assets are absent in this checkout (gitignored workspace/revit-school).",
)


def _args(tmp_path: Path) -> dict[str, Path]:
    return {
        "workspace_root": ROOT,
        "inventory_path": BASE / "School-Architectural-readonly-inventory.json",
        "source_model_path": BASE / "scratch/School-Architectural-electrical-L4-Complete.rvt",
        "family_path": BASE / "official-library/extracted/CoreLine-Panel-G6-Revit/RC132V G6 29_36_43S_830_40 PSD W60L60 OC.RFA",
        "ies_path": BASE / "official-library/extracted/CoreLine-Panel-G6-IES/RC132V G6 29_36_43S_830_40 PSD W60L60 OC_6.ies",
        "output_model_path": BASE / f"scratch/{tmp_path.name}-Classroom1500-Sample.rvt",
    }


def test_builds_symmetric_eight_fixture_classroom_sample(tmp_path: Path) -> None:
    value = build_preview(**_args(tmp_path))
    points = value["placement"]["points"]
    assert value["room"]["element_id"] == 221093
    assert value["ceiling"]["element_id"] == 278813
    assert value["placement"]["mounting_z_mm"] == 2743.2
    assert len(points) == 8
    assert {(p["grid_column"], p["grid_row"]) for p in points} == {
        (1, 1), (2, 1), (3, 1), (1, 2), (3, 2), (1, 3), (2, 3), (3, 3)
    }
    assert value["calculation"]["achieved_lux_lumen_method"] == pytest.approx(310.8)
    assert value["calculation"]["active_connected_load_w"] == 248
    assert value["review_scope"]["bulk_distribution_authorized"] is False


def test_loader_rejects_tamper_expiry_and_existing_output(tmp_path: Path) -> None:
    value = build_preview(**_args(tmp_path))
    target = BASE / f"test-temp/{tmp_path.name}-classroom-sample.json"
    output = ROOT / value["output"]["model_relative_path"]
    try:
        write_preview(target, value)
        assert load_preview(target, workspace_root=ROOT)["preview_sha256"] == value["preview_sha256"]
        changed = json.loads(target.read_text(encoding="utf-8"))
        changed["placement"]["points"][0]["z_mm"] = 2600
        target.write_text(json.dumps(changed), encoding="utf-8")
        with pytest.raises(ValueError, match="SHA-256"):
            load_preview(target, workspace_root=ROOT)
        target.unlink(); write_preview(target, value)
        with pytest.raises(ValueError, match="expired"):
            load_preview(target, workspace_root=ROOT, now=datetime.fromisoformat(value["expires_utc"]) + timedelta(seconds=1))
        output.write_bytes(b"existing")
        with pytest.raises(ValueError, match="output or points"):
            load_preview(target, workspace_root=ROOT)
    finally:
        target.unlink(missing_ok=True)
        output.unlink(missing_ok=True)


def test_writer_refuses_overwrite(tmp_path: Path) -> None:
    target = tmp_path / "sample.json"
    target.write_text("operator", encoding="utf-8")
    with pytest.raises(ValueError, match="refuses overwrite"):
        write_preview(target, {})
