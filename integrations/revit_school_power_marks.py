"""Bounded read-only paging and reconciliation of V5 power review markers."""

from __future__ import annotations

import hashlib
import json
import math
import os
import time
import uuid
from pathlib import Path

from agent.exceptions import RevitIntegrationError
from integrations.revit_request_bridge import default_bridge_root


EXPECTED = {"DUPLEX": 426, "GFI": 21, "DEDICATED": 78, "PANEL": 2}
EXPECTED_TOTAL = sum(EXPECTED.values())


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def submit_power_mark_request(
    *, title: str, source_model: Path, maximum_elements: int = 32,
    start_after_element_id: int = 0, bridge_root: Path | None = None,
) -> tuple[str, Path]:
    """Submit one source-hash-bound, read-only page request to the live bridge."""
    source = source_model.resolve(strict=True)
    if (not source.is_file() or source.is_symlink() or source.suffix.lower() != ".rvt"
            or title != source.stem or not 1 <= maximum_elements <= 32
            or isinstance(maximum_elements, bool) or start_after_element_id < 0
            or isinstance(start_after_element_id, bool)):
        raise RevitIntegrationError("School V5 power mark request identity is invalid.")
    bridge = (bridge_root or default_bridge_root()).resolve(strict=False)
    pending = bridge / "requests" / "pending.json"
    if pending.exists():
        raise RevitIntegrationError("A Revit bridge request is already pending.")
    request_id = str(uuid.uuid4())
    payload = {
        "schema_version": 1, "request_id": request_id,
        "action": "school_v5_power_mark_query", "expected_document_title": title,
        "provider_egress_permitted": False, "document_mutation_permitted": False,
        "source_sha256": file_sha256(source), "max_elements": maximum_elements,
        "start_after_element_id": start_after_element_id,
    }
    pending.parent.mkdir(parents=True, exist_ok=True)
    temporary = pending.with_name(f".{request_id}.tmp")
    temporary.write_text(json.dumps(payload), encoding="utf-8")
    os.replace(temporary, pending)
    return request_id, bridge / "responses" / f"{request_id}.json"


def load_power_mark_page(path: Path, *, expected_sha256: str, expected_cursor: int) -> dict[str, object]:
    """Reject malformed, truncated, mutated or non-progressing Revit pages."""
    value = json.loads(path.resolve(strict=True).read_text(encoding="utf-8-sig"))
    marks = value.get("marks")
    counts = value.get("counts_by_kind")
    next_cursor = value.get("next_start_after_element_id")
    if (value.get("schema_version") != 1 or value.get("document_sha256") != expected_sha256
            or value.get("total_count") != EXPECTED_TOTAL
            or value.get("start_after_element_id") != expected_cursor
            or counts != EXPECTED or not isinstance(marks, list) or len(marks) > 32
            or value.get("returned_count") != len(marks)
            or value.get("provider_egress_performed") is not False
            or value.get("document_mutation_performed") is not False
            or not isinstance(next_cursor, int) or isinstance(next_cursor, bool)):
        raise RevitIntegrationError("School V5 power mark page contract is invalid.")
    ids: list[int] = []
    for mark in marks:
        if not isinstance(mark, dict):
            raise RevitIntegrationError("School power mark item is invalid.")
        first = mark.get("first_curve_id")
        kind = mark.get("kind")
        segment_ids = mark.get("curve_ids")
        rooms = mark.get("room_ids")
        x, y = mark.get("x_mm"), mark.get("y_mm")
        if (not isinstance(first, int) or isinstance(first, bool) or first <= expected_cursor
                or kind not in EXPECTED or not isinstance(segment_ids, list)
                or len(segment_ids) != {"DUPLEX": 10, "GFI": 6, "DEDICATED": 4, "PANEL": 6}[kind]
                or segment_ids[0] != first or len(set(segment_ids)) != len(segment_ids)
                or not isinstance(rooms, list) or (kind != "PANEL" and len(rooms) != 1)
                or any(not isinstance(room, int) or room <= 0 for room in rooms)
                or not isinstance(x, (int, float)) or not math.isfinite(x)
                or not isinstance(y, (int, float)) or not math.isfinite(y)
                or not isinstance(mark.get("view_id"), int)
                or not isinstance(mark.get("level_id"), int)):
            raise RevitIntegrationError("School power mark item geometry or identity is invalid.")
        ids.append(first)
    if ids != sorted(set(ids)):
        raise RevitIntegrationError("School power mark page did not progress in ID order.")
    if value.get("truncated") is True:
        if not ids or next_cursor != ids[-1]:
            raise RevitIntegrationError("School power mark next cursor is invalid.")
    elif value.get("truncated") is False:
        if next_cursor != 0:
            raise RevitIntegrationError("School power mark final page has a continuation.")
    else:
        raise RevitIntegrationError("School power mark truncation flag is invalid.")
    return value


