"""Verify the source-bound school electrical review handed off by Revit."""

import hashlib
import json
from collections import Counter
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SCHOOL = ROOT / "workspace" / "revit-school"
REVIEW = SCHOOL / "previews" / "electrical-distribution"


def _read(name: str) -> dict:
    return json.loads((REVIEW / name).read_text(encoding="utf-8"))


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def test_preview_is_bound_to_school_source_and_has_distinct_device_types() -> None:
    preview = _read("School-Electrical-Revit-Review-V3-Preview-20261030.json")
    source = SCHOOL / "scratch" / "School-Architectural-electrical-Classroom1500-Sample.rvt"
    assert preview["source_sha256"] == _sha256(source)
    assert preview["provider_egress_performed"] is False
    assert preview["source_document_mutation_performed"] is False
    rooms = preview["rooms"]
    assert len(rooms) == 108
    assert len({room["room_id"] for room in rooms}) == len(rooms)
    assert {room["luminaire_type"] for room in rooms} == {"L1", "L2", "L3", "L4", "L5", "L6"}
    assert sum(room["lighting_count"] for room in rooms) == 1152
    assert sum(room["new_duplex_count"] for room in rooms) == 426
    assert sum(room["new_gfi_count"] for room in rooms) == 21
    assert sum(room["dedicated_count"] for room in rooms) == 78


def test_live_report_reconciles_every_room_and_detects_no_placement_violation() -> None:
    preview = _read("School-Electrical-Revit-Review-V3-Preview-20261030.json")
    report = _read("School-Electrical-Review-V3-Live-Report-20261030.json")
    assert report["preview_sha256"] == preview["preview_sha256"]
    assert report["source_sha256"] == preview["source_sha256"]
    assert report["transaction_committed"] is True
    assert report["saved_as_new_file"] is True
    assert report["provider_egress_performed"] is False
    assert report["exact_duplicate_count"] == 0
    assert report["outside_room_count"] == 0
    assert report["door_clearance_violation_count"] == 0
    assert report["stair_shaft_hit_count"] == 0
    expected = {room["room_id"]: room for room in preview["rooms"]}
    observed = {room["room_id"]: room for room in report["rooms"]}
    assert len(observed) == len(expected) == 108
    for room_id, row in observed.items():
        spec = expected[room_id]
        assert row["lighting_placed"] == spec["lighting_count"]
        assert row["new_duplex_placed"] == spec["new_duplex_count"]
        assert row["new_gfi_placed"] == spec["new_gfi_count"]
        assert row["dedicated_placed"] == spec["dedicated_count"]
    views = Counter("LIGHTING" if "LIGHTING" in view["view_name"] else "POWER" for view in report["views"])
    assert views == Counter({"LIGHTING": 3, "POWER": 3})
    assert sum(view["point_count"] for view in report["views"]) == 1152 + 426 + 21 + 78 + 16 + 2
