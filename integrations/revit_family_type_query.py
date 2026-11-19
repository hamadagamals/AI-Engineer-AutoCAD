"""Strict request and result boundary for loaded Revit electrical family types."""

from __future__ import annotations

import json
import os
import uuid
from dataclasses import dataclass
from pathlib import Path

from agent.exceptions import RevitIntegrationError
from integrations.revit_category_query import ALLOWED_CATEGORIES
from integrations.revit_request_bridge import default_bridge_root


FAMILY_TYPE_CATEGORIES = frozenset({
    "lighting_fixtures", "lighting_devices",
    "electrical_fixtures", "electrical_equipment",
})
ALLOWED_HOST_REQUIREMENTS = frozenset({
    "level", "hosted_element_unresolved", "two_levels", "work_plane_or_face",
    "view", "curve", "detail_curve", "unsupported_or_unresolved",
})


@dataclass(frozen=True, slots=True)
class RevitFamilyTypeQuery:
    request_id: str
    document_title: str
    category_key: str
    total_count: int
    returned_count: int
    truncated: bool
    types: tuple[dict[str, object], ...]


def submit_family_type_request(
    expected_document_title: str,
    category_key: str,
    *,
    maximum_elements: int = 64,
    bridge_root: Path | None = None,
) -> tuple[str, Path]:
    if category_key not in FAMILY_TYPE_CATEGORIES or category_key not in ALLOWED_CATEGORIES:
        raise RevitIntegrationError("Revit family-type category is not allowlisted.")
    if not isinstance(maximum_elements, int) or not 1 <= maximum_elements <= 64:
        raise RevitIntegrationError("Revit family-type limit must be within 1..64.")
    title = expected_document_title.strip()
    if not title or len(title) > 260:
        raise RevitIntegrationError("Expected Revit document title is invalid.")
    root = (bridge_root or default_bridge_root()).expanduser().resolve(strict=False)
    pending = root / "requests" / "pending.json"
    if pending.exists():
        raise RevitIntegrationError("A Revit read-only request is already pending.")
    request_id = str(uuid.uuid4())
    value = {
        "schema_version": 1,
        "request_id": request_id,
        "action": "family_type_query",
        "expected_document_title": title,
        "provider_egress_permitted": False,
        "document_mutation_permitted": False,
        "category_key": category_key,
        "max_elements": maximum_elements,
    }
    pending.parent.mkdir(parents=True, exist_ok=True)
    response_path = root / "responses" / f"{request_id}.json"
    response_path.parent.mkdir(parents=True, exist_ok=True)
    temporary = pending.with_name(f".{request_id}.tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False), encoding="utf-8")
    os.replace(temporary, pending)
    return request_id, response_path


def load_family_type_query(path: Path, *, allowed_root: Path) -> RevitFamilyTypeQuery:
    root = allowed_root.expanduser().resolve(strict=True)
    candidate = path.expanduser().resolve(strict=True)
    if not candidate.is_file() or candidate.is_symlink() or not candidate.is_relative_to(root):
        raise RevitIntegrationError("Revit family-type result escaped its configured root.")
    if candidate.stat().st_size > 262_144:
        raise RevitIntegrationError("Revit family-type result exceeds 256 KiB.")
    value = json.loads(candidate.read_text(encoding="utf-8-sig"))
    if not isinstance(value, dict) or value.get("schema_version") != 1:
        raise RevitIntegrationError("Revit family-type result schema is invalid.")
    if value.get("provider_egress_performed") is not False or value.get("document_mutation_performed") is not False:
        raise RevitIntegrationError("Revit family-type result violated the read-only boundary.")
    category = value.get("category_key")
    types = value.get("types")
    total = value.get("total_count")
    returned = value.get("returned_count")
    if category not in FAMILY_TYPE_CATEGORIES or not isinstance(types, list) or len(types) > 64:
        raise RevitIntegrationError("Revit family-type result is outside the bounded contract.")
    if not isinstance(total, int) or not isinstance(returned, int) or returned != len(types) or total < returned:
        raise RevitIntegrationError("Revit family-type result counts are invalid.")
    for item in types:
        if not isinstance(item, dict) or not isinstance(item.get("symbol_id"), int) or not isinstance(item.get("family_id"), int):
            raise RevitIntegrationError("Revit family-type identity is invalid.")
        for field in ("category", "family_name", "type_name", "family_placement_type"):
            if not isinstance(item.get(field), str) or len(item[field]) > 260:
                raise RevitIntegrationError(f"Revit family-type field {field} is invalid.")
        if item.get("host_requirement") not in ALLOWED_HOST_REQUIREMENTS or not isinstance(item.get("is_active"), bool):
            raise RevitIntegrationError("Revit family-type placement metadata is invalid.")
    return RevitFamilyTypeQuery(
        request_id=str(value.get("request_id", "")),
        document_title=str(value.get("document_title", "")),
        category_key=str(category),
        total_count=total,
        returned_count=returned,
        truncated=value.get("truncated") is True,
        types=tuple(types),
    )
