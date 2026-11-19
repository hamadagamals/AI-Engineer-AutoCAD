"""Read-only, source-pinned wall/opening check for the first school outlet sample."""

from __future__ import annotations

import json
import math
from pathlib import Path

from agent.exceptions import RevitIntegrationError
from integrations.revit_category_query import load_category_query
from integrations.revit_request_bridge import (
    default_bridge_root, submit_category_request, wait_for_response,
)
from integrations.revit_school_outlet_isolated_test import SOURCE_SHA, SOURCE_TITLE, file_sha256


MARK_ID = 448049
WALL_ID = 195628


def _plan_bbox_distance(x: float, y: float, item: dict) -> float:
    lo, hi = item["bbox_min"], item["bbox_max"]
    return math.hypot(max(lo["x_mm"] - x, 0, x - hi["x_mm"]),
                      max(lo["y_mm"] - y, 0, y - hi["y_mm"]))


def _live_category(category: str) -> list[dict]:
    root = default_bridge_root()
    cursor = 0
    elements: list[dict] = []
    for _ in range(256):
        request_id, response_path = submit_category_request(
            SOURCE_TITLE, category, start_after_element_id=cursor, bridge_root=root,
        )
        response = wait_for_response(request_id, response_path,
                                     expected_action="category_query", timeout_seconds=60)
        if response.status != "completed":
            raise RevitIntegrationError(f"{category} query failed: {response.error}")
        page = load_category_query(root / response.output_file, allowed_root=root)
        if page.request_id != request_id or page.category_key != category:
            raise RevitIntegrationError("Category page identity changed.")
        elements.extend(page.elements)
        if not page.truncated:
            if len(elements) != page.total_count:
                raise RevitIntegrationError("Category page total changed.")
            return elements
        cursor = page.next_start_after_element_id
    raise RevitIntegrationError("Category pagination exceeded bound.")


def audit(source: Path, evidence_dir: Path) -> dict:
    if file_sha256(source) != SOURCE_SHA:
        raise RevitIntegrationError("School source hash changed.")
    marks = json.loads((evidence_dir / "School-V5-Power-Marks-Exact-20261101.json")
                       .read_text(encoding="utf-8"))
    if marks["source_sha256"] != SOURCE_SHA:
        raise RevitIntegrationError("Power marks belong to another source.")
    mark = next(item for item in marks["marks"] if item["first_curve_id"] == MARK_ID)
    if mark["kind"] != "DUPLEX" or mark["level_id"] != 30:
        raise RevitIntegrationError("Sample mark identity changed.")
    wall_page = _live_category("walls")
    wall = next((item for item in wall_page if item["element_id"] == WALL_ID), None)
    if wall is None or wall["level_id"] != 30:
        raise RevitIntegrationError("Sample wall identity changed.")
    x, y = mark["x_mm"], mark["y_mm"]
    doors = _live_category("doors")
    windows = _live_category("windows")
    missing_bbox = [
        {"element_id": item["element_id"], "category": category,
         "host_id": (item.get("host") or {}).get("element_id")}
        for category, items in (("doors", doors), ("windows", windows))
        for item in items if item["level_id"] == 30
        and (item.get("bbox_min") is None or item.get("bbox_max") is None)
    ]
    near = [
        {"element_id": item["element_id"], "category": category,
         "distance_mm": round(_plan_bbox_distance(x, y, item), 1),
         "host_id": (item.get("host") or {}).get("element_id")}
        for category, items in (("doors", doors), ("windows", windows))
        for item in items if item["level_id"] == 30
        and item.get("bbox_min") is not None and item.get("bbox_max") is not None
        if _plan_bbox_distance(x, y, item) < 2000
    ]
    near.sort(key=lambda item: item["distance_mm"])
    result = {"schema_version": 1, "source_sha256": SOURCE_SHA,
              "mark_id": MARK_ID, "mark_room_ids": mark["room_ids"],
              "mark_x_mm": x, "mark_y_mm": y, "wall_id": WALL_ID,
              "wall_bbox": {"min": wall["bbox_min"], "max": wall["bbox_max"]},
              "wall_bbox_distance_mm": round(_plan_bbox_distance(x, y, wall), 1),
              "door_count": len(doors), "window_count": len(windows),
              "opening_missing_bbox_count": len(missing_bbox),
              "opening_missing_bbox": missing_bbox,
              "nearby_openings": near, "document_mutation_performed": False,
              "provider_egress_performed": False}
    if file_sha256(source) != SOURCE_SHA:
        raise RevitIntegrationError("School source hash changed during audit.")
    return result