def reconcile_power_mark_pages(pages: list[dict[str, object]]) -> dict[str, object]:
    """Assemble the complete 527-mark review without duplicate or gap in pagination."""
    if not pages:
        raise RevitIntegrationError("School power mark pages are missing.")
    cursor = 0
    all_marks: list[dict[str, object]] = []
    source_hash = str(pages[0].get("document_sha256", ""))
    for index, page in enumerate(pages):
        if page.get("document_sha256") != source_hash or page.get("start_after_element_id") != cursor:
            raise RevitIntegrationError("School power mark pages changed source or cursor.")
        marks = page.get("marks", [])
        all_marks.extend(marks)
        cursor = page.get("next_start_after_element_id", 0)
        if index < len(pages) - 1 and cursor == 0:
            raise RevitIntegrationError("School power mark pages have an early final page.")
    if cursor != 0 or len(all_marks) != EXPECTED_TOTAL:
        raise RevitIntegrationError("School power mark page set is incomplete.")
    ids = [mark["first_curve_id"] for mark in all_marks]
    counts = {kind: sum(mark["kind"] == kind for mark in all_marks) for kind in EXPECTED}
    if ids != sorted(set(ids)) or counts != EXPECTED:
        raise RevitIntegrationError("School power mark page set has duplicates or wrong totals.")
    return {"schema_version": 1, "source_sha256": source_hash,
            "total_count": len(all_marks), "counts_by_kind": counts, "marks": all_marks,
            "provider_egress_performed": False, "document_mutation_performed": False}


def capture_all_live_power_marks(
    *, source_model: Path, output_report: Path, bridge_root: Path | None = None,
    page_timeout_seconds: float = 60,
) -> dict[str, object]:
    """Capture every page serially; publish only a complete validated report."""
    if not 0 < page_timeout_seconds <= 120:
        raise RevitIntegrationError("School power mark page timeout is invalid.")
    source = source_model.resolve(strict=True)
    expected_hash = file_sha256(source)
    bridge = (bridge_root or default_bridge_root()).resolve(strict=True)
    output = output_report.resolve(strict=False)
    if output.exists() or output.is_symlink() or not output.parent.is_dir():
        raise RevitIntegrationError("School power mark report output must be new.")
    pages: list[dict[str, object]] = []
    request_ids: list[str] = []
    cursor = 0
    for _ in range(17):
        if file_sha256(source) != expected_hash:
            raise RevitIntegrationError("School V5 RVT changed during power mark paging.")
        request_id, response_path = submit_power_mark_request(
            title=source.stem, source_model=source, start_after_element_id=cursor,
            bridge_root=bridge,
        )
        deadline = time.monotonic() + page_timeout_seconds
        while not response_path.is_file():
            if time.monotonic() >= deadline:
                raise RevitIntegrationError("School V5 power mark query timed out.")
            time.sleep(0.1)
        response = json.loads(response_path.read_text(encoding="utf-8-sig"))
        if (response.get("request_id") != request_id
                or response.get("action") != "school_v5_power_mark_query"
                or response.get("status") != "completed"
                or response.get("provider_egress_performed") is not False
                or response.get("document_mutation_performed") is not False):
            raise RevitIntegrationError("School V5 power mark query failed: " + str(response.get("error", "")))
        path = (bridge / str(response.get("output_file", ""))).resolve(strict=True)
        if not path.is_file() or not path.is_relative_to(bridge / "school-power-marks"):
            raise RevitIntegrationError("School V5 power mark response escaped the bridge.")
        page = load_power_mark_page(path, expected_sha256=expected_hash, expected_cursor=cursor)
        if page.get("request_id") != request_id:
            raise RevitIntegrationError("School V5 power mark page request ID changed.")
        pages.append(page)
        request_ids.append(request_id)
        cursor = int(page["next_start_after_element_id"])
        if cursor == 0:
            break
    result = reconcile_power_mark_pages(pages)
    if file_sha256(source) != expected_hash:
        raise RevitIntegrationError("School V5 RVT changed after power mark paging.")
    result["source_file"] = source.name
    result["page_request_ids"] = request_ids
    temporary = output.with_name(f".{uuid.uuid4()}.tmp")
    temporary.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    os.replace(temporary, output)
    return result


