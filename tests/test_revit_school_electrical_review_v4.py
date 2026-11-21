"""Source and live-coverage gates for the corrected school review."""

import hashlib
import csv
import json
import math
from pathlib import Path

import pytest


ROOT = Path(__file__).resolve().parents[1]
SCHOOL = ROOT / "workspace" / "revit-school"
REVIEW = SCHOOL / "previews" / "electrical-distribution"
PREVIEW = REVIEW / "School-Electrical-Revit-Review-V4-Preview-20261030.json"
REPORT = REVIEW / "School-Electrical-Review-V4-Live-Report-20261030.json"

pytestmark = pytest.mark.skipif(
    not PREVIEW.is_file() or not REPORT.is_file(),
    reason="Field-project Revit V4 review evidence is absent in this checkout (gitignored workspace/revit-school).",
)


def test_v4_preview_is_bound_to_unchanged_source() -> None:
    preview = json.loads(PREVIEW.read_text(encoding="utf-8"))
    digest = preview.pop("preview_sha256")
    canonical = json.dumps(preview, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
    assert hashlib.sha256(canonical.encode("utf-8")).hexdigest().upper() == digest
    source = SCHOOL / "scratch" / "School-Architectural-electrical-Classroom1500-Sample.rvt"
    assert hashlib.sha256(source.read_bytes()).hexdigest().upper() == preview["source_sha256"]
    assert preview["algorithm_version"] == "balanced-room-rows-coverage-electrical-v4"
    assert len(preview["rooms"]) == 108
    assert sum(row["lighting_count"] for row in preview["rooms"]) == 1152


def test_v4_live_report_has_no_uncovered_room_or_missing_point() -> None:
    preview = json.loads(PREVIEW.read_text(encoding="utf-8"))
    report = json.loads(REPORT.read_text(encoding="utf-8"))
    assert report["preview_sha256"] == preview["preview_sha256"]
    assert report["transaction_committed"] is True
    assert report["saved_as_new_file"] is True
    assert report["room_count"] == 108
    assert report["lighting_point_count"] == 1152
    assert report["exact_duplicate_count"] == 0
    assert report["outside_room_count"] == 0
    assert report["door_clearance_violation_count"] == 0
    assert report["stair_shaft_hit_count"] == 0
    by_id = {row["room_id"]: row for row in report["rooms"]}
    assert len(by_id) == 108
    for spec in preview["rooms"]:
        row = by_id[spec["room_id"]]
        assert row["lighting_placed"] == spec["lighting_count"]
        assert row["lighting_rows"] > 0
        assert row["maximum_coverage_distance_mm"] <= max(
            2500.0,
            1.2 * math.sqrt(row["actual_room_area_m2"] / spec["lighting_count"]) * 1000.0 + 0.1,
        )
    assert by_id[221494]["lighting_placed"] == 45  # GYM 3500


def test_actual_room_area_audit_exposes_bbox_quantity_error() -> None:
    path = REVIEW / "School-Electrical-Review-V4-Actual-Area-Audit-20261030.csv"
    with path.open(encoding="utf-8-sig", newline="") as stream:
        rows = list(csv.DictReader(stream))
    assert len(rows) == 108
    assert sum(int(row["v4_lighting_count"]) for row in rows) == 1152
    assert sum(int(row["indicative_count_if_only_area_changes"]) for row in rows) == 945
    assert sum(row["review_flag"] == "AREA_RATIO_GT_1_5" for row in rows) == 8
    corridor = next(row for row in rows if row["room_name"] == "CORRIDOR 2513")
    assert float(corridor["bbox_to_actual_area_ratio"]) == 3.479
    assert int(corridor["indicative_count_if_only_area_changes"]) == 19
    assert float(corridor["indicative_lux_same_count_actual_area"]) > 350
