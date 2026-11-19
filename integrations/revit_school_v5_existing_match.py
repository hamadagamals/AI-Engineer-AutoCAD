"""Read-only one-to-one matching of existing school sample fixtures to V5 marks."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import re
import statistics
from pathlib import Path
from typing import Any

from integrations.revit_school_v5_load_readiness import (
    SchoolV5ReadinessError,
    _preview_fingerprint,
    _read_object,
    _workspace_path,
    _write_new,
)


EXPECTED_TITLE = "School-Electrical-Review-V5-20261031"
MARK_ROOM_PATTERN = re.compile(r"^AIENG-(?:L4-GYM|L1-CLASSROOM-1500)-\d{2,3}$")


def _optimal_pairs(fixtures: list[dict[str, Any]], points: list[dict[str, Any]]) -> list[tuple[int, int]]:
    """Return minimum total XY-distance pairs with a deterministic Hungarian solver."""
    count = len(fixtures)
    if count != len(points) or not 1 <= count <= 64:
        raise SchoolV5ReadinessError("Physical and proposed room counts are unequal or unbounded.")
    costs = [
        [math.hypot(f["location_point"]["x_mm"] - p["x_mm"], f["location_point"]["y_mm"] - p["y_mm"])
         for p in points]
        for f in fixtures
    ]
    u = [0.0] * (count + 1)
    v = [0.0] * (count + 1)
    matching = [0] * (count + 1)
    for row in range(1, count + 1):
        matching[0] = row
        column = 0
        minimum = [math.inf] * (count + 1)
        predecessor = [0] * (count + 1)
        used = [False] * (count + 1)
        while True:
            used[column] = True
            current = matching[column]
            delta = math.inf
            next_column = 0
            for candidate in range(1, count + 1):
                if used[candidate]:
                    continue
                reduced = costs[current - 1][candidate - 1] - u[current] - v[candidate]
                if reduced < minimum[candidate]:
                    minimum[candidate] = reduced
                    predecessor[candidate] = column
                if minimum[candidate] < delta:
                    delta = minimum[candidate]
                    next_column = candidate
            for candidate in range(count + 1):
                if used[candidate]:
                    u[matching[candidate]] += delta
                    v[candidate] -= delta
                else:
                    minimum[candidate] -= delta
            column = next_column
            if matching[column] == 0:
                break
        while True:
            prior = predecessor[column]
            matching[column] = matching[prior]
            column = prior
            if column == 0:
                break
    pairs = [(matching[column] - 1, column - 1) for column in range(1, count + 1)]
    return sorted(pairs)


def build_match(preview: dict[str, Any], pages: list[dict[str, Any]]) -> dict[str, Any]:
    """Reconcile exactly 53 existing fixtures; never infer approved relocation."""
    if preview.get("preview_sha256") != _preview_fingerprint(preview):
        raise SchoolV5ReadinessError("Approved V5 preview fingerprint is invalid.")
    if len(pages) != 2:
        raise SchoolV5ReadinessError("Expected both lighting-fixture inventory pages.")
    previous_cursor = 0
    fixtures: list[dict[str, Any]] = []
    for page_index, page in enumerate(pages):
        elements = page.get("elements")
        if (
            page.get("schema_version") != 1
            or page.get("document_title") != EXPECTED_TITLE
            or page.get("category_key") != "lighting_fixtures"
            or page.get("total_count") != 53
            or page.get("start_after_element_id") != previous_cursor
            or page.get("document_mutation_performed") is not False
            or page.get("provider_egress_performed") is not False
            or not isinstance(elements, list)
            or page.get("returned_count") != len(elements)
            or (page_index == 0 and page.get("truncated") is not True)
            or (page_index == 1 and page.get("truncated") is not False)
        ):
            raise SchoolV5ReadinessError("Live lighting inventory pages are invalid or non-progressing.")
        ids = [item.get("element_id") for item in elements if isinstance(item, dict)]
        if len(ids) != len(elements) or ids != sorted(ids) or any(not isinstance(x, int) for x in ids):
            raise SchoolV5ReadinessError("Live fixture IDs are invalid or unsorted.")
        if page_index == 0 and (not ids or page.get("next_start_after_element_id") != ids[-1]):
            raise SchoolV5ReadinessError("First lighting inventory page has no valid continuation.")
        if page_index == 1 and page.get("next_start_after_element_id") != 0:
            raise SchoolV5ReadinessError("Final lighting inventory page has an unexpected continuation.")
        previous_cursor = page.get("next_start_after_element_id")
        fixtures.extend(elements)
    ids = [item["element_id"] for item in fixtures]
    if len(fixtures) != 53 or len(ids) != len(set(ids)):
        raise SchoolV5ReadinessError("Live fixture count or identity does not reconcile.")
    rooms = {room["room_name"]: room for room in preview["rooms"]}
    targets = {"GYM 3500": (45, "L4"), "CLASSROOM 1500": (8, "L1")}
    groups: dict[str, list[dict[str, Any]]] = {name: [] for name in targets}
    for fixture in fixtures:
        mark = fixture.get("parameters", {}).get("mark", "")
        if not MARK_ROOM_PATTERN.fullmatch(mark) or fixture.get("level_name") != "Level 1":
            raise SchoolV5ReadinessError(f"Unreviewed existing fixture identity {fixture.get('element_id')}.")
        room_name = "GYM 3500" if mark.startswith("AIENG-L4-GYM-") else "CLASSROOM 1500"
        if not isinstance(fixture.get("location_point"), dict):
            raise SchoolV5ReadinessError("Existing fixture has no exact LocationPoint.")
        groups[room_name].append(fixture)
    results: list[dict[str, Any]] = []
    matched_point_keys: set[tuple[int, int]] = set()
    room_summaries: list[dict[str, Any]] = []
    for name, (expected, light_type) in targets.items():
        room = rooms.get(name)
        if room is None or room["lighting_count"] != expected or room["luminaire_type"] != light_type:
            raise SchoolV5ReadinessError(f"V5 room definition changed for {name}.")
        proposed = sorted(
            (point for point in preview["v5_expected_points"] if point["room_id"] == room["room_id"]),
            key=lambda point: point["index"],
        )
        existing = sorted(groups[name], key=lambda fixture: fixture["element_id"])
        if (
            len(proposed) != expected or len(existing) != expected
            or any(fixture.get("level_id") != room["level_id"] for fixture in existing)
            or len({fixture.get("type_id") for fixture in existing}) != 1
        ):
            raise SchoolV5ReadinessError(f"Physical/proposed count mismatch for {name}.")
        distances: list[float] = []
        for physical_index, proposed_index in _optimal_pairs(existing, proposed):
            fixture = existing[physical_index]
            point = proposed[proposed_index]
            key = (room["room_id"], point["index"])
            if key in matched_point_keys:
                raise SchoolV5ReadinessError("A proposed point received two physical fixtures.")
            matched_point_keys.add(key)
            distance = math.hypot(
                fixture["location_point"]["x_mm"] - point["x_mm"],
                fixture["location_point"]["y_mm"] - point["y_mm"],
            )
            distances.append(distance)
            results.append({
                "room_id": room["room_id"], "room_name": name,
                "element_id": fixture["element_id"], "mark": fixture["parameters"]["mark"],
                "family_name": fixture["family_name"], "type_name": fixture["type_name"],
                "level_id": fixture["level_id"],
                "existing_x_mm": fixture["location_point"]["x_mm"],
                "existing_y_mm": fixture["location_point"]["y_mm"],
                "existing_z_mm": fixture["location_point"]["z_mm"],
                "proposed_index": point["index"],
                "proposed_x_mm": point["x_mm"], "proposed_y_mm": point["y_mm"],
                "xy_distance_mm": round(distance, 3),
                "within_50_mm": distance <= 50.0,
            })
        room_summaries.append({
            "room_name": name, "room_id": room["room_id"], "physical": expected,
            "proposed": expected, "within_50_mm": sum(d <= 50 for d in distances),
            "over_50_mm": sum(d > 50 for d in distances),
            "median_xy_distance_mm": round(statistics.median(distances), 3),
            "max_xy_distance_mm": round(max(distances), 3),
        })
    if len(matched_point_keys) != 53 or len(preview["v5_expected_points"]) != 947:
        raise SchoolV5ReadinessError("Matched physical fixture count or V5 total changed.")
    return {
        "schema_version": 1, "status": "READ_ONLY_CANDIDATE_MATCH_REVIEW_REQUIRED",
        "document_title": EXPECTED_TITLE, "v5_preview_sha256": preview["preview_sha256"],
        "physical_fixture_count": 53, "proposed_lighting_point_count": 947,
        "candidate_new_positions_after_review": 894,
        "automatic_relocation_authorized": False,
        "automatic_new_placement_authorized": False,
        "match_basis": "known sample marks + same-level minimum-total-XY-distance; 50 mm is a QA flag, not code clearance",
        "rooms": room_summaries, "matches": sorted(results, key=lambda row: row["element_id"]),
        "power_outlet_matching": "BLOCKED: V5 preview has room counts, not exact candidate outlet coordinates",
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--preview", required=True, type=Path)
    parser.add_argument("--page1", required=True, type=Path)
    parser.add_argument("--page2", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    workspace = (Path(__file__).resolve().parents[1] / "workspace").resolve(strict=True)
    preview_path = _workspace_path(args.preview, workspace, must_exist=True)
    page_paths = [
        _workspace_path(args.page1, workspace, must_exist=True),
        _workspace_path(args.page2, workspace, must_exist=True),
    ]
    output = _workspace_path(args.output, workspace, must_exist=False)
    if output.suffix.lower() != ".json":
        raise SchoolV5ReadinessError("Fixture match output must be JSON.")
    result = build_match(_read_object(preview_path), [_read_object(path) for path in page_paths])
    result["input_file_sha256"] = {
        "preview": hashlib.sha256(preview_path.read_bytes()).hexdigest().upper(),
        "page1": hashlib.sha256(page_paths[0].read_bytes()).hexdigest().upper(),
        "page2": hashlib.sha256(page_paths[1].read_bytes()).hexdigest().upper(),
    }
    _write_new(output, json.dumps(result, ensure_ascii=False, indent=2) + "\n")
    print(json.dumps({"rooms": result["rooms"], "candidate_new_positions": 894}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