def audit_existing_outlet_proximity(
    combined: dict[str, object], outlet_category: dict[str, object],
    *, review_radius_mm: float = 1000.0,
) -> dict[str, object]:
    """Flag possible nearby duplicates without silently deleting any review point."""
    marks = combined.get("marks")
    outlets = outlet_category.get("elements")
    if (combined.get("total_count") != EXPECTED_TOTAL or not isinstance(marks, list)
            or outlet_category.get("category_key") != "electrical_fixtures"
            or outlet_category.get("total_count") != 16
            or outlet_category.get("returned_count") != 16
            or not isinstance(outlets, list) or len(outlets) != 16
            or not 0 < review_radius_mm <= 5000):
        raise RevitIntegrationError("School outlet proximity inputs are incomplete.")
    candidates = [mark for mark in marks if mark["kind"] != "PANEL"]
    results: list[dict[str, object]] = []
    seen: set[int] = set()
    for outlet in outlets:
        element_id = outlet.get("element_id")
        point = outlet.get("location_point")
        level_id = outlet.get("level_id")
        if (not isinstance(element_id, int) or element_id <= 0 or element_id in seen
                or not isinstance(point, dict) or not isinstance(level_id, int)
                or not isinstance(point.get("x_mm"), (int, float))
                or not isinstance(point.get("y_mm"), (int, float))):
            raise RevitIntegrationError("Existing school outlet identity is invalid.")
        seen.add(element_id)
        same_level = [mark for mark in candidates if mark["level_id"] == level_id]
        nearest = min(same_level, key=lambda mark: math.hypot(
            float(mark["x_mm"]) - float(point["x_mm"]),
            float(mark["y_mm"]) - float(point["y_mm"])), default=None)
        distance = (math.hypot(float(nearest["x_mm"]) - float(point["x_mm"]),
                               float(nearest["y_mm"]) - float(point["y_mm"]))
                    if nearest is not None else None)
        results.append({
            "existing_element_id": element_id, "existing_family": outlet.get("family_name"),
            "level_id": level_id,
            "nearest_candidate_curve_id": nearest["first_curve_id"] if nearest else None,
            "nearest_candidate_kind": nearest["kind"] if nearest else None,
            "distance_mm": round(distance, 1) if distance is not None else None,
            "requires_duplicate_review": distance is not None and distance <= review_radius_mm,
        })
    return {
        "schema_version": 1, "source_sha256": combined.get("source_sha256"),
        "existing_outlet_count": len(results), "new_review_candidate_count": len(candidates),
        "review_radius_mm": review_radius_mm,
        "possible_duplicate_count": sum(row["requires_duplicate_review"] for row in results),
        "outlets": results, "automatic_removal_performed": False,
    }
