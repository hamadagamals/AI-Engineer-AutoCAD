"""Live Revit evidence for the operator-marked V4 lighting locations."""

import json
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
REVIEW = ROOT / "workspace/revit-school/previews/electrical-distribution"


def test_marked_spot_query_is_bound_to_v4_and_read_only() -> None:
    report = json.loads(
        (REVIEW / "School-V4-Marked-Spots-Revit-Query-20261030.json").read_text(
            encoding="utf-8"
        )
    )
    v4 = json.loads(
        (REVIEW / "School-Electrical-Review-V4-Live-Report-20261030.json").read_text(
            encoding="utf-8"
        )
    )
    assert report["document_sha256"] == v4["output_sha256"]
    assert report["document_mutation_performed"] is False
    assert report["provider_egress_performed"] is False
    spots = {spot["spot_id"]: spot for spot in report["spots"]}
    assert len(spots) == 17
    assert spots["west_stair_upper"]["stair_bbox_ids"]
    assert spots["east_kitchen_edge"]["room_ids"] == []
    assert spots["middle_corridor"]["room_names"] == ["CORRIDOR 2502"]
    assert spots["middle_corridor"]["ceiling_bbox_ids"] == []
    assert spots["south_vestibule"]["ceiling_bbox_ids"] == []
    assert spots["south_corridor"]["ceiling_bbox_ids"] == []


def test_nearest_review_light_uses_the_same_room() -> None:
    report = json.loads(
        (REVIEW / "School-V4-Marked-Spots-Nearest-Light-20261031.json").read_text(
            encoding="utf-8"
        )
    )
    v4 = json.loads(
        (REVIEW / "School-Electrical-Review-V4-Live-Report-20261030.json").read_text(
            encoding="utf-8"
        )
    )
    assert report["document_sha256"] == v4["output_sha256"]
    assert report["document_mutation_performed"] is False
    spots = {spot["spot_id"]: spot for spot in report["spots"]}
    assert len(spots) == 17
    assert spots["west_stair_upper"]["nearest_room_light_stroke_mm"] > 3000
    assert spots["west_stair_lower"]["nearest_room_light_stroke_mm"] < 200
    assert spots["east_kitchen_edge"]["nearest_room_light_stroke_mm"] is None
    assert spots["north_cafeteria"]["nearest_room_light_type"] == "L5-WATERPROOF"


def test_marked_spot_light_strokes_stay_within_their_rooms() -> None:
    report = json.loads(
        (REVIEW / "School-V4-Marked-Spots-Footprint-Audit-20261031.json").read_text(
            encoding="utf-8"
        )
    )
    v4 = json.loads(
        (REVIEW / "School-Electrical-Review-V4-Live-Report-20261030.json").read_text(
            encoding="utf-8"
        )
    )
    assert report["document_sha256"] == v4["output_sha256"]
    assert report["document_mutation_performed"] is False
    assert len(report["spots"]) == 17
    assert all(
        spot["nearby_room_light_stroke_boundary_crossings_3m"] == 0
        for spot in report["spots"]
    )
    assert any(
        spot["nearby_room_light_stroke_count_3m"] > 0
        for spot in report["spots"]
    )

