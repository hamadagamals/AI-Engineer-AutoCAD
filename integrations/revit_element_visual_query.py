"""Strict loader for one local Revit element visual/focus query."""

from __future__ import annotations

import json
import math
from dataclasses import dataclass
from pathlib import Path

from agent.exceptions import RevitIntegrationError


@dataclass(frozen=True, slots=True)
class RevitElementVisualQuery:
    request_id: str
    document_title: str
    element_id: int
    family_name: str
    type_name: str
    active_view_id: int
    active_view_name: str
    active_view_type: str
    temporary_hide_isolate_was_active: bool
    visible_in_view: bool
    model_bounding_box_mm: tuple[float, ...]
    view_bounding_box_mm: tuple[float, ...]
    image_file: str
    facing_orientation: tuple[float, float, float]
    hand_orientation: tuple[float, float, float]
    mirrored: bool
    facing_flipped: bool
    hand_flipped: bool
    hidden_in_view: bool
    category_hidden_in_view: bool
    visible_geometry_objects: int
    location_point_mm: tuple[float, ...]
    elevation_from_level_mm: float | None
    matching_view_filters: tuple[dict[str, object], ...]
    element_projection_line_color: tuple[int, ...]


def load_element_visual_query(path: Path, *, allowed_root: Path) -> RevitElementVisualQuery:
    root = allowed_root.expanduser().resolve(strict=True)
    candidate = path.expanduser().resolve(strict=True)
    if not candidate.is_file() or candidate.is_symlink() or not candidate.is_relative_to(root):
        raise RevitIntegrationError("Revit visual query escaped its allowed root.")
    if candidate.stat().st_size > 65_536:
        raise RevitIntegrationError("Revit visual query exceeds 64 KiB.")
    try:
        value = json.loads(candidate.read_text(encoding="utf-8-sig"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise RevitIntegrationError(f"Revit visual query is invalid JSON: {exc}") from exc
    if (
        not isinstance(value, dict)
        or value.get("schema_version") != 1
        or value.get("category_key") != "lighting_fixtures"
        or value.get("provider_egress_performed") is not False
        or value.get("document_mutation_performed") is not False
    ):
        raise RevitIntegrationError("Revit visual query contract is invalid.")

    def text(name: str, maximum: int = 260) -> str:
        item = value.get(name)
        if not isinstance(item, str) or not item.strip() or len(item) > maximum:
            raise RevitIntegrationError(f"Revit visual query field {name} is invalid.")
        return item

    def positive_integer(name: str) -> int:
        item = value.get(name)
        if not isinstance(item, int) or isinstance(item, bool) or item <= 0:
            raise RevitIntegrationError(f"Revit visual query field {name} is invalid.")
        return item

    def box(name: str) -> tuple[float, ...]:
        item = value.get(name)
        if not isinstance(item, list) or len(item) not in {0, 6}:
            raise RevitIntegrationError(f"Revit visual query field {name} is invalid.")
        if any(
            not isinstance(number, (int, float))
            or isinstance(number, bool)
            or not math.isfinite(float(number))
            or abs(float(number)) > 1_000_000
            for number in item
        ):
            raise RevitIntegrationError(f"Revit visual query field {name} is invalid.")
        return tuple(float(number) for number in item)

    def point(name: str) -> tuple[float, ...]:
        item = value.get(name)
        if not isinstance(item, list) or len(item) not in {0, 3}:
            raise RevitIntegrationError(f"Revit visual query field {name} is invalid.")
        if any(
            not isinstance(number, (int, float))
            or isinstance(number, bool)
            or not math.isfinite(float(number))
            or abs(float(number)) > 1_000_000
            for number in item
        ):
            raise RevitIntegrationError(f"Revit visual query field {name} is invalid.")
        return tuple(float(number) for number in item)

    temporary = value.get("temporary_hide_isolate_was_active")
    visible = value.get("visible_in_view")
    if not isinstance(temporary, bool) or not isinstance(visible, bool):
        raise RevitIntegrationError("Revit visual query visibility flags are invalid.")
    view_box = box("view_bounding_box_mm")
    if visible != bool(view_box):
        raise RevitIntegrationError("Revit visual query visibility and view bounds disagree.")
    image_file = text("image_file", 180)
    if not image_file.startswith("visual-exports/") or not image_file.lower().endswith(".png"):
        raise RevitIntegrationError("Revit visual evidence image path is invalid.")
    flags = (
        value.get("mirrored"), value.get("facing_flipped"), value.get("hand_flipped"),
        value.get("hidden_in_view"), value.get("category_hidden_in_view"),
    )
    if any(not isinstance(flag, bool) for flag in flags):
        raise RevitIntegrationError("Revit visual query orientation flags are invalid.")
    geometry_count = value.get("visible_geometry_objects")
    if not isinstance(geometry_count, int) or isinstance(geometry_count, bool) or not 0 <= geometry_count <= 10000:
        raise RevitIntegrationError("Revit visual geometry count is invalid.")
    location_point = point("location_point_mm")
    elevation = value.get("elevation_from_level_mm")
    if elevation is not None and (
        not isinstance(elevation, (int, float))
        or isinstance(elevation, bool)
        or not math.isfinite(float(elevation))
        or abs(float(elevation)) > 1_000_000
    ):
        raise RevitIntegrationError("Revit visual elevation is invalid.")
    filters = value.get("matching_view_filters")
    if not isinstance(filters, list) or len(filters) > 32:
        raise RevitIntegrationError("Revit visual view filters are invalid.")
    checked_filters: list[dict[str, object]] = []
    for item in filters:
        if (
            not isinstance(item, dict)
            or not isinstance(item.get("filter_id"), int)
            or isinstance(item.get("filter_id"), bool)
            or item["filter_id"] <= 0
            or not isinstance(item.get("filter_name"), str)
            or not item["filter_name"].strip()
            or len(item["filter_name"]) > 260
            or not isinstance(item.get("visible"), bool)
        ):
            raise RevitIntegrationError("Revit visual view filter entry is invalid.")
        color = item.get("projection_line_color")
        if not isinstance(color, list) or len(color) not in {0, 3} or any(
            not isinstance(channel, int) or isinstance(channel, bool) or not 0 <= channel <= 255
            for channel in color
        ):
            raise RevitIntegrationError("Revit visual view filter color is invalid.")
        checked_filters.append({
            "filter_id": item["filter_id"],
            "filter_name": item["filter_name"],
            "visible": item["visible"],
            "projection_line_color": tuple(color),
        })
    element_color = value.get("element_projection_line_color")
    if not isinstance(element_color, list) or len(element_color) not in {0, 3} or any(
        not isinstance(channel, int) or isinstance(channel, bool) or not 0 <= channel <= 255
        for channel in element_color
    ):
        raise RevitIntegrationError("Revit visual element override color is invalid.")

    def vector(name: str) -> tuple[float, float, float]:
        item = value.get(name)
        if not isinstance(item, list) or len(item) != 3 or any(
            not isinstance(number, (int, float)) or isinstance(number, bool)
            or not math.isfinite(float(number)) or abs(float(number)) > 1.000001
            for number in item
        ):
            raise RevitIntegrationError(f"Revit visual query field {name} is invalid.")
        return (float(item[0]), float(item[1]), float(item[2]))
    return RevitElementVisualQuery(
        request_id=text("request_id", 36),
        document_title=text("document_title"),
        element_id=positive_integer("element_id"),
        family_name=text("family_name"),
        type_name=text("type_name"),
        active_view_id=positive_integer("active_view_id"),
        active_view_name=text("active_view_name"),
        active_view_type=text("active_view_type", 64),
        temporary_hide_isolate_was_active=temporary,
        visible_in_view=visible,
        model_bounding_box_mm=box("model_bounding_box_mm"),
        view_bounding_box_mm=view_box,
        image_file=image_file,
        facing_orientation=vector("facing_orientation"),
        hand_orientation=vector("hand_orientation"),
        mirrored=flags[0],
        facing_flipped=flags[1],
        hand_flipped=flags[2],
        hidden_in_view=flags[3],
        category_hidden_in_view=flags[4],
        visible_geometry_objects=geometry_count,
        location_point_mm=location_point,
        elevation_from_level_mm=None if elevation is None else float(elevation),
        matching_view_filters=tuple(checked_filters),
        element_projection_line_color=tuple(element_color),
    )
