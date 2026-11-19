"""Build a bounded, non-executable L4 gym lighting placement preview."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path
from typing import Any


L4_LUMENS = 10_501.2
L4_ACTIVE_POWER_W = 67.0
L4_TARGET_LUX = 300.0
L4_UF = 0.65
L4_MF = 0.80
L4_QUANTITY = 45
L4_GRID_COLUMNS = 9
L4_GRID_ROWS = 5


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def _canonical_fingerprint(payload: dict[str, Any]) -> str:
    encoded = json.dumps(
        payload,
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest().upper()


def _find_room(inventory: dict[str, Any], room_name: str) -> dict[str, Any]:
    rooms = inventory.get("categories", {}).get("rooms", {}).get("elements", [])
    matches = [room for room in rooms if room.get("name") == room_name]
    if len(matches) != 1:
        raise ValueError(f"Expected one room named {room_name!r}; found {len(matches)}")
    return matches[0]


def _grid_points(room: dict[str, Any]) -> list[dict[str, Any]]:
    minimum = room["bbox_min"]
    maximum = room["bbox_max"]
    width = float(maximum["x_mm"]) - float(minimum["x_mm"])
    depth = float(maximum["y_mm"]) - float(minimum["y_mm"])
    step_x = width / L4_GRID_COLUMNS
    step_y = depth / L4_GRID_ROWS
    points: list[dict[str, Any]] = []
    for row in range(L4_GRID_ROWS):
        for column in range(L4_GRID_COLUMNS):
            points.append(
                {
                    "point_id": f"L4-GYM-{len(points) + 1:03d}",
                    "grid_column": column + 1,
                    "grid_row": row + 1,
                    "x_mm": round(float(minimum["x_mm"]) + (column + 0.5) * step_x, 3),
                    "y_mm": round(float(minimum["y_mm"]) + (row + 0.5) * step_y, 3),
                    "mounting_z_mm": None,
                    "design_id": "L4",
                    "active_power_w": L4_ACTIVE_POWER_W,
                    "lumens": L4_LUMENS,
                }
            )
    return points


def build_preview(inventory_path: Path, workbook_path: Path) -> dict[str, Any]:
    inventory = json.loads(inventory_path.read_text(encoding="utf-8-sig"))
    room = _find_room(inventory, "GYM 3500")
    minimum = room["bbox_min"]
    maximum = room["bbox_max"]
    width_mm = float(maximum["x_mm"]) - float(minimum["x_mm"])
    depth_mm = float(maximum["y_mm"]) - float(minimum["y_mm"])
    area_m2 = width_mm * depth_mm / 1_000_000
    points = _grid_points(room)
    achieved_lux = L4_QUANTITY * L4_LUMENS * L4_UF * L4_MF / area_m2

    if len(points) != L4_QUANTITY:
        raise ValueError("The L4 grid does not contain the approved 45 points")
    if not math.isclose(area_m2, 801.1714431707999, rel_tol=0, abs_tol=0.01):
        raise ValueError(f"Gym bounding area changed unexpectedly: {area_m2:.3f} m2")

    payload: dict[str, Any] = {
        "schema_version": 1,
        "preview_kind": "revit_school_l4_gym_xy_placement",
        "execution_authorized": False,
        "created_date": "2026-10-16",
        "design_basis": {
            "system": "380/220 V, 3-phase, 4-wire, 50 Hz, TN-S",
            "governing_code_family": "Egyptian electrical code",
            "compliance_status": (
                "PRELIMINARY: exact licensed Egyptian illuminance clauses are not available in "
                "the project evidence set"
            ),
        },
        "engineer_decision": {
            "decision": "Use exact official IES power for L4 preliminary distribution",
            "approved_active_power_w": L4_ACTIVE_POWER_W,
            "approved_by": "Hamada Gamal",
            "approval_channel": "conversation",
            "approval_date": "2026-10-16",
            "scope": "preliminary lighting distribution only",
        },
        "source": {
            "inventory_path": "workspace/revit-school/School-Architectural-readonly-inventory.json",
            "inventory_sha256": _sha256(inventory_path),
            "workbook_path": "workspace/revit-school/outputs/School-Electrical-Distribution-Preview.xlsx",
            "workbook_sha256": _sha256(workbook_path),
            "source_rvt_sha256": inventory["source_rvt_sha256"],
            "l4_ies_sha256": "648B25C3299A591FEDF87FF72D67DE3FF93D81416467CA6811AEAFEADE6258A8",
        },
        "room": {
            "element_id": room["element_id"],
            "name": room["name"],
            "level_id": room["level_id"],
            "level_name": room["level_name"],
            "bbox_min": minimum,
            "bbox_max": maximum,
            "bbox_area_m2": round(area_m2, 6),
            "bbox_area_matches_room_area": True,
            "ceiling_host_present": False,
        },
        "calculation": {
            "target_lux": L4_TARGET_LUX,
            "lumens_per_luminaire": L4_LUMENS,
            "utilization_factor": L4_UF,
            "maintenance_factor": L4_MF,
            "required_quantity": L4_QUANTITY,
            "achieved_lux_lumen_method": round(achieved_lux, 1),
            "active_connected_load_w": L4_QUANTITY * L4_ACTIVE_POWER_W,
            "apparent_load_va": None,
            "power_factor": None,
        },
        "placement": {
            "grid": "9x5",
            "spacing_x_mm": round(width_mm / L4_GRID_COLUMNS, 3),
            "spacing_y_mm": round(depth_mm / L4_GRID_ROWS, 3),
            "mounting_z_mm": None,
            "status": "XY_READY_Z_AND_FINAL_PHOTOMETRIC_CHECK_REQUIRED",
            "points": points,
        },
        "hold_points": [
            "Enter and approve mounting height before Revit execution.",
            "Run an IES-based point calculation including reflectances and sports glare review.",
            "Pin the licensed Egyptian illuminance clause before claiming code compliance.",
            "Confirm power factor before calculating VA, current, circuit and panel values.",
            "Issue a separate hash-bound Revit transaction preview and approval.",
        ],
    }
    payload["approval_fingerprint_sha256"] = _canonical_fingerprint(payload)
    return payload


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--inventory", required=True, type=Path)
    parser.add_argument("--workbook", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    preview = build_preview(args.inventory.resolve(), args.workbook.resolve())
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(
        json.dumps(preview, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    print(json.dumps({
        "output": str(args.output.resolve()),
        "fingerprint": preview["approval_fingerprint_sha256"],
        "points": len(preview["placement"]["points"]),
        "achieved_lux": preview["calculation"]["achieved_lux_lumen_method"],
    }))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
