"""Strict loader for bounded Revit linked-ceiling face geometry candidates."""

from __future__ import annotations

import json
import math
from dataclasses import dataclass
from pathlib import Path

from agent.exceptions import RevitIntegrationError


@dataclass(frozen=True, slots=True)
class RevitHostFaceQuery:
    request_id: str
    document_title: str
    reference_element_id: int
    link_instance_id: int
    linked_element_id: int
    linked_document_title: str
    face_stable_reference: str
    face_area_mm2: float
    mesh_vertices: int
    mesh_triangles: int
    candidates: tuple[dict[str, float], ...]


def load_host_face_query(path: Path, *, allowed_root: Path) -> RevitHostFaceQuery:
    root = allowed_root.expanduser().resolve(strict=True)
    candidate = path.expanduser().resolve(strict=True)
    if not candidate.is_file() or candidate.is_symlink() or not candidate.is_relative_to(root):
        raise RevitIntegrationError("Revit host-face result escaped its configured root.")
    if candidate.stat().st_size > 65_536:
        raise RevitIntegrationError("Revit host-face result exceeds 64 KiB.")
    value = json.loads(candidate.read_text(encoding="utf-8-sig"))
    if not isinstance(value, dict) or value.get("schema_version") != 1:
        raise RevitIntegrationError("Revit host-face result schema is invalid.")
    if value.get("provider_egress_performed") is not False or value.get("document_mutation_performed") is not False:
        raise RevitIntegrationError("Revit host-face result violated the read-only boundary.")
    for field in ("reference_element_id", "link_instance_id", "linked_element_id"):
        item = value.get(field)
        if not isinstance(item, int) or isinstance(item, bool) or item <= 0:
            raise RevitIntegrationError("Revit host-face identity is invalid.")
    for field, maximum in (
        ("request_id", 64), ("document_title", 260),
        ("linked_document_title", 260), ("face_stable_reference", 4096),
    ):
        item = value.get(field)
        if not isinstance(item, str) or not item or len(item) > maximum:
            raise RevitIntegrationError(f"Revit host-face field {field} is invalid.")
    if value.get("linked_category_key") != "ceilings":
        raise RevitIntegrationError("Revit host-face result is not bound to a ceiling.")
    area = value.get("face_area_mm2")
    vertices = value.get("mesh_vertices")
    triangles = value.get("mesh_triangles")
    items = value.get("candidates")
    if (
        not isinstance(area, (int, float)) or isinstance(area, bool)
        or not math.isfinite(float(area)) or area <= 0
        or not isinstance(vertices, int) or isinstance(vertices, bool) or not 3 <= vertices <= 512
        or not isinstance(triangles, int) or isinstance(triangles, bool) or not 1 <= triangles <= 512
        or not isinstance(items, list) or not 1 <= len(items) <= 8
        or value.get("candidate_count") != len(items)
    ):
        raise RevitIntegrationError("Revit host-face geometry bounds are invalid.")
    numeric_fields = (
        "x_mm", "y_mm", "z_mm", "normal_x", "normal_y", "normal_z",
        "nearest_fixture_clearance_mm",
    )
    for item in items:
        if not isinstance(item, dict) or set(item) != set(numeric_fields):
            raise RevitIntegrationError("Revit host-face candidate fields are invalid.")
        if any(
            not isinstance(item.get(field), (int, float))
            or isinstance(item.get(field), bool)
            or not math.isfinite(float(item[field]))
            or abs(float(item[field])) > 1_000_000_000
            for field in numeric_fields
        ):
            raise RevitIntegrationError("Revit host-face candidate value is invalid.")
        normal_length = math.sqrt(sum(float(item[field]) ** 2 for field in ("normal_x", "normal_y", "normal_z")))
        if not 0.999 <= normal_length <= 1.001 or item["nearest_fixture_clearance_mm"] < 0:
            raise RevitIntegrationError("Revit host-face candidate normal or clearance is invalid.")
    return RevitHostFaceQuery(
        request_id=value["request_id"], document_title=value["document_title"],
        reference_element_id=value["reference_element_id"], link_instance_id=value["link_instance_id"],
        linked_element_id=value["linked_element_id"], linked_document_title=value["linked_document_title"],
        face_stable_reference=value["face_stable_reference"], face_area_mm2=float(area),
        mesh_vertices=vertices, mesh_triangles=triangles, candidates=tuple(items),
    )
