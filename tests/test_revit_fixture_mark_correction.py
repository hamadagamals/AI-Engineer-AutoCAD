"""Regression checks for the bounded CLASSROOM 1501 Mark correction preview."""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from agent.exceptions import RevitIntegrationError
from integrations.revit_fixture_mark_correction import (
    _preview_hash,
    create_classroom1501_mark_preview,
    submit_approved_mark_correction,
)


ROOT = Path(__file__).resolve().parents[1]
EVIDENCE = ROOT / "workspace/revit-school/previews/electrical-distribution"
SOURCE = ROOT / "workspace/revit-school/scratch/School-Electrical-V5-Classroom1501-Physical-Sample-20261031.rvt"

pytestmark = pytest.mark.skipif(
    not SOURCE.is_file()
    or not (EVIDENCE / "School-V5-Classroom1501-Physical-Category-20261031.json").is_file()
    or not (EVIDENCE / "School-V5-Classroom1501-Physical-Sample-Preview-20261031.json").is_file(),
    reason="Field-project Revit acceptance artifacts are absent in this checkout (gitignored workspace/revit-school).",
)


def _create(tmp_path: Path, category: Path = EVIDENCE / "School-V5-Classroom1501-Physical-Category-20261031.json") -> dict[str, object]:
    return create_classroom1501_mark_preview(
        workspace_root=ROOT, source_model=SOURCE, category_evidence=category,
        original_placement_preview=EVIDENCE / "School-V5-Classroom1501-Physical-Sample-Preview-20261031.json",
        output_model=tmp_path / "corrected.rvt", preview_path=tmp_path / "preview.json",
    )


def test_exact_eight_marks_and_locations_are_source_bound(tmp_path: Path) -> None:
    preview = _create(tmp_path)
    assert preview["source_sha256"] == "66405403F8A34A3F4E444E09E94B5FCD34D78AD67A2625F56756A3658EA8E3AB"
    assert preview["preview_sha256"] == _preview_hash(preview)
    assert [item["element_id"] for item in preview["items"]] == list(range(457165, 457173))
    assert [item["new_mark"] for item in preview["items"]] == [
        f"AIENG-L1-CLASSROOM-1501-{number:02d}" for number in range(1, 9)
    ]
    with pytest.raises(RevitIntegrationError, match="preview"):
        submit_approved_mark_correction(
            workspace_root=ROOT, preview_path=tmp_path / "preview.json", source_model=SOURCE,
            output_model=tmp_path / "corrected.rvt", confirmed_preview_sha256="0" * 64,
            bridge_root=tmp_path / "bridge",
        )
    assert not (tmp_path / "bridge/requests/pending.json").exists()
    preview_path = tmp_path / "preview.json"
    tampered = json.loads(preview_path.read_text(encoding="utf-8"))
    tampered["items"][0]["new_mark"] = "AIENG-L1-CLASSROOM-1501-WRONG"
    preview_path.write_text(json.dumps(tampered), encoding="utf-8")
    with pytest.raises(RevitIntegrationError, match="preview"):
        submit_approved_mark_correction(
            workspace_root=ROOT, preview_path=preview_path, source_model=SOURCE,
            output_model=tmp_path / "corrected.rvt",
            confirmed_preview_sha256=str(preview["preview_sha256"]),
            bridge_root=tmp_path / "bridge",
        )
    assert not (tmp_path / "bridge/requests/pending.json").exists()


def test_wrong_existing_mark_or_position_is_rejected(tmp_path: Path) -> None:
    original = json.loads((EVIDENCE / "School-V5-Classroom1501-Physical-Category-20261031.json").read_text(encoding="utf-8"))
    original["elements"][0]["parameters"]["mark"] = "wrong-room"
    changed = tmp_path / "category.json"
    changed.write_text(json.dumps(original), encoding="utf-8")
    with pytest.raises(RevitIntegrationError, match="item 1"):
        _create(tmp_path, changed)
    original["elements"][0]["parameters"]["mark"] = "AIENG-L1-CLASSROOM-1500-09"
    original["elements"][0]["location_point"]["x_mm"] += 100
    changed.write_text(json.dumps(original), encoding="utf-8")
    with pytest.raises(RevitIntegrationError, match="item 1"):
        _create(tmp_path, changed)