def audit_all_marks(source: Path, evidence_dir: Path) -> dict:
    """Screen review symbols against live wall/opening boxes before real placement."""
    if file_sha256(source) != SOURCE_SHA:
        raise RevitIntegrationError("School source hash changed.")
    marks = json.loads((evidence_dir / "School-V5-Power-Marks-Exact-20261101.json")
                       .read_text(encoding="utf-8"))
    existing = json.loads((evidence_dir / "School-V5-Existing-Outlets-Current-20261101.json")
                          .read_text(encoding="utf-8"))
    if (marks.get("source_sha256") != SOURCE_SHA or marks.get("total_count") != 527
            or existing.get("total_count") != 16):
        raise RevitIntegrationError("Power mark or existing outlet evidence changed.")
    walls = [item for item in _live_category("walls")
             if item.get("bbox_min") and item.get("bbox_max")
             and item["bbox_max"]["z_mm"] > 1500 and "Foundation" not in item["name"]]
    openings = [item for category in ("doors", "windows")
                for item in _live_category(category)
                if item.get("bbox_min") and item.get("bbox_max")]
    old_outlets = [item for item in existing["elements"] if item.get("location_point")]
    rows: list[dict] = []
    counts: dict[str, int] = {}
    for mark in marks["marks"]:
        if mark["kind"] == "PANEL":
            continue
        x, y, level = mark["x_mm"], mark["y_mm"], mark["level_id"]
        matched_walls = [item for item in walls if item["level_id"] == level]
        if not matched_walls:
            counts["REVIEW_LEVEL_WALL"] = counts.get("REVIEW_LEVEL_WALL", 0) + 1
            rows.append({"mark_id": mark["first_curve_id"], "kind": mark["kind"],
                         "level_id": level, "room_ids": mark["room_ids"],
                         "x_mm": x, "y_mm": y, "nearest_wall_id": None,
                         "wall_bbox_distance_mm": None, "nearest_opening_id": None,
                         "opening_bbox_distance_mm": None,
                         "nearest_existing_outlet_id": None,
                         "existing_outlet_distance_mm": None,
                         "status": "REVIEW_LEVEL_WALL"})
            continue
        wall = min(matched_walls, key=lambda item: _plan_bbox_distance(x, y, item))
        wall_gap = _plan_bbox_distance(x, y, wall)
        opening_gaps = [(_plan_bbox_distance(x, y, item), item["element_id"])
                        for item in openings if item["level_id"] == level]
        opening_gap, opening_id = min(opening_gaps, default=(None, None))
        old_gaps = [(math.hypot(x - item["location_point"]["x_mm"],
                                y - item["location_point"]["y_mm"]), item["element_id"])
                    for item in old_outlets if item["level_id"] == level]
        old_gap, old_id = min(old_gaps, default=(None, None))
        status = ("REVIEW_WALL_DISTANCE" if wall_gap > 300 else
                  "REVIEW_OPENING" if opening_gap is not None and opening_gap < 600 else
                  "REVIEW_EXISTING" if old_gap is not None and old_gap < 1000 else
                  "FACE_CHECK_REQUIRED")
        counts[status] = counts.get(status, 0) + 1
        rows.append({"mark_id": mark["first_curve_id"], "kind": mark["kind"],
                     "level_id": level, "room_ids": mark["room_ids"],
                     "x_mm": x, "y_mm": y, "nearest_wall_id": wall["element_id"],
                     "wall_bbox_distance_mm": round(wall_gap, 1),
                     "nearest_opening_id": opening_id,
                     "opening_bbox_distance_mm": None if opening_gap is None else round(opening_gap, 1),
                     "nearest_existing_outlet_id": old_id,
                     "existing_outlet_distance_mm": None if old_gap is None else round(old_gap, 1),
                     "status": status})
    if len(rows) != 525 or file_sha256(source) != SOURCE_SHA:
        raise RevitIntegrationError("Power host screen is incomplete or source changed.")
    return {"schema_version": 1, "source_sha256": SOURCE_SHA,
            "method": "bbox screening only; wall face, opening geometry, room side and mounting height pending",
            "outlet_mark_count": len(rows), "status_counts": counts,
            "level_wall_count": len(walls), "opening_with_bbox_count": len(openings),
            "rows": rows, "document_mutation_performed": False,
            "provider_egress_performed": False}
