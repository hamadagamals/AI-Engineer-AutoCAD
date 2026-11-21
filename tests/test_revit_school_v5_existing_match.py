"""School V5 physical-fixture matching and duplicate-prevention regression."""

from __future__ import annotations

import copy
import json
from pathlib import Path

import pytest

from integrations.revit_school_v5_existing_match import _optimal_pairs, build_match
from integrations.revit_school_v5_load_readiness import SchoolV5ReadinessError
from integrations.revit_preview import load_placement_preview


ROOT = Path(__file__).resolve().parents[1]
EVIDENCE = ROOT / "workspace" / "revit-school" / "previews" / "electrical-distribution"


@pytest.fixture(scope="module")
def evidence() -> tuple[dict, list[dict]]:
    required = (
        "School-Electrical-V5-Execution-Preview-20261031.json",
        "School-V5-Existing-Lighting-Page1-20261031.json",
        "School-V5-Existing-Lighting-Page2-20261031.json",
    )
    if any(not (EVIDENCE / name).is_file() for name in required):
        pytest.skip("Field-project Revit V5 evidence is absent in this checkout (gitignored workspace/revit-school).")
    preview = json.loads((EVIDENCE / required[0]).read_text(encoding="utf-8"))
    pages = [json.loads((EVIDENCE / name).read_text(encoding="utf-8")) for name in required[1:]]
    return preview, pages


def test_optimal_pairs_are_one_to_one_and_choose_lower_total_distance() -> None:
    fixtures = [
        {"location_point": {"x_mm": 0, "y_mm": 0}},
        {"location_point": {"x_mm": 10, "y_mm": 0}},
    ]
    points = [{"x_mm": 9, "y_mm": 0}, {"x_mm": 1, "y_mm": 0}]
    assert _optimal_pairs(fixtures, points) == [(0, 1), (1, 0)]


def test_live_fixtures_match_53_unique_v5_positions(evidence: tuple[dict, list[dict]]) -> None:
    preview, pages = evidence
    result = build_match(preview, pages)
    assert result["physical_fixture_count"] == 53
    assert result["candidate_new_positions_after_review"] == 894
    assert len({(item["room_id"], item["proposed_index"]) for item in result["matches"]}) == 53
    assert [(room["physical"], room["over_50_mm"]) for room in result["rooms"]] == [(45, 30), (8, 8)]
    assert result["automatic_relocation_authorized"] is False


def test_rejects_repeated_page_or_unknown_mark(evidence: tuple[dict, list[dict]]) -> None:
    preview, pages = copy.deepcopy(evidence)
    with pytest.raises(SchoolV5ReadinessError, match="non-progressing"):
        build_match(preview, [pages[0], pages[0]])
    pages[1]["elements"][-1]["parameters"]["mark"] = "UNREVIEWED"
    with pytest.raises(SchoolV5ReadinessError, match="Unreviewed"):
        build_match(preview, pages)


def test_classroom1501_sample_preview_is_exact_and_has_no_existing_fixture(
    evidence: tuple[dict, list[dict]],
) -> None:
    v5, pages = evidence
    sample = load_placement_preview(
        EVIDENCE / "School-V5-Classroom1501-Physical-Sample-Preview-20261031.json",
        workspace_root=ROOT,
    )
    existing = build_match(v5, pages)
    assert not any(row["room_name"] == "CLASSROOM 1501" for row in existing["matches"])
    expected = sorted(
        (point for point in v5["v5_expected_points"] if point["room_id"] == 221095),
        key=lambda point: point["index"],
    )
    assert len(expected) == len(sample["placements"]) == 8
    assert sample["symbol_id"] == 447405
    assert sample["level_id"] == 30
    assert all(
        (placement["x_mm"], placement["y_mm"], placement["z_mm"])
        == (point["x_mm"], point["y_mm"], 2743.2)
        for placement, point in zip(sample["placements"], expected, strict=True)
    )
