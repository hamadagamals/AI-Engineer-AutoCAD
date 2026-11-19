"""Build a deterministic no-mutation review of the right DB3-GA2 circuit group."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
from typing import Any

from PIL import Image, ImageDraw, ImageFont

from integrations.build_lighting_panel_schedule_evidence import ROUTE_LAYER, load_index_entities


WIDTH = 1600
HEIGHT = 1000
MARGIN = 70
EXPECTED_EVIDENCE_SHA256 = "6d940d635251846b5cd6a565cf21de38c4afb5de9550e7b97173808262ca111f"
EXPECTED_SOURCE_SHA256 = "1d08ccb0557e740f7d471e006d5accad0675d54b82e45d9aabc64e239ecfb117"
SELECTED_COMPONENTS = {
    "2": {
        "component": "1A1C308E",
        "tag_handles": ["1A1C304B"],
        "route_handles": ["1A1C308E", "1A1C308F", "1A1C3090", "1A1C3112"],
        "block_handles": ["1A1C2F0F", "1A1C2F19", "1A1C2F1A", "1A1C2F2B"],
    },
    "4": {
        "component": "1A1C33F8",
        "tag_handles": ["1A1C3192"],
        "route_handles": [
            "1A1C308C", "1A1C308D", "1A1C30B5", "1A1C30B6",
            "1A1C3109", "1A1C3371", "1A1C33F7", "1A1C33F8",
        ],
        "block_handles": [
            "1A1C2F1E", "1A1C2F1F", "1A1C2F20", "1A1C2F21",
            "1A1C2F8D", "1A1C2F9B", "1A1C33F5", "1A1C33F6",
        ],
    },
    "7": {
        "component": "1A1C3211",
        "tag_handles": ["1A1C38B6"],
        "route_handles": [
            "1A1C3097", "1A1C30B1", "1A1C320C", "1A1C3211",
            "1A1C3216", "1A1C3242", "1A1C324D", "1A1C324E",
        ],
        "block_handles": [
            "1A1C2F18", "1A1C2F31", "1A1C2F94", "1A1C2F9C",
            "1A1C31FF", "1A1C3202", "1A1C322E", "1A1C322F",
        ],
    },
    "6": {
        "component": "1A1C30B2",
        "tag_handles": ["1A1C304C"],
        "route_handles": [
            "1A1C30B0", "1A1C30B2", "1A1C30B3", "1A1C30B4", "1A1C3111",
            "1A1C32D6", "1A1C32D7", "1A1C3453", "1A1C3459", "1A1C345A",
        ],
        "block_handles": [
            "1A1C2F10", "1A1C2F1B", "1A1C2F1C", "1A1C2F2A",
            "1A1C2F5B", "1A1C33E2", "1A1C33E3", "1A1C33E4",
        ],
    },
}


def _canonical(value: Any) -> bytes:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")


def _digest(value: Any) -> str:
    return hashlib.sha256(_canonical(value)).hexdigest()


def _font(size: int, bold: bool = False) -> ImageFont.FreeTypeFont | ImageFont.ImageFont:
    path = Path("C:/Windows/Fonts") / ("arialbd.ttf" if bold else "arial.ttf")
    return ImageFont.truetype(str(path), size) if path.exists() else ImageFont.load_default()


def build_preview(
    *, manifest: dict[str, Any], entities: list[dict[str, Any]], evidence: dict[str, Any]
) -> dict[str, Any]:
    """Validate exact source objects and return the fixed right-group review contract."""
    if evidence.get("result_sha256") != EXPECTED_EVIDENCE_SHA256:
        raise ValueError("Panel evidence differs from the reviewed SHA-256.")
    if evidence.get("source", {}).get("source_drawing_sha256") != EXPECTED_SOURCE_SHA256:
        raise ValueError("Panel evidence is bound to a different source drawing.")
    if manifest.get("entity_count") != 4848 or manifest.get("complete") is not True:
        raise ValueError("Drawing index is not the reviewed complete 4,848-entity index.")
    by_handle = {entity["object_id"]: entity for entity in entities}
    selected_route_handles: list[str] = []
    selected_tag_handles: list[str] = []
    selected_block_handles: list[str] = []
    groups: list[dict[str, Any]] = []
    for circuit, expected in SELECTED_COMPONENTS.items():
        route_handles = expected["route_handles"]
        tag_handles = expected["tag_handles"]
        block_handles = expected["block_handles"]
        for handle in route_handles:
            entity = by_handle.get(handle)
            if (
                entity is None
                or entity.get("layer") != ROUTE_LAYER
                or entity.get("entity_type") not in {"LINE", "LWPOLYLINE", "POLYLINE"}
                or len(entity.get("points", [])) < 2
            ):
                raise ValueError(f"Reviewed route handle is missing or changed: {handle}")
        for handle in tag_handles:
            entity = by_handle.get(handle)
            if entity is None or not entity.get("points"):
                raise ValueError(f"Reviewed circuit-tag handle is missing or changed: {handle}")
        for handle in block_handles:
            entity = by_handle.get(handle)
            if entity is None or entity.get("entity_type") != "INSERT" or not entity.get("points"):
                raise ValueError(f"Reviewed load-block handle is missing or changed: {handle}")
        selected_route_handles.extend(route_handles)
        selected_tag_handles.extend(tag_handles)
        selected_block_handles.extend(block_handles)
        groups.append(
            {
                "circuit": circuit,
                "component": expected["component"],
                "route_handles": route_handles,
                "tag_handles": tag_handles,
                "block_handles": block_handles,
                "review_status": "ENGINEER-ASSIGNED-FROM-SHARED-5-7-COMPONENT" if circuit == "7" else "RIGHT-OCCURRENCE-CANDIDATE",
            }
        )
    if len(selected_route_handles) != len(set(selected_route_handles)):
        raise ValueError("Reviewed right-group route handles overlap unexpectedly.")
    payload: dict[str, Any] = {
        "schema_version": 1,
        "preview_type": "db3-ga2-right-circuit-group-highlight",
        "panel": "DB3-GA2",
        "selection_rule": "greatest-X/right drawing occurrence only",
        "requested_circuit_range": [1, 2, 3, 4, 5, 6, 7],
        "selected_circuits": [2, 4, 6, 7],
        "absent_circuits": [1, 3, 5],
        "engineer_reclassification": {
            "source_tag_handle": "1A1C32F2",
            "source_tag_value": "5/DB3-GA2",
            "assigned_circuit": 7,
            "instruction": "Treat the shared 5/7 component as circuit 7.",
        },
        "highlight_color": "cyan",
        "proposed_layer": "AIENG-REVIEW-DB3-GA2-RIGHT",
        "method": "duplicate reviewed route geometry as removable overlay; do not recolor source entities",
        "circuit_6_completion_rule": "255-unit device bridge plus exact endpoint-on-polyline T-junction; stable through 350 units",
        "groups": groups,
        "selected_route_count": len(selected_route_handles),
        "selected_route_handles": selected_route_handles,
        "selected_tag_handles": selected_tag_handles,
        "selected_block_count": len(selected_block_handles),
        "selected_block_handles": selected_block_handles,
        "source_index_id": manifest["index_id"],
        "source_entity_count": manifest["entity_count"],
        "source_drawing_sha256": EXPECTED_SOURCE_SHA256,
        "source_evidence_sha256": EXPECTED_EVIDENCE_SHA256,
        "drawing_mutation_performed": False,
        "limitations": [
            "Circuits 1 and 3 were not observed for DB3-GA2.",
            "The former shared circuit-5/7 component is assigned to circuit 7 by explicit engineer instruction; circuit 5 is not generated from it.",
            "Highlight approval does not approve final way numbering, loads, protection, cable, or phase allocation.",
        ],
    }
    payload["preview_sha256"] = _digest(payload)
    return payload


def render_png(preview: dict[str, Any], entities: list[dict[str, Any]], output: Path) -> None:
    """Render the selected right-side occurrence against nearby drawing primitives."""
    by_handle = {entity["object_id"]: entity for entity in entities}
    selected = set(preview["selected_route_handles"])
    selected_points = [
        point
        for handle in selected
        for point in by_handle[handle].get("points", [])
        if len(point) >= 2
    ]
    min_x = min(float(point[0]) for point in selected_points) - 1500.0
    max_x = max(float(point[0]) for point in selected_points) + 1500.0
    min_y = min(float(point[1]) for point in selected_points) - 1500.0
    max_y = max(float(point[1]) for point in selected_points) + 1500.0
    scale = min((WIDTH - 2 * MARGIN) / (max_x - min_x), (HEIGHT - 2 * MARGIN) / (max_y - min_y))

    def xy(point: list[float]) -> tuple[float, float]:
        return (
            MARGIN + (float(point[0]) - min_x) * scale,
            HEIGHT - MARGIN - (float(point[1]) - min_y) * scale,
        )

    image = Image.new("RGB", (WIDTH, HEIGHT), "#202830")
    draw = ImageDraw.Draw(image)
    for entity in entities:
        points = entity.get("points") or []
        if len(points) < 2:
            continue
        if entity.get("layer") != ROUTE_LAYER:
            continue
        if max(float(p[0]) for p in points) < min_x or min(float(p[0]) for p in points) > max_x:
            continue
        if max(float(p[1]) for p in points) < min_y or min(float(p[1]) for p in points) > max_y:
            continue
        draw.line([xy(point) for point in points], fill="#59636d", width=2)
    for handle in preview["selected_route_handles"]:
        draw.line([xy(point) for point in by_handle[handle]["points"]], fill="#00e5ff", width=8)
    for entity in entities:
        if entity.get("entity_type") == "INSERT" and entity.get("points"):
            point = entity["points"][0]
            if min_x <= float(point[0]) <= max_x and min_y <= float(point[1]) <= max_y:
                x, y = xy(point)
                draw.rectangle((x - 4, y - 4, x + 4, y + 4), outline="#ff5252", width=2)
    selected_blocks = set(preview["selected_block_handles"])
    for handle in selected_blocks:
        point = by_handle[handle]["points"][0]
        x, y = xy(point)
        draw.rectangle((x - 9, y - 9, x + 9, y + 9), outline="#00e5ff", fill="#153943", width=4)
    colors = {"2": "#ffff00", "4": "#ffb300", "7": "#ff4dd2", "6": "#7cff6b"}
    for group in preview["groups"]:
        for tag_handle in group["tag_handles"]:
            point = by_handle[tag_handle]["points"][0]
            x, y = xy(point)
            draw.ellipse((x - 11, y - 11, x + 11, y + 11), outline=colors[group["circuit"]], width=4)
            draw.text((x + 15, y - 18), f"CCT {group['circuit']}", fill=colors[group["circuit"]], font=_font(24, True))
    ignored = by_handle[preview["engineer_reclassification"]["source_tag_handle"]]["points"][0]
    x, y = xy(ignored)
    draw.line((x - 10, y - 10, x + 10, y + 10), fill="#ff9800", width=4)
    draw.line((x - 10, y + 10, x + 10, y - 10), fill="#ff9800", width=4)
    draw.text((x + 15, y + 10), "SOURCE 5 -> ASSIGNED 7", fill="#ff9800", font=_font(17, True))
    draw.rectangle((20, 18, 875, 108), fill="#111820", outline="#00e5ff", width=2)
    draw.text((35, 30), "DB3-GA2 - RIGHT OCCURRENCE REVIEW", fill="white", font=_font(28, True))
    draw.text((35, 67), "CYAN = complete route + load overlay | CCT 1,3 absent | SOURCE 5 assigned to CCT 7", fill="#00e5ff", font=_font(18))
    draw.text((35, HEIGHT - 35), f"Preview SHA-256: {preview['preview_sha256']}", fill="#c5ced8", font=_font(13))
    output.parent.mkdir(parents=True, exist_ok=True)
    image.save(output)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--autocad-root", type=Path, required=True)
    parser.add_argument("--index-id", required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    parser.add_argument("--output-json", type=Path, required=True)
    parser.add_argument("--output-png", type=Path, required=True)
    args = parser.parse_args()
    manifest, entities = load_index_entities(args.autocad_root, args.index_id)
    evidence = json.loads(args.evidence.read_text(encoding="utf-8"))
    preview = build_preview(manifest=manifest, entities=entities, evidence=evidence)
    args.output_json.parent.mkdir(parents=True, exist_ok=True)
    args.output_json.write_bytes(_canonical(preview) + b"\n")
    render_png(preview, entities, args.output_png)
    print(json.dumps({"preview_sha256": preview["preview_sha256"], "selected_route_count": preview["selected_route_count"], "drawing_mutation_performed": False}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
