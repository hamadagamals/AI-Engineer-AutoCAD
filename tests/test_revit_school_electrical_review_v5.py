"""Integrity and live geometry gates for the school V5 review proposal."""

import hashlib
import json
import xml.etree.ElementTree as ET
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SCHOOL = ROOT / "workspace/revit-school"
REVIEW = SCHOOL / "previews/electrical-distribution"
PREVIEW = REVIEW / "School-Electrical-V5-Execution-Preview-20261031.json"
FEASIBILITY = REVIEW / "School-Electrical-V5-Layout-Feasibility-Geometry-20261031.json"
FINAL_VALIDATION = REVIEW / "School-Electrical-V5-Final-ReadOnly-Validation-20261031.json"
LIVE_HANDOFF = REVIEW / "School-Electrical-V5-Live-Handoff-20261031.json"
SOURCE = SCHOOL / "scratch/School-Architectural-electrical-Classroom1500-Sample.rvt"
OUTPUT_RVT = SCHOOL / "scratch/School-Electrical-Review-V5-20261031.rvt"


def test_v5_execution_preview_is_bound_to_the_source_and_live_geometry() -> None:
    preview = json.loads(PREVIEW.read_text(encoding="utf-8"))
    digest = preview.pop("preview_sha256")
    canonical = json.dumps(preview, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
    assert hashlib.sha256(canonical.encode("utf-8")).hexdigest().upper() == digest
    assert hashlib.sha256(SOURCE.read_bytes()).hexdigest().upper() == preview["source_sha256"]
    assert preview["feasibility_report_sha256"] == hashlib.sha256(
        FEASIBILITY.read_bytes()
    ).hexdigest().upper()
    assert preview["algorithm_version"] == "balanced-room-rows-actual-area-host-review-v5"
    assert preview["execution_allowed"] is True
    assert preview["code_compliance_established"] is False
    assert len(preview["rooms"]) == 108
    assert sum(room["lighting_count"] for room in preview["rooms"]) == 947
    assert len(preview["v5_expected_points"]) == 947
    assert preview["stair_wall_review_candidate"]["mounting_height_mm"] is None
    assert preview["stair_wall_review_candidate"]["luminaire_count"] is None


def test_v5_live_revit_feasibility_has_all_rooms_and_exact_coordinates() -> None:
    preview = json.loads(PREVIEW.read_text(encoding="utf-8"))
    report = json.loads(FEASIBILITY.read_text(encoding="utf-8"))
    assert report["room_count"] == report["feasible_room_count"] == 108
    assert report["feasible_light_count"] == 947
    assert report["document_mutation_performed"] is False
    assert report["source_sha256"] == preview["source_sha256"]
    expected = {
        (point["room_id"], point["index"]): (point["x_mm"], point["y_mm"])
        for point in preview["v5_expected_points"]
    }
    assert len(expected) == 947
    for room in report["rooms"]:
        assert room["feasible"] is True
        assert room["error"] == ""
        assert room["requested_count"] == len(room["points"])
        assert room["minimum_spacing_mm"] >= 600 or len(room["points"]) == 1
        for index, point in enumerate(room["points"]):
            assert expected[(room["room_id"], index)] == (point["x_mm"], point["y_mm"])


def test_final_execution_preview_passed_live_without_mutation() -> None:
    preview = json.loads(PREVIEW.read_text(encoding="utf-8"))
    report = json.loads(FINAL_VALIDATION.read_text(encoding="utf-8"))
    assert report["preview_sha256"] == preview["preview_sha256"]
    assert report["source_sha256"] == preview["source_sha256"]
    assert report["room_count"] == report["feasible_room_count"] == 108
    assert report["feasible_light_count"] == 947
    assert report["document_mutation_performed"] is False
    expected = {
        (point["room_id"], point["index"]): (point["x_mm"], point["y_mm"])
        for point in preview["v5_expected_points"]
    }
    actual = {
        (room["room_id"], index): (point["x_mm"], point["y_mm"])
        for room in report["rooms"]
        for index, point in enumerate(room["points"])
    }
    assert actual == expected


def test_v5_vector_previews_show_all_points_and_separate_stair_marker() -> None:
    namespace = "{http://www.w3.org/2000/svg}"
    counts = []
    for filename in (
        "School-V5-Level1-Lighting-Preview-20261031.svg",
        "School-V5-Level2-Lighting-Preview-20261031.svg",
        "School-V5-Level2-Check-Lighting-Preview-20261031.svg",
    ):
        root = ET.parse(REVIEW / filename).getroot()
        counts.append(len(root.findall(f"{namespace}circle")))
    assert counts == [575, 336, 36]
    stair = ET.parse(REVIEW / "School-V5-West-Stair-Preview-20261031.svg").getroot()
    assert len(stair.findall(f"{namespace}polygon")) == 1


def test_v4_door_and_host_audit_is_read_only_and_complete() -> None:
    report = json.loads(
        (REVIEW / "School-V4-Level1-Lighting-Door-Host-Audit-20261031.json").read_text(
            encoding="utf-8"
        )
    )
    assert report["symbol_stroke_count"] == 3568
    assert report["unassigned_stroke_count"] == 0
    assert report["document_mutation_performed"] is False
    assert sum(room["symbol_stroke_count"] for room in report["rooms"]) == 3568
    assert sum(room["no_ceiling_bbox_strokes"] for room in report["rooms"]) == 1150


def test_v5_live_save_as_is_bound_and_preserves_source() -> None:
    preview = json.loads(PREVIEW.read_text(encoding="utf-8"))
    report = json.loads(LIVE_HANDOFF.read_text(encoding="utf-8"))
    assert report["preview_sha256"] == preview["preview_sha256"]
    assert report["source_sha256"] == hashlib.sha256(SOURCE.read_bytes()).hexdigest().upper()
    assert report["output_sha256"] == hashlib.sha256(OUTPUT_RVT.read_bytes()).hexdigest().upper()
    assert report["transaction_committed"] is True
    assert report["saved_as_new_file"] is True
    assert report["room_count"] == 108
    assert report["lighting_point_count"] == 947
    assert report["mounting_host_review_count"] == 170
    assert report["stair_wall_review_marker_count"] == 1
    assert report["new_duplex_count"] == 426
    assert report["new_gfi_count"] == 21
    assert report["dedicated_count"] == 78
    assert report["exact_duplicate_count"] == 0
    assert report["outside_room_count"] == 0
    assert report["door_clearance_violation_count"] == 0
    assert report["stair_shaft_hit_count"] == 0
    assert len(report["views"]) == 6
    assert sum(view["point_count"] for view in report["views"] if view["level_key"].startswith("LIGHTING")) == 947
    assert sum(room["lighting_placed"] for room in report["rooms"]) == 947
    assert sum(room["mounting_host_review_count"] for room in report["rooms"]) == 170
