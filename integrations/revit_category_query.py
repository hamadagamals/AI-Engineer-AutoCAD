"""Strict loader for bounded chat-driven Revit category query results."""

from __future__ import annotations

import json
import math
from dataclasses import dataclass
from pathlib import Path

from agent.exceptions import RevitIntegrationError


ALLOWED_CATEGORIES = frozenset({
    "walls", "floors", "ceilings", "doors", "windows", "rooms", "mep_spaces",
    "lighting_fixtures", "lighting_devices", "electrical_fixtures",
    "electrical_equipment", "conduit", "cable_tray",
    "conduit_fittings", "cable_tray_fittings",
})
ALLOWED_PARAMETER_KEYS = frozenset({
    "mark", "type_mark", "manufacturer", "model", "voltage", "wattage",
    "apparent_load", "circuit_number", "panel",
})
ALLOWED_HOST_KINDS = frozenset({"element", "face", "linked_face"})


@dataclass(frozen=True, slots=True)
class RevitCategoryQuery:
    request_id: str
    document_title: str
    category_key: str
    host_filter: str
    total_count: int
    returned_count: int
    truncated: bool
    start_after_element_id: int
    next_start_after_element_id: int
    elements: tuple[dict[str, object], ...]


def load_category_query(path: Path, *, allowed_root: Path) -> RevitCategoryQuery:
    root = allowed_root.expanduser().resolve(strict=True)
    candidate = path.expanduser().resolve(strict=True)
    if not candidate.is_file() or candidate.is_symlink() or not candidate.is_relative_to(root):
        raise RevitIntegrationError("Revit category result escaped its configured root.")
    if candidate.stat().st_size > 262_144:
        raise RevitIntegrationError("Revit category result exceeds 256 KiB.")
    value = json.loads(candidate.read_text(encoding="utf-8-sig"))
    if not isinstance(value, dict) or value.get("schema_version") != 1:
        raise RevitIntegrationError("Revit category result schema is invalid.")
    if value.get("provider_egress_performed") is not False or value.get("document_mutation_performed") is not False:
        raise RevitIntegrationError("Revit category result violated the read-only boundary.")
    category = value.get("category_key")
    host_filter = value.get("host_filter", "all")
    elements = value.get("elements")
    total = value.get("total_count")
    returned = value.get("returned_count")
    start_after = value.get("start_after_element_id", 0)
    next_start_after = value.get("next_start_after_element_id", 0)
    if (
        category not in ALLOWED_CATEGORIES
        or host_filter not in {"all", "hosted", "ceiling_hosted"}
        or not isinstance(elements, list)
        or len(elements) > 32
    ):
        raise RevitIntegrationError("Revit category result is outside the bounded contract.")
    if not isinstance(total, int) or not isinstance(returned, int) or returned != len(elements) or total < returned:
        raise RevitIntegrationError("Revit category result counts are invalid.")
    if any(
        not isinstance(item, int) or isinstance(item, bool) or item < 0
        for item in (start_after, next_start_after)
    ):
        raise RevitIntegrationError("Revit category result cursor is invalid.")
    element_ids: list[int] = []
    for element in elements:
        if not isinstance(element, dict) or not isinstance(element.get("element_id"), int):
            raise RevitIntegrationError("Revit category element is invalid.")
        element_ids.append(element["element_id"])
        for field in ("category", "name", "type_name", "family_name", "level_name"):
            if not isinstance(element.get(field), str) or len(element[field]) > 260:
                raise RevitIntegrationError(f"Revit category element field {field} is invalid.")
        for field in ("type_id", "level_id"):
            identity = element.get(field, -1)
            if not isinstance(identity, int) or isinstance(identity, bool) or identity < -1:
                raise RevitIntegrationError(f"Revit category element field {field} is invalid.")
        location = element.get("location_point")
        if location is not None:
            if not isinstance(location, dict) or any(
                not isinstance(location.get(axis), (int, float))
                or isinstance(location.get(axis), bool)
                or not math.isfinite(float(location[axis]))
                or abs(float(location[axis])) > 1_000_000
                for axis in ("x_mm", "y_mm", "z_mm")
            ):
                raise RevitIntegrationError("Revit category location point is invalid.")
        for field in ("level_elevation_mm", "elevation_from_level_mm"):
            measurement = element.get(field)
            if measurement is not None and (
                not isinstance(measurement, (int, float))
                or isinstance(measurement, bool)
                or not math.isfinite(float(measurement))
                or abs(float(measurement)) > 1_000_000
            ):
                raise RevitIntegrationError(f"Revit category element field {field} is invalid.")
        for field in ("curve_length_mm", "diameter_mm", "width_mm", "height_mm"):
            measurement = element.get(field)
            if measurement is not None and (
                not isinstance(measurement, (int, float))
                or isinstance(measurement, bool)
                or not math.isfinite(float(measurement))
                or float(measurement) < 0
                or float(measurement) > 100_000_000
            ):
                raise RevitIntegrationError(f"Revit category element field {field} is invalid.")
        host = element.get("host")
        if host_filter in {"hosted", "ceiling_hosted"} and host is None:
            raise RevitIntegrationError("Revit hosted category result contains an unhosted element.")
        if host is not None:
            if not isinstance(host, dict) or host.get("kind") not in ALLOWED_HOST_KINDS:
                raise RevitIntegrationError("Revit category host metadata is invalid.")
            for field in ("element_id", "linked_element_id"):
                identity = host.get(field, -1)
                if not isinstance(identity, int) or isinstance(identity, bool) or identity < -1:
                    raise RevitIntegrationError("Revit category host identity is invalid.")
            if not isinstance(host.get("is_linked"), bool):
                raise RevitIntegrationError("Revit category host link flag is invalid.")
            for field in (
                "category", "category_key", "name", "linked_document_title",
                "linked_category", "linked_category_key", "linked_name",
            ):
                if not isinstance(host.get(field), str) or len(host[field]) > 260:
                    raise RevitIntegrationError("Revit category host text is invalid.")
            stable = host.get("face_stable_reference")
            if not isinstance(stable, str) or len(stable) > 4096:
                raise RevitIntegrationError("Revit category host face reference is invalid.")
            if host["is_linked"] != (host["kind"] == "linked_face"):
                raise RevitIntegrationError("Revit category host link metadata is inconsistent.")
            if host["is_linked"] and (
                host["element_id"] <= 0
                or host["linked_element_id"] <= 0
                or not host["face_stable_reference"]
            ):
                raise RevitIntegrationError("Revit linked host metadata is incomplete.")
            if host_filter == "ceiling_hosted" and not (
                host["category_key"] == "ceilings"
                or host["linked_category_key"] == "ceilings"
            ):
                raise RevitIntegrationError("Revit ceiling-hosted result contains a non-ceiling host.")
        parameters = element.get("parameters")
        if not isinstance(parameters, dict) or len(parameters) > len(ALLOWED_PARAMETER_KEYS):
            raise RevitIntegrationError("Revit category parameters are invalid.")
        if any(
            key not in ALLOWED_PARAMETER_KEYS
            or not isinstance(item, str)
            or len(item) > 260
            for key, item in parameters.items()
        ):
            raise RevitIntegrationError("Revit category parameter value is invalid.")
    if element_ids != sorted(element_ids) or len(element_ids) != len(set(element_ids)):
        raise RevitIntegrationError("Revit category elements are not uniquely sorted.")
    if element_ids and element_ids[0] <= start_after:
        raise RevitIntegrationError("Revit category page did not advance beyond its cursor.")
    if value.get("truncated") is True:
        if not element_ids or next_start_after != element_ids[-1]:
            raise RevitIntegrationError("Truncated Revit category continuation is invalid.")
    elif next_start_after != 0:
        raise RevitIntegrationError("Final Revit category page advertises a continuation.")
    return RevitCategoryQuery(
        request_id=str(value.get("request_id", "")),
        document_title=str(value.get("document_title", "")),
        category_key=category,
        host_filter=host_filter,
        total_count=total,
        returned_count=returned,
        truncated=value.get("truncated") is True,
        start_after_element_id=start_after,
        next_start_after_element_id=next_start_after,
        elements=tuple(elements),
    )
