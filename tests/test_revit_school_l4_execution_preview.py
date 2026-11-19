from datetime import UTC, datetime, timedelta
import json
from pathlib import Path

import pytest

from integrations.revit_school_l4_execution_preview import (
    EXPECTED_FAMILY_SHA256,
    EXPECTED_IES_SHA256,
    EXPECTED_SOURCE_SHA256,
    build_preview,
    load_preview,
    write_preview,
)


ROOT = Path(__file__).resolve().parents[1]
FAMILY = ROOT / "workspace/revit-school/official-library/extracted/CoreLine-Highbay-G5-Revit/REVIT_CL Highbay gen5 complete/63_UN_HighBay_Coreline Highbay BY120P G5 PSU 105-150_Signify.rfa"
IES = ROOT / "workspace/revit-school/official-library/downloads/CoreLine-Highbay-911401629308-IES-20261016.ies"


def _arguments(tmp_path: Path) -> dict[str, Path]:
    return {
        "workspace_root": ROOT,
        "layout_preview_path": ROOT / "workspace/revit-school/previews/electrical-distribution/L4-Gym-Placement-Preview.json",
        "source_model_path": ROOT / "workspace/revit-school/working/School-Architectural-electrical-working.rvt",
        "family_path": FAMILY,
        "ies_path": IES,
        "output_model_path": ROOT / f"workspace/revit-school/scratch/{tmp_path.name}-L4-Batch1.rvt",
    }


def test_builds_bounded_checkerboard_batch(tmp_path: Path) -> None:
    preview = build_preview(**_arguments(tmp_path))

    placements = preview["batch"]["placements"]
    remaining = preview["batch"]["remaining_point_ids_for_batch_2"]
    assert preview["execution_allowed"] is False
    assert preview["source"]["model_sha256"] == EXPECTED_SOURCE_SHA256
    assert preview["family"]["sha256"] == EXPECTED_FAMILY_SHA256
    assert preview["photometry"]["sha256"] == EXPECTED_IES_SHA256
    assert preview["room"]["mounting_z_mm"] == 9500
    assert preview["room"]["clearance_below_room_top_mm"] == pytest.approx(1119.465)
    assert len(placements) == 23
    assert len(remaining) == 22
    assert all((point["grid_column"] + point["grid_row"]) % 2 == 0 for point in placements)
    assert all(point["z_mm"] == 9500 for point in placements)
    assert preview["batch"]["batch_2_source_sha256"] is None
    assert preview["engineering_scope"]["claim_of_code_compliance"] is False
    assert len(preview["preview_sha256"]) == 64


def test_rejects_changed_source_hash(tmp_path: Path) -> None:
    arguments = _arguments(tmp_path)
    changed = tmp_path / "changed.rvt"
    changed.write_bytes(b"not the approved school model")
    arguments["source_model_path"] = changed
    arguments["workspace_root"] = tmp_path
    arguments["layout_preview_path"] = changed
    arguments["family_path"] = changed
    arguments["ies_path"] = changed
    arguments["output_model_path"] = tmp_path / "output.rvt"

    with pytest.raises(ValueError, match="School source model SHA-256 changed"):
        build_preview(**arguments)


def test_preview_writer_refuses_overwrite(tmp_path: Path) -> None:
    target = tmp_path / "preview.json"
    target.write_text("operator file", encoding="utf-8")

    with pytest.raises(ValueError, match="overwrite is not allowed"):
        write_preview(target, {"schema_version": 1})


def test_loader_rejects_tampering_and_expiry(tmp_path: Path) -> None:
    preview = build_preview(**_arguments(tmp_path))
    target = ROOT / f"workspace/revit-school/test-temp/{tmp_path.name}-preview.json"
    try:
        write_preview(target, preview)
        loaded = load_preview(target, workspace_root=ROOT)
        assert loaded["preview_sha256"] == preview["preview_sha256"]

        tampered = json.loads(target.read_text(encoding="utf-8"))
        tampered["room"]["mounting_z_mm"] = 9000
        target.write_text(json.dumps(tampered), encoding="utf-8")
        with pytest.raises(ValueError, match="SHA-256 is invalid"):
            load_preview(target, workspace_root=ROOT)

        target.unlink()
        write_preview(target, preview)
        expires = datetime.fromisoformat(preview["expires_utc"])
        with pytest.raises(ValueError, match="expired"):
            load_preview(target, workspace_root=ROOT, now=expires + timedelta(seconds=1))
    finally:
        target.unlink(missing_ok=True)
