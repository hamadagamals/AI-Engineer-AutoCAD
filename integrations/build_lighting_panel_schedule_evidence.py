"""Build reviewable lighting panel-schedule evidence from a drawing-wide index.

This module deliberately produces candidate circuit membership.  It never promotes a
spatial match to reviewed electrical connectivity and it never mutates a drawing.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import re
from collections import defaultdict
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable


ROUTE_LAYER = "BLOCK003-G$0$E-Lite-Circ"
TAG_PATTERN = re.compile(
    r"^\s*(?P<circuit>\d+[A-Z]?)\s*/\s*(?P<panel>DB[23]-(?:B|GA[1-4]))\s*$",
    re.IGNORECASE,
)


@dataclass(frozen=True)
class LoadRule:
    """Provisional block-name classification approved for the lighting draft."""

    key: str
    description: str
    watts: float | None
    power_factor: float | None
    source_class: str
    confidence: str


LOAD_RULES = (
    LoadRule("REGTANGULAR SPOT_L01", "Rectangular lighting fixture", 35.0, 0.90, "ESTIMATED", "MEDIUM"),
    LoadRule("SPOT Toilet", "Toilet spot lighting fixture", 28.0, 0.90, "ESTIMATED", "MEDIUM"),
    LoadRule("SPOT_L03", "L03 spot lighting fixture", 28.0, 0.90, "ESTIMATED", "MEDIUM"),
    LoadRule("OUTLET SPOT_L04", "L04 outlet spot lighting fixture", None, None, "UNRESOLVED", "LOW"),
    LoadRule("OUTLET SPOT - Type 1", "Outlet spot lighting fixture", None, None, "UNRESOLVED", "LOW"),
    LoadRule("PENDANT _L05", "Pendant lighting fixture", 75.0, 0.90, "ESTIMATED", "LOW"),
    LoadRule("Propeller Fan NEW", "Extract fan", 40.0, 0.80, "ESTIMATED-EXTERNAL-BASIS", "LOW"),
    LoadRule("2X28W", "2 x 28 W lighting fixture", 56.0, 0.90, "DRAWING-NAME", "HIGH"),
    LoadRule("LED Tapes", "LED tape segment", None, None, "UNRESOLVED-LENGTH", "LOW"),
    LoadRule("Downlight-SIMES", "Stage downlight", None, None, "UNRESOLVED", "LOW"),
)

CONTROL_MARKERS = ("LITE-SWCH", "MOTION DETECTOR", "PANEL", "KWH METER")


def _canonical_json(value: Any) -> bytes:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")


def _sha256(value: Any) -> str:
    return hashlib.sha256(_canonical_json(value)).hexdigest()


def _xy(point: Iterable[float]) -> tuple[float, float]:
    values = list(point)
    return float(values[0]), float(values[1])


def _distance_point_segment(
    point: tuple[float, float], start: tuple[float, float], end: tuple[float, float]
) -> float:
    px, py = point
    ax, ay = start
    bx, by = end
    dx, dy = bx - ax, by - ay
    if dx == 0.0 and dy == 0.0:
        return math.hypot(px - ax, py - ay)
    fraction = max(0.0, min(1.0, ((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy)))
    return math.hypot(px - (ax + fraction * dx), py - (ay + fraction * dy))


def _distance_point_polyline(point: tuple[float, float], points: list[tuple[float, float]]) -> float:
    if not points:
        return math.inf
    if len(points) == 1:
        return math.dist(point, points[0])
    return min(_distance_point_segment(point, start, end) for start, end in zip(points, points[1:]))


class _UnionFind:
    def __init__(self, values: Iterable[str]) -> None:
        self.parent = {value: value for value in values}

    def find(self, value: str) -> str:
        parent = self.parent[value]
        if parent != value:
            self.parent[value] = self.find(parent)
        return self.parent[value]

    def union(self, left: str, right: str) -> None:
        left_root, right_root = self.find(left), self.find(right)
        if left_root != right_root:
            self.parent[right_root] = left_root


def _classify_block(name: str) -> tuple[str, LoadRule | None]:
    if any(marker.casefold() in name.casefold() for marker in CONTROL_MARKERS):
        return "control", None
    for rule in LOAD_RULES:
        if rule.key.casefold() in name.casefold():
            return "load", rule
    return "other", None


def _summarize_component_blocks(
    block_ids: Iterable[str], blocks: dict[str, dict[str, Any]]
) -> dict[str, Any]:
    classified: list[dict[str, Any]] = []
    connected_watts = 0.0
    connected_va = 0.0
    unresolved_loads = 0
    control_count = 0
    for block_id in sorted(block_ids):
        block_name = str(blocks[block_id].get("block_name") or "")
        category, rule = _classify_block(block_name)
        if category == "control":
            control_count += 1
            continue
        if category != "load" or rule is None:
            continue
        watts = rule.watts
        va = watts / rule.power_factor if watts is not None and rule.power_factor else None
        if watts is None:
            unresolved_loads += 1
        else:
            connected_watts += watts
            connected_va += va or 0.0
        classified.append(
            {
                "handle": block_id,
                "description": rule.description,
                "watts": watts,
                "power_factor": rule.power_factor,
                "va": round(va, 6) if va is not None else None,
                "source_class": rule.source_class,
                "confidence": rule.confidence,
            }
        )
    signature = sorted(
        (
            load["description"],
            load["watts"],
            load["power_factor"],
            load["source_class"],
            load["confidence"],
        )
        for load in classified
    )
    return {
        "candidate_block_count": len(set(block_ids)),
        "classified_load_count": len(classified),
        "control_count": control_count,
        "unresolved_load_count": unresolved_loads,
        "known_partial_w": round(connected_watts, 6) if classified else None,
        "known_partial_va": round(connected_va, 6) if classified else None,
        "provisional_connected_w": round(connected_watts, 6) if classified and not unresolved_loads else None,
        "provisional_connected_va": round(connected_va, 6) if classified and not unresolved_loads else None,
        "provisional_current_a_at_230v": (
            round(connected_va / 230.0, 6) if classified and not unresolved_loads else None
        ),
        "loads": classified,
        "signature": signature,
    }


def _schedule_panels(circuit_rows: list[dict[str, Any]]) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    """Create numeric way rows; gaps stay truly blank and are never called spares."""

    by_panel: dict[str, dict[int, dict[str, Any]]] = defaultdict(dict)
    review_items: list[dict[str, Any]] = []
    for circuit in circuit_rows:
        if not circuit["circuit"].isdigit():
            review_items.append(
                {
                    "panel": circuit["panel"],
                    "reference": circuit["circuit"],
                    "reason": "Non-numeric circuit reference cannot be assigned to a panel way automatically.",
                }
            )
            continue
        by_panel[circuit["panel"]][int(circuit["circuit"])] = circuit

    panels: list[dict[str, Any]] = []
    for panel in sorted(by_panel):
        observed = by_panel[panel]
        maximum_way = max(observed)
        ways: list[dict[str, Any]] = []
        for way in range(1, maximum_way + 1):
            circuit = observed.get(way)
            if circuit is None:
                ways.append(
                    {
                        "way": way,
                        "observed_in_lighting_drawing": False,
                        "description": None,
                        "phase": None,
                        "connected_w": None,
                        "connected_va": None,
                        "current_a": None,
                        "status": "BLANK-NOT-OBSERVED-IN-LIGHTING-DRAWING",
                    }
                )
                continue
            descriptions: dict[str, int] = defaultdict(int)
            for load in circuit["loads"]:
                descriptions[load["description"]] += 1
            description = "; ".join(
                f"{count} x {name}" for name, count in sorted(descriptions.items())
            ) or None
            ways.append(
                {
                    "way": way,
                    "observed_in_lighting_drawing": True,
                    "description": description,
                    "phase": None,
                    "connected_w": circuit["provisional_connected_w"],
                    "connected_va": circuit["provisional_connected_va"],
                    "current_a": circuit["provisional_current_a_at_230v"],
                    "known_partial_w": circuit["known_partial_w"],
                    "known_partial_va": circuit["known_partial_va"],
                    "unresolved_load_count": circuit["unresolved_load_count"],
                    "membership_status": circuit["membership_status"],
                    "status": (
                        "REVIEW-CONFLICT"
                        if circuit["occurrence_conflict"]
                        else "REVIEW-INCOMPLETE-LOADS"
                        if circuit["unresolved_load_count"]
                        else "DRAFT-ESTIMATED-CANDIDATE"
                    ),
                }
            )
        panels.append(
            {
                "panel": panel,
                "schedule_row_basis": "1 through highest numeric circuit reference observed in this drawing",
                "rated_ways": None,
                "system": "400/230 V, 3 Phase, 4 Wire",
                "frequency_hz": None,
                "ways": ways,
            }
        )
    return panels, review_items


def load_index_entities(autocad_root: Path, index_id: str) -> tuple[dict[str, Any], list[dict[str, Any]]]:
    """Load and independently validate every context named by an index manifest."""

    manifest_path = autocad_root / "drawing-index" / "inbox" / f"{index_id}.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    entities: list[dict[str, Any]] = []
    for chunk in manifest["chunks"]:
        context_path = autocad_root / "context" / "inbox" / f"{chunk['context_id']}.json"
        context = json.loads(context_path.read_text(encoding="utf-8"))
        if len(context["entities"]) != int(chunk["entity_count"]):
            raise ValueError(f"Context entity count differs: {context_path}")
        entities.extend(context["entities"])
    if len(entities) != int(manifest["entity_count"]):
        raise ValueError("Drawing index entity total differs from its chunks")
    handles = [entity["object_id"] for entity in entities]
    if len(handles) != len(set(handles)):
        raise ValueError("Drawing index contains duplicate object handles")
    return manifest, entities


def build_candidate_evidence(
    manifest: dict[str, Any],
    entities: list[dict[str, Any]],
    *,
    endpoint_tolerance: float = 2.0,
    device_snap_tolerance: float = 250.0,
    tag_snap_tolerance: float = 1200.0,
) -> dict[str, Any]:
    """Return deterministic candidate memberships and provisional connected loads."""

    routes = {
        entity["object_id"]: [_xy(point) for point in entity.get("points", [])]
        for entity in entities
        if entity.get("layer") == ROUTE_LAYER
        and entity.get("entity_type") in {"LINE", "LWPOLYLINE", "POLYLINE"}
        and len(entity.get("points", [])) >= 2
    }
    blocks = {
        entity["object_id"]: entity
        for entity in entities
        if entity.get("entity_type") == "INSERT" and entity.get("points")
    }
    tags: list[tuple[dict[str, Any], re.Match[str]]] = []
    for entity in entities:
        text = entity.get("text")
        if not isinstance(text, str):
            continue
        match = TAG_PATTERN.match(text)
        if match:
            tags.append((entity, match))

    union = _UnionFind(routes)
    route_items = list(routes.items())
    for index, (left_id, left_points) in enumerate(route_items):
        left_endpoints = (left_points[0], left_points[-1])
        for right_id, right_points in route_items[index + 1 :]:
            right_endpoints = (right_points[0], right_points[-1])
            if min(math.dist(left, right) for left in left_endpoints for right in right_endpoints) <= endpoint_tolerance:
                union.union(left_id, right_id)

    block_routes: dict[str, list[tuple[str, float]]] = {}
    for block_id, block in blocks.items():
        point = _xy(block["points"][0])
        nearby = [
            (route_id, _distance_point_polyline(point, points))
            for route_id, points in route_items
        ]
        nearby = [(route_id, distance) for route_id, distance in nearby if distance <= device_snap_tolerance]
        nearby.sort(key=lambda item: (item[1], item[0]))
        block_routes[block_id] = nearby
        for route_id, _distance in nearby[1:]:
            union.union(nearby[0][0], route_id)

    components: dict[str, set[str]] = defaultdict(set)
    for route_id in routes:
        components[union.find(route_id)].add(route_id)

    component_blocks: dict[str, set[str]] = defaultdict(set)
    for block_id, nearby in block_routes.items():
        if nearby:
            component_blocks[union.find(nearby[0][0])].add(block_id)

    circuit_components: dict[tuple[str, str], set[str]] = defaultdict(set)
    tag_observations: list[dict[str, Any]] = []
    component_route_points = {
        root: [routes[route_id] for route_id in route_ids]
        for root, route_ids in components.items()
    }
    for tag, match in tags:
        tag_point = _xy(tag["points"][0])
        component_distances = sorted(
            (
                min(_distance_point_polyline(tag_point, points) for points in polylines),
                root,
            )
            for root, polylines in component_route_points.items()
        )
        eligible_with_blocks = [
            item
            for item in component_distances
            if item[0] <= tag_snap_tolerance and component_blocks[item[1]]
        ]
        distance, component = (
            eligible_with_blocks[0]
            if eligible_with_blocks
            else component_distances[0]
        )
        if distance > tag_snap_tolerance:
            component = None
        route_id = min(
            components[component],
            key=lambda candidate: (
                _distance_point_polyline(tag_point, routes[candidate]),
                candidate,
            ),
        ) if component else min(
            routes,
            key=lambda candidate: (
                _distance_point_polyline(tag_point, routes[candidate]),
                candidate,
            ),
        )
        panel = match.group("panel").upper()
        circuit = match.group("circuit").upper()
        if component:
            circuit_components[(panel, circuit)].add(component)
        tag_observations.append(
            {
                "handle": tag["object_id"],
                "panel": panel,
                "circuit": circuit,
                "nearest_route_handle": route_id,
                "distance_to_route": round(distance, 6),
                "candidate_component": component,
            }
        )

    circuit_rows: list[dict[str, Any]] = []
    for panel, circuit in sorted(circuit_components, key=lambda item: (item[0], int(re.match(r"\d+", item[1]).group()))):
        roots = circuit_components[(panel, circuit)]
        occurrences = []
        for root in sorted(roots):
            summary = _summarize_component_blocks(component_blocks[root], blocks)
            occurrences.append(
                {
                    "component": root,
                    "candidate_route_count": len(components[root]),
                    **summary,
                }
            )
        nonempty = [occurrence for occurrence in occurrences if occurrence["classified_load_count"]]
        signatures = {_canonical_json(occurrence["signature"]) for occurrence in nonempty}
        duplicate_equivalent = len(nonempty) > 1 and len(signatures) == 1
        conflicting = len(signatures) > 1
        representative = nonempty[0] if nonempty and not conflicting else None
        circuit_rows.append(
            {
                "panel": panel,
                "circuit": circuit,
                "candidate_occurrence_count": len(occurrences),
                "load_bearing_occurrence_count": len(nonempty),
                "duplicate_equivalent": duplicate_equivalent,
                "occurrence_conflict": conflicting,
                "candidate_route_count": representative["candidate_route_count"] if representative else None,
                "candidate_block_count": representative["candidate_block_count"] if representative else None,
                "classified_load_count": representative["classified_load_count"] if representative else None,
                "control_count": representative["control_count"] if representative else None,
                "unresolved_load_count": representative["unresolved_load_count"] if representative else None,
                "known_partial_w": representative["known_partial_w"] if representative else None,
                "known_partial_va": representative["known_partial_va"] if representative else None,
                "provisional_connected_w": representative["provisional_connected_w"] if representative else None,
                "provisional_connected_va": representative["provisional_connected_va"] if representative else None,
                "provisional_current_a_at_230v": representative["provisional_current_a_at_230v"] if representative else None,
                "membership_status": (
                    "CONFLICT-OBSERVED-OCCURRENCES-DIFFER"
                    if conflicting
                    else "CANDIDATE-DUPLICATE-EQUIVALENT-NOT-REVIEWED"
                    if duplicate_equivalent
                    else "CANDIDATE-SPATIAL-NOT-REVIEWED"
                ),
                "loads": representative["loads"] if representative else [],
                "occurrences": occurrences,
            }
        )

    schedule_panels, review_items = _schedule_panels(circuit_rows)
    payload: dict[str, Any] = {
        "schema_version": 1,
        "evidence_type": "lighting-panel-schedule-candidate-evidence",
        "source": {
            "drawing_index_id": manifest["index_id"],
            "drawing_index_sha256": manifest.get("index_sha256"),
            "source_drawing_sha256": manifest["document"]["source_drawing_sha256"],
            "entity_count": manifest["entity_count"],
        },
        "method": {
            "route_layer": ROUTE_LAYER,
            "endpoint_tolerance_drawing_units": endpoint_tolerance,
            "device_snap_tolerance_drawing_units": device_snap_tolerance,
            "tag_snap_tolerance_drawing_units": tag_snap_tolerance,
            "crossing_routes_connected": False,
            "drawing_mutation_performed": False,
            "limitation": "All memberships are spatial candidates pending engineer review.",
        },
        "tag_observations": tag_observations,
        "circuits": circuit_rows,
        "schedule_panels": schedule_panels,
        "review_items": review_items,
    }
    payload["result_sha256"] = _sha256(payload)
    return payload


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--autocad-root", type=Path, required=True)
    parser.add_argument("--index-id", required=True)
    parser.add_argument("--output", type=Path, required=True)
    arguments = parser.parse_args()
    manifest, entities = load_index_entities(arguments.autocad_root, arguments.index_id)
    result = build_candidate_evidence(manifest, entities)
    arguments.output.parent.mkdir(parents=True, exist_ok=True)
    arguments.output.write_bytes(_canonical_json(result) + b"\n")
    print(json.dumps({"output": str(arguments.output), "circuits": len(result["circuits"]), "sha256": result["result_sha256"]}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
