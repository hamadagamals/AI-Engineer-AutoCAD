from datetime import datetime, timedelta
import json
from pathlib import Path

import pytest

from integrations.revit_school_l4_batch2_preview import (
    EXPECTED_SOURCE_SHA256,
    build_preview,
    load_preview,
    write_preview,
)


ROOT = Path(__file__).resolve().parents[1]
BASE = ROOT / "workspace/revit-school"
FAMILY = BASE / "official-library/extracted/CoreLine-Highbay-G5-Revit/REVIT_CL Highbay gen5 complete/63_UN_HighBay_Coreline Highbay BY120P G5 PSU 105-150_Signify.rfa"
IES = BASE / "official-library/downloads/CoreLine-Highbay-911401629308-IES-20261016.ies"
HANDOFF = BASE / "scratch/SCHOOL_L4_BATCH1_HANDOFF_592c9a53-1f9c-4160-bc7a-e0a7bd36e5ce.json"
VERIFICATION = BASE / "scratch/SCHOOL_L4_BATCH1_VERIFICATION_e88d158a-c3d8-46b1-8a9d-fca777402734.json"

pytestmark = pytest.mark.skipif(
    not FAMILY.is_file() or not IES.is_file() or not HANDOFF.is_file() or not VERIFICATION.is_file(),
    reason="Field-project Revit L4 Batch 2 assets are absent in this checkout (gitignored workspace/revit-school).",
)


def _arguments(tmp_path: Path) -> dict[str, Path]:
    return {
        "workspace_root": ROOT,
        "layout_preview_path": BASE / "previews/electrical-distribution/L4-Gym-Placement-Preview.json",
        "batch1_source_model_path": BASE / "scratch/School-Architectural-electrical-L4-Batch1.rvt",
        "batch1_handoff_path": HANDOFF,
        "batch1_verification_path": VERIFICATION,
        "family_path": FAMILY,
        "ies_path": IES,
        "output_model_path": BASE / f"scratch/{tmp_path.name}-L4-Complete.rvt",
    }


def test_builds_complementary_verified_batch(tmp_path: Path) -> None:
    preview = build_preview(**_arguments(tmp_path))
    placements = preview["batch"]["placements"]
    assert preview["source"]["model_sha256"] == EXPECTED_SOURCE_SHA256
    assert preview["predecessor"]["existing_instance_count"] == 23
    assert len(preview["predecessor"]["existing_element_ids"]) == 23
    assert len(placements) == 22
    assert all((point["grid_column"] + point["grid_row"]) % 2 == 1 for point in placements)
    assert all(point["z_mm"] == 9500 for point in placements)
    assert preview["batch"]["resulting_total_instance_count"] == 45
    assert preview["execution_allowed"] is False
    assert preview["engineering_scope"]["claim_of_code_compliance"] is False
    assert len(preview["preview_sha256"]) == 64


def test_loader_rejects_tamper_expiry_and_existing_output(tmp_path: Path) -> None:
    preview = build_preview(**_arguments(tmp_path))
    target = BASE / f"test-temp/{tmp_path.name}-batch2-preview.json"
    output = ROOT / preview["output"]["model_relative_path"]
    try:
        write_preview(target, preview)
        assert load_preview(target, workspace_root=ROOT)["preview_sha256"] == preview["preview_sha256"]
        tampered = json.loads(target.read_text(encoding="utf-8"))
        tampered["batch"]["placements"][0]["z_mm"] = 9000
        target.write_text(json.dumps(tampered), encoding="utf-8")
        with pytest.raises(ValueError, match="SHA-256"):
            load_preview(target, workspace_root=ROOT)
        target.unlink()
        write_preview(target, preview)
        expires = datetime.fromisoformat(preview["expires_utc"])
        with pytest.raises(ValueError, match="expired"):
            load_preview(target, workspace_root=ROOT, now=expires + timedelta(seconds=1))
        output.write_bytes(b"operator output")
        with pytest.raises(ValueError, match="output or placement"):
            load_preview(target, workspace_root=ROOT)
    finally:
        target.unlink(missing_ok=True)
        output.unlink(missing_ok=True)


def test_writer_refuses_overwrite(tmp_path: Path) -> None:
    target = tmp_path / "preview.json"
    target.write_text("operator file", encoding="utf-8")
    with pytest.raises(ValueError, match="overwrite"):
        write_preview(target, {"schema_version": 1})
