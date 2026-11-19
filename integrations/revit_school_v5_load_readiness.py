"""Build a source-bound preliminary load ledger for the school V5 review overlay.

This module never edits Revit. Detail Lines are review marks, not MEP devices.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import uuid
from collections import Counter, defaultdict
from pathlib import Path
from typing import Any


class SchoolV5ReadinessError(ValueError):
    """The review evidence is inconsistent or insufficient for a load ledger."""


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def _preview_fingerprint(preview: dict[str, Any]) -> str:
    payload = {key: value for key, value in preview.items() if key != "preview_sha256"}
    encoded = json.dumps(payload, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest().upper()


def _read_object(path: Path) -> dict[str, Any]:
    value = json.loads(path.read_text(encoding="utf-8-sig"))
    if not isinstance(value, dict):
        raise SchoolV5ReadinessError(f"Expected JSON object: {path}")
    return value


def _workspace_path(path: Path, workspace_root: Path, *, must_exist: bool) -> Path:
    candidate = path.expanduser().resolve(strict=must_exist)
    if (
        not candidate.is_relative_to(workspace_root)
        or (must_exist and (not candidate.is_file() or path.is_symlink()))
        or (not must_exist and (candidate.exists() or not candidate.parent.is_dir()))
    ):
        raise SchoolV5ReadinessError(f"Path is outside the workspace or has an unsafe state: {path}")
    return candidate


def _write_new(path: Path, content: str) -> None:
    temporary = path.with_name(f".{path.name}.{uuid.uuid4().hex}.tmp")
    try:
        with temporary.open("x", encoding="utf-8", newline="\n") as stream:
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def build_readiness(
    preview: dict[str, Any], handoff: dict[str, Any],
    model_inventory: dict[str, dict[str, Any]],
) -> dict[str, Any]:
    """Reconcile exact points and preliminary watts, preserving unknown engineering inputs."""
    if (
        preview.get("action") != "create_school_electrical_review_v5"
        or preview.get("preview_sha256") != _preview_fingerprint(preview)
        or handoff.get("preview_sha256") != preview.get("preview_sha256")
        or handoff.get("source_sha256") != preview.get("source_sha256")
        or handoff.get("saved_as_new_file") is not True
        or handoff.get("transaction_committed") is not True
    ):
        raise SchoolV5ReadinessError("V5 preview/handoff identity or Save As evidence does not reconcile.")
    inventory_counts: dict[str, int] = {}
    for category in ("lighting_fixtures", "electrical_fixtures", "electrical_equipment"):
        item = model_inventory.get(category)
        if (
            not isinstance(item, dict)
            or item.get("category_key") != category
            or item.get("document_title") != "School-Electrical-Review-V5-20261031"
            or item.get("document_mutation_performed") is not False
            or item.get("provider_egress_performed") is not False
            or not isinstance(item.get("total_count"), int)
            or isinstance(item.get("total_count"), bool)
            or item["total_count"] < 0
            or not isinstance(item.get("elements"), list)
            or item.get("returned_count") != len(item["elements"])
        ):
            raise SchoolV5ReadinessError(f"Live V5 {category} inventory is invalid.")
        inventory_counts[category] = item["total_count"]
    rooms = preview.get("rooms")
    points = preview.get("v5_expected_points")
    if not isinstance(rooms, list) or not isinstance(points, list) or not rooms:
        raise SchoolV5ReadinessError("V5 room or point list is missing.")
    level_by_room = {
        room.get("room_id"): room.get("level_key") for room in rooms if isinstance(room, dict)
    }
    room_ids: set[int] = set()
    room_rows: list[dict[str, Any]] = []
    lighting_zones: dict[str, dict[str, Any]] = defaultdict(lambda: {
        "room_count": 0, "lighting_symbols": 0, "connected_lighting_w": 0,
    })
    power_zones: dict[str, dict[str, Any]] = defaultdict(lambda: {
        "room_count": 0, "duplex_candidates": 0, "gfi_candidates": 0,
        "dedicated_candidates": 0,
    })
    point_counts = Counter()
    point_keys: set[tuple[str, float, float]] = set()
    for point in points:
        if not isinstance(point, dict) or not isinstance(point.get("room_id"), int):
            raise SchoolV5ReadinessError("V5 contains an invalid lighting point.")
        level = level_by_room.get(point["room_id"])
        if not isinstance(level, str) or not level:
            raise SchoolV5ReadinessError("V5 lighting point references an unknown room level.")
        x, y = point.get("x_mm"), point.get("y_mm")
        if not all(isinstance(value, (int, float)) and not isinstance(value, bool) for value in (x, y)):
            raise SchoolV5ReadinessError("V5 lighting point coordinates are invalid.")
        key = (level, round(float(x), 1), round(float(y), 1))
        if key in point_keys:
            raise SchoolV5ReadinessError("V5 contains coincident lighting point centers.")
        point_keys.add(key)
        point_counts[point["room_id"]] += 1
    for room in rooms:
        if not isinstance(room, dict):
            raise SchoolV5ReadinessError("V5 contains an invalid room row.")
        room_id = room.get("room_id")
        count = room.get("lighting_count")
        watts = room.get("lighting_w")
        counts = [room.get(key) for key in ("new_duplex_count", "new_gfi_count", "dedicated_count")]
        if (
            not isinstance(room_id, int) or isinstance(room_id, bool) or room_id in room_ids
            or not isinstance(count, int) or isinstance(count, bool) or count < 1
            or not isinstance(watts, (int, float)) or isinstance(watts, bool) or watts <= 0
            or any(not isinstance(n, int) or isinstance(n, bool) or n < 0 for n in counts)
            or point_counts[room_id] != count
            or not isinstance(room.get("lighting_panel_zone"), str)
            or not isinstance(room.get("power_panel_zone"), str)
        ):
            raise SchoolV5ReadinessError(f"V5 room/point/load mismatch for room {room_id}.")
        room_ids.add(room_id)
        row = {
            "room_id": room_id,
            "room_name": room.get("room_name"),
            "level_key": room.get("level_key"),
            "use": room.get("use"),
            "lighting_type": room.get("luminaire_type"),
            "lighting_symbols": count,
            "preliminary_connected_lighting_w": round(float(watts), 3),
            "lighting_panel_zone_candidate": room["lighting_panel_zone"],
            "power_panel_zone_candidate": room["power_panel_zone"],
            "duplex_candidates": counts[0],
            "gfi_candidates": counts[1],
            "dedicated_candidates": counts[2],
            "lighting_va": None,
            "receptacle_va": None,
            "equipment_va": None,
            "circuit_id": None,
            "breaker_a": None,
            "cable_size_mm2": None,
            "installed_cable_length_m": None,
        }
        room_rows.append(row)
        lighting_zone = lighting_zones[room["lighting_panel_zone"]]
        lighting_zone["room_count"] += 1
        lighting_zone["lighting_symbols"] += count
        lighting_zone["connected_lighting_w"] += float(watts)
        power_zone = power_zones[room["power_panel_zone"]]
        power_zone["room_count"] += 1
        power_zone["duplex_candidates"] += counts[0]
        power_zone["gfi_candidates"] += counts[1]
        power_zone["dedicated_candidates"] += counts[2]
    if set(point_counts) != room_ids:
        raise SchoolV5ReadinessError("V5 lighting points reference unknown rooms.")
    totals = {
        "rooms": len(room_rows),
        "lighting_symbols": len(points),
        "preliminary_connected_lighting_w": round(sum(row["preliminary_connected_lighting_w"] for row in room_rows), 3),
        "duplex_candidates": sum(row["duplex_candidates"] for row in room_rows),
        "gfi_candidates": sum(row["gfi_candidates"] for row in room_rows),
        "dedicated_candidates": sum(row["dedicated_candidates"] for row in room_rows),
    }
    expected = {
        "rooms": handoff.get("room_count"),
        "lighting_symbols": handoff.get("lighting_point_count"),
        "duplex_candidates": handoff.get("new_duplex_count"),
        "gfi_candidates": handoff.get("new_gfi_count"),
        "dedicated_candidates": handoff.get("dedicated_count"),
    }
    if any(totals[key] != value for key, value in expected.items()):
        raise SchoolV5ReadinessError("V5 load totals differ from the live Revit handoff.")
    return {
        "schema_version": 1,
        "status": "PRELIMINARY_REVIEW_ONLY",
        "preview_sha256": preview["preview_sha256"],
        "source_rvt_sha256": preview["source_sha256"],
        "v5_rvt_sha256_from_live_handoff": handoff.get("output_sha256"),
        "totals": totals,
        "existing_physical_revit_category_counts": inventory_counts,
        "lighting_zone_candidates": {
            key: {**value, "connected_lighting_w": round(value["connected_lighting_w"], 3)}
            for key, value in sorted(lighting_zones.items())
        },
        "power_zone_candidates": dict(sorted(power_zones.items())),
        "rooms": sorted(room_rows, key=lambda row: (str(row["level_key"]), str(row["room_name"]), row["room_id"])),
        "calculation_gates": {
            "actual_revit_lighting_families_and_connectors": "INCOMPLETE: existing sample instances require exact point and connector reconciliation",
            "actual_revit_receptacle_families_and_connectors": "INCOMPLETE: existing instances require exact point and connector reconciliation",
            "panel_families_distribution_and_locations": "MISSING",
            "fixture_power_factor_and_apparent_load": "MISSING",
            "receptacle_and_equipment_nameplate_loads": "MISSING",
            "approved_demand_diversity_and_phase_allocation": "MISSING",
            "verified_route_and_vertical_lengths": "MISSING",
            "source_fault_duty_and_protection_coordination": "MISSING",
            "cable_installation_derating_and_code_edition": "MISSING",
            "photometric_and_mounting_review": "MISSING",
        },
        "not_calculated": [
            "circuit membership", "apparent power VA", "design current A", "breaker rating A",
            "cable cross-section mm2", "voltage drop percent", "short-circuit duty kA",
            "installed cable/conduit quantities", "panel schedules", "Egyptian-code compliance",
        ],
    }


def render_markdown(result: dict[str, Any]) -> str:
    """Show reconciled quantities and explicit calculation holds for engineer review."""
    totals = result["totals"]
    lines = [
        "# School V5 electrical load readiness",
        "",
        "**Status: preliminary review overlay; V5 created no new Revit circuits or routed wiring.**",
        "",
        f"Approved V5 preview SHA-256: `{result['preview_sha256']}`.",
        f"Live saved RVT SHA-256: `{result['v5_rvt_sha256_from_live_handoff']}`.",
        "",
        f"Reconciled: {totals['rooms']} rooms, {totals['lighting_symbols']} lighting review symbols, "
        f"{totals['preliminary_connected_lighting_w']:,.0f} W preliminary connected lighting, "
        f"{totals['duplex_candidates']} duplex, {totals['gfi_candidates']} GFI and "
        f"{totals['dedicated_candidates']} dedicated outlet candidates.",
        "The watts come from the V5 review schedule and are **active W**, not VA or demand load.",
        "Counts are not physical Revit family quantities or a construction takeoff.",
        f"Read-only Revit category counts: {result['existing_physical_revit_category_counts']['lighting_fixtures']} "
        "existing Lighting Fixtures, "
        f"{result['existing_physical_revit_category_counts']['electrical_fixtures']} "
        "existing Electrical Fixtures, and "
        f"{result['existing_physical_revit_category_counts']['electrical_equipment']} "
        "Electrical Equipment. These are not subtracted from proposed symbols until exact",
        "level/coordinate/type and connector reconciliation prevents double placement.",
        "",
        "| Proposed lighting zone | Rooms | Light symbols | Preliminary W |",
        "|---|---:|---:|---:|",
    ]
    for name, zone in result["lighting_zone_candidates"].items():
        lines.append(
            f"| {name} | {zone['room_count']} | {zone['lighting_symbols']} | "
            f"{zone['connected_lighting_w']:,.0f} |"
        )
    lines += [
        "",
        "| Proposed power zone | Rooms | Duplex candidates | GFI candidates | Dedicated candidates |",
        "|---|---:|---:|---:|---:|",
    ]
    for name, zone in result["power_zone_candidates"].items():
        lines.append(
            f"| {name} | {zone['room_count']} | {zone['duplex_candidates']} | "
            f"{zone['gfi_candidates']} | {zone['dedicated_candidates']} |"
        )
    lines += [
        "",
        "Each room's counts and unresolved fields are in the companion JSON. Proposed zone names",
        "do not establish actual panel identities, circuit membership, or installed routes.",
        "",
        "## Required before physical circuits and calculations",
        "",
    ]
    lines += [f"- {name.replace('_', ' ')}: {state}." for name, state in result["calculation_gates"].items()]
    lines += [
        "",
        "Revit requires compatible placed electrical components for actual circuits; circuit",
        "paths and panel assignments must be reviewed before cable lengths or voltage drop",
        "are treated as installed-design values. Do not turn this ledger into breaker, cable,",
        "panel schedule, or Egyptian-code approval values without the missing inputs.",
        "",
    ]
    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--preview", type=Path, required=True)
    parser.add_argument("--handoff", type=Path, required=True)
    parser.add_argument("--lighting-inventory", type=Path, required=True)
    parser.add_argument("--outlet-inventory", type=Path, required=True)
    parser.add_argument("--equipment-inventory", type=Path, required=True)
    parser.add_argument("--json-output", type=Path, required=True)
    parser.add_argument("--markdown-output", type=Path, required=True)
    args = parser.parse_args()
    workspace = (Path(__file__).resolve().parents[1] / "workspace").resolve(strict=True)
    preview_path = _workspace_path(args.preview, workspace, must_exist=True)
    handoff_path = _workspace_path(args.handoff, workspace, must_exist=True)
    inventory_paths = {
        "lighting_fixtures": _workspace_path(args.lighting_inventory, workspace, must_exist=True),
        "electrical_fixtures": _workspace_path(args.outlet_inventory, workspace, must_exist=True),
        "electrical_equipment": _workspace_path(args.equipment_inventory, workspace, must_exist=True),
    }
    json_output = _workspace_path(args.json_output, workspace, must_exist=False)
    markdown_output = _workspace_path(args.markdown_output, workspace, must_exist=False)
    if json_output == markdown_output or json_output.suffix.lower() != ".json" or markdown_output.suffix.lower() != ".md":
        raise SchoolV5ReadinessError("Distinct .json and .md outputs are required.")
    preview = _read_object(preview_path)
    handoff = _read_object(handoff_path)
    result = build_readiness(preview, handoff, {
        category: _read_object(path) for category, path in inventory_paths.items()
    })
    result["preview_file_sha256"] = _sha256(preview_path)
    result["handoff_file_sha256"] = _sha256(handoff_path)
    result["inventory_file_sha256"] = {
        category: _sha256(path) for category, path in inventory_paths.items()
    }
    _write_new(json_output, json.dumps(result, ensure_ascii=False, indent=2) + "\n")
    _write_new(markdown_output, render_markdown(result))
    print(json.dumps({"rooms": result["totals"]["rooms"], "lighting_symbols": result["totals"]["lighting_symbols"], "connected_lighting_w": result["totals"]["preliminary_connected_lighting_w"]}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
