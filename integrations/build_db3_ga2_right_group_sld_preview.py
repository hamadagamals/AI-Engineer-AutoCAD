"""Build the reviewed right-occurrence DB3-GA2 SLD without drawing mutation."""

from __future__ import annotations

import argparse
import json
from collections import Counter
from pathlib import Path
from typing import Any

from integrations.build_panel_sld_preview import (
    MARGIN,
    WIDTH,
    _canonical,
    _sha256,
    render_png,
    render_svg,
)


EXPECTED_EVIDENCE_SHA256 = "6d940d635251846b5cd6a565cf21de38c4afb5de9550e7b97173808262ca111f"
EXPECTED_GROUP_SHA256 = "afa1bb24cfc5b26d7971bfab7f13e471710487f3200ce474ea6f6c4dbee03097"
EXPECTED_SOURCE_SHA256 = "1d08ccb0557e740f7d471e006d5accad0675d54b82e45d9aabc64e239ecfb117"
EXTRA_CIRCUIT_6_L03 = {"1A1C2F5B", "1A1C33E2", "1A1C33E3", "1A1C33E4"}
L03_BLOCK_TOKEN = "LIGHTING FIXTURE- SPOT_L03_"


def _occurrence(evidence: dict[str, Any], circuit: str, component: str) -> dict[str, Any]:
    record = next(
        (
            item
            for item in evidence.get("circuits", [])
            if item.get("panel") == "DB3-GA2" and item.get("circuit") == circuit
        ),
        None,
    )
    if record is None:
        raise ValueError(f"Circuit evidence is missing: {circuit}")
    occurrence = next(
        (item for item in record.get("occurrences", []) if item.get("component") == component),
        None,
    )
    if occurrence is None:
        raise ValueError(f"Reviewed occurrence is missing: {circuit}/{component}")
    return occurrence


def _description(loads: list[dict[str, Any]]) -> str:
    counts = Counter(str(load["description"]) for load in loads)
    return "; ".join(f"{count} x {name}" for name, count in sorted(counts.items()))


def _way(circuit: int, loads: list[dict[str, Any]], unresolved: int) -> dict[str, Any]:
    known_w = round(sum(float(load["watts"]) for load in loads if load.get("watts") is not None), 6)
    known_va = round(sum(float(load["va"]) for load in loads if load.get("va") is not None), 6)
    complete = unresolved == 0 and all(load.get("watts") is not None and load.get("va") is not None for load in loads)
    return {
        "way": circuit,
        "observed_in_lighting_drawing": True,
        "description": _description(loads),
        "connected_w": known_w if complete else None,
        "connected_va": known_va if complete else None,
        "current_a": round(known_va / 230.0, 6) if complete else None,
        "known_partial_w": known_w,
        "known_partial_va": known_va,
        "unresolved_load_count": unresolved,
        "phase": None,
        "status": "DRAFT-ESTIMATED-REVIEWED-RIGHT-OCCURRENCE" if complete else "REVIEW-INCOMPLETE-LOADS",
    }


