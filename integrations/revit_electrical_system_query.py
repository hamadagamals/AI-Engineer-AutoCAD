"""Strict loader for bounded read-only Revit electrical-system pages."""

from __future__ import annotations

import json
import math
from dataclasses import dataclass
from pathlib import Path

from agent.exceptions import RevitIntegrationError


@dataclass(frozen=True, slots=True)
class RevitElectricalSystemQuery:
    request_id: str
    document_title: str
    start_after_element_id: int
    next_start_after_element_id: int
    total_system_count: int
    returned_system_count: int
    truncated: bool
    systems: tuple[dict[str, object], ...]


def load_electrical_system_query(
    path: Path, *, allowed_root: Path
) -> RevitElectricalSystemQuery:
    """Load and fail closed on malformed, escaped, or non-progressing pages."""
    root = allowed_root.expanduser().resolve(strict=True)
    candidate = path.expanduser().resolve(strict=True)
    if not candidate.is_file() or candidate.is_symlink() or not candidate.is_relative_to(root):
        raise RevitIntegrationError("Revit electrical-system result escaped its allowed root.")
    if candidate.stat().st_size > 1_048_576:
        raise RevitIntegrationError("Revit electrical-system result exceeds 1 MiB.")
    try:
        value = json.loads(candidate.read_text(encoding="utf-8-sig"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise RevitIntegrationError(f"Revit electrical-system result is invalid JSON: {exc}") from exc
    if (
        not isinstance(value, dict)
        or value.get("schema_version") != 1
        or value.get("provider_egress_performed") is not False
        or value.get("document_mutation_performed") is not False
    ):
        raise RevitIntegrationError("Revit electrical-system contract is invalid.")

    def text(item: object, name: str) -> str:
        if not isinstance(item, str) or len(item) > 260:
            raise RevitIntegrationError(f"Revit electrical-system field {name} is invalid.")
        return item

    def identity(item: object, name: str, *, allow_zero: bool = False) -> int:
        minimum = 0 if allow_zero else 1
        if not isinstance(item, int) or isinstance(item, bool) or item < minimum:
            raise RevitIntegrationError(f"Revit electrical-system identity {name} is invalid.")
        return item

    def number(item: object, name: str) -> float:
        if (
            not isinstance(item, (int, float))
            or isinstance(item, bool)
            or not math.isfinite(float(item))
            or abs(float(item)) > 1_000_000_000
        ):
            raise RevitIntegrationError(f"Revit electrical-system measurement {name} is invalid.")
        return float(item)

    request_id = text(value.get("request_id"), "request_id")
    document_title = text(value.get("document_title"), "document_title")
    start_after = identity(value.get("start_after_element_id", 0), "start_after", allow_zero=True)
    next_after = identity(value.get("next_start_after_element_id", 0), "next_start_after", allow_zero=True)
    total = value.get("total_system_count")
    returned = value.get("returned_system_count")
    systems = value.get("systems")
    truncated = value.get("truncated")
    if (
        not isinstance(total, int) or isinstance(total, bool) or total < 0
        or not isinstance(returned, int) or isinstance(returned, bool) or not 0 <= returned <= 32
        or not isinstance(systems, list) or returned != len(systems) or total < returned
        or not isinstance(truncated, bool)
        or (start_after == 0 and truncated != (total > returned))
    ):
        raise RevitIntegrationError("Revit electrical-system counts are invalid.")
    system_ids: list[int] = []
    for entry in systems:
        if not isinstance(entry, dict):
            raise RevitIntegrationError("Revit electrical-system entry is invalid.")
        circuit = entry.get("circuit")
        if not isinstance(circuit, dict):
            raise RevitIntegrationError("Revit electrical-system circuit is invalid.")
        system_ids.append(identity(circuit.get("system_id"), "system_id"))
        for field in (
            "system_type", "circuit_number", "panel_name", "load_name", "phase_label",
            "wire_size", "circuit_notes",
        ):
            text(circuit.get(field, ""), field)
        for field in (
            "voltage_v", "apparent_load_va", "true_load_w", "power_factor", "rating_a",
            "frame_a", "apparent_current_a", "true_current_a", "voltage_drop_percent",
            "apparent_current_phase_a", "apparent_current_phase_b", "apparent_current_phase_c",
            "length_mm",
        ):
            number(circuit.get(field, 0), field)
        for field in (
            "poles", "start_slot", "hot_conductors", "neutral_conductors", "ground_conductors",
            "runs", "ways",
        ):
            item = circuit.get(field, 0)
            if not isinstance(item, int) or isinstance(item, bool) or not -1 <= item <= 10000:
                raise RevitIntegrationError(f"Revit electrical-system integer {field} is invalid.")
        if not isinstance(circuit.get("balanced_load", False), bool):
            raise RevitIntegrationError("Revit electrical-system balanced-load value is invalid.")
        text(entry.get("load_classifications"), "load_classifications")
        member_count = entry.get("member_count")
        category_counts = entry.get("member_category_counts")
        if (
            not isinstance(member_count, int) or isinstance(member_count, bool) or member_count < 0
            or not isinstance(category_counts, list) or len(category_counts) > 64
        ):
            raise RevitIntegrationError("Revit electrical-system member evidence is invalid.")
        counted = 0
        category_keys: list[str] = []
        for category in category_counts:
            if not isinstance(category, dict):
                raise RevitIntegrationError("Revit electrical-system category evidence is invalid.")
            category_keys.append(text(category.get("category_key"), "category_key"))
            count = category.get("count")
            if not isinstance(count, int) or isinstance(count, bool) or count < 1:
                raise RevitIntegrationError("Revit electrical-system category count is invalid.")
            counted += count
        if category_keys != sorted(set(category_keys)) or counted != member_count:
            raise RevitIntegrationError("Revit electrical-system member counts do not reconcile.")
    if system_ids != sorted(set(system_ids)) or any(item <= start_after for item in system_ids):
        raise RevitIntegrationError("Revit electrical-system page identities are invalid.")
    if truncated and (not system_ids or next_after != system_ids[-1]):
        raise RevitIntegrationError("Revit electrical-system page continuation is invalid.")
    if not truncated and next_after != 0:
        raise RevitIntegrationError("Final Revit electrical-system page has a continuation.")
    return RevitElectricalSystemQuery(
        request_id=request_id,
        document_title=document_title,
        start_after_element_id=start_after,
        next_start_after_element_id=next_after,
        total_system_count=total,
        returned_system_count=returned,
        truncated=truncated,
        systems=tuple(systems),
    )