def build_preview(
    evidence: dict[str, Any], right_group: dict[str, Any], inventory: dict[str, Any]
) -> dict[str, Any]:
    """Return the exact seven-way SLD bound to the reviewed right occurrence."""
    if evidence.get("result_sha256") != EXPECTED_EVIDENCE_SHA256:
        raise ValueError("Panel evidence differs from the reviewed SHA-256.")
    if right_group.get("preview_sha256") != EXPECTED_GROUP_SHA256:
        raise ValueError("Right-group review differs from the accepted SHA-256.")
    if right_group.get("source_drawing_sha256") != EXPECTED_SOURCE_SHA256:
        raise ValueError("Right-group review is bound to another drawing.")
    if inventory.get("source_drawing_sha256") != EXPECTED_SOURCE_SHA256:
        raise ValueError("Inventory is bound to another drawing.")
    if len(inventory.get("entities", [])) != 4848:
        raise ValueError("Inventory is not the complete reviewed 4,848-entity drawing.")

    groups = {str(item["circuit"]): item for item in right_group.get("groups", [])}
    if set(groups) != {"2", "4", "6", "7"} or right_group.get("absent_circuits") != [1, 3, 5]:
        raise ValueError("Right-group way membership is not the accepted 1-7 review.")

    entities = {item["object_id"]: item for item in inventory["entities"]}
    for handle in EXTRA_CIRCUIT_6_L03:
        entity = entities.get(handle)
        if (
            entity is None
            or entity.get("entity_type") != "INSERT"
            or L03_BLOCK_TOKEN not in str(entity.get("block_name", ""))
        ):
            raise ValueError(f"Circuit 6 L03 load block is missing or changed: {handle}")

    ways_by_number: dict[int, dict[str, Any]] = {}
    for circuit in ("2", "4", "6"):
        occurrence = _occurrence(evidence, circuit, str(groups[circuit]["component"]))
        loads = [dict(item) for item in occurrence["loads"]]
        if circuit == "6":
            loads.extend(
                {
                    "handle": handle,
                    "description": "L03 spot lighting fixture",
                    "watts": 28.0,
                    "power_factor": 0.9,
                    "va": round(28.0 / 0.9, 6),
                    "source_class": "ESTIMATED",
                    "confidence": "MEDIUM",
                }
                for handle in sorted(EXTRA_CIRCUIT_6_L03)
            )
        expected_handles = set(groups[circuit]["block_handles"])
        if {load["handle"] for load in loads} != expected_handles:
            raise ValueError(f"Circuit {circuit} load membership differs from right-group review.")
        ways_by_number[int(circuit)] = _way(int(circuit), loads, 0)

    occurrence_7 = _occurrence(evidence, "7", str(groups["7"]["component"]))
    if {item["handle"] for item in occurrence_7["loads"]} != set(groups["7"]["block_handles"]):
        raise ValueError("Circuit 7 load membership differs from engineer-assigned group.")
    ways_by_number[7] = _way(7, [dict(item) for item in occurrence_7["loads"]], 2)

    spacing = (WIDTH - 2 * MARGIN) / 6
    geometry: list[dict[str, Any]] = []
    for number in range(1, 8):
        way = ways_by_number.get(
            number,
            {
                "way": number,
                "observed_in_lighting_drawing": False,
                "description": None,
                "connected_w": None,
                "connected_va": None,
                "current_a": None,
                "known_partial_w": None,
                "known_partial_va": None,
                "unresolved_load_count": 0,
                "phase": None,
                "status": "BLANK-NOT-OBSERVED-IN-REVIEWED-RIGHT-GROUP",
            },
        )
        geometry.append(
            {
                **way,
                "observed": bool(way["observed_in_lighting_drawing"]),
                "x": round(MARGIN + (number - 1) * spacing, 3),
            }
        )

    preview: dict[str, Any] = {
        "schema_version": 2,
        "preview_type": "db3-ga2-right-occurrence-single-line-diagram-draft",
        "panel": "DB3-GA2",
        "system": "400/230 V, 3 Phase, 4 Wire (engineer-assumed)",
        "frequency_hz": None,
        "rated_ways": None,
        "source_evidence_sha256": EXPECTED_EVIDENCE_SHA256,
        "source_right_group_sha256": EXPECTED_GROUP_SHA256,
        "source_inventory_sha256": inventory.get("inventory_sha256"),
        "source_drawing_sha256": EXPECTED_SOURCE_SHA256,
        "way_count_shown": 7,
        "observed_way_count": 4,
        "blank_way_count": 3,
        "assumptions": [
            "400/230 V, 3 Phase, 4 Wire is engineer-assumed.",
            "Ways 1, 3, and 5 are blank, not Spare.",
            "The reviewed shared 5/7 drawing component is assigned only to way 7 by engineer instruction.",
            "Circuit 6 includes four additional reviewed L03 blocks after route completion.",
            "Circuit 7 total remains TBD because L04 and LED tape wattages are unresolved.",
            "Incomer, busbar, phase allocation, protection, cable, and frequency remain TBD.",
            "Red numeric values are provisional estimates, not drawing facts.",
            "This is a no-mutation preview, not an approved construction drawing.",
        ],
        "geometry": geometry,
        "drawing_mutation_performed": False,
    }
    preview["preview_sha256"] = _sha256(preview)
    return preview


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--evidence", type=Path, required=True)
    parser.add_argument("--right-group", type=Path, required=True)
    parser.add_argument("--inventory", type=Path, required=True)
    parser.add_argument("--output-json", type=Path, required=True)
    parser.add_argument("--output-svg", type=Path, required=True)
    parser.add_argument("--output-png", type=Path, required=True)
    args = parser.parse_args()
    preview = build_preview(
        json.loads(args.evidence.read_text(encoding="utf-8")),
        json.loads(args.right_group.read_text(encoding="utf-8")),
        json.loads(args.inventory.read_text(encoding="utf-8")),
    )
    args.output_json.parent.mkdir(parents=True, exist_ok=True)
    args.output_json.write_bytes(_canonical(preview) + b"\n")
    args.output_svg.write_text(render_svg(preview), encoding="utf-8")
    render_png(preview, args.output_png)
    print(json.dumps({"preview_sha256": preview["preview_sha256"], "drawing_mutation_performed": False}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
