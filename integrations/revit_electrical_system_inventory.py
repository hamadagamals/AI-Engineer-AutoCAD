"""Merge complete Revit electrical-system pages into a source-bound inventory."""

from __future__ import annotations

import hashlib
import json
from collections import Counter
from datetime import UTC, datetime
from pathlib import Path

from agent.exceptions import RevitIntegrationError
from integrations.revit_electrical_system_query import (
    RevitElectricalSystemQuery,
    load_electrical_system_query,
)


def create_electrical_system_inventory(
    *,
    workspace_root: Path,
    bridge_root: Path,
    source_model: Path,
    page_paths: list[Path],
    output_path: Path,
) -> dict[str, object]:
    """Validate one complete page chain, classify systems, and write atomically."""
    workspace = workspace_root.expanduser().resolve(strict=True)
    bridge = bridge_root.expanduser().resolve(strict=True)
    source = source_model.expanduser().resolve(strict=True)
    output = output_path.expanduser().resolve(strict=False)
    if not source.is_file() or source.is_symlink() or not source.is_relative_to(workspace):
        raise RevitIntegrationError("Revit electrical-system source must be a workspace file.")
    if output.exists() or not output.parent.resolve(strict=True).is_relative_to(workspace):
        raise RevitIntegrationError("Revit electrical-system inventory output is unsafe or exists.")
    if not page_paths:
        raise RevitIntegrationError("Revit electrical-system inventory requires page files.")
    pages = tuple(load_electrical_system_query(path, allowed_root=bridge) for path in page_paths)
    systems = _merge_pages(pages)
    classifications = Counter(_classify(entry) for entry in systems)
    panels = Counter(
        str(entry["circuit"]["panel_name"])
        for entry in systems
        if str(entry["circuit"].get("panel_name", "")).strip()
    )
    enriched = []
    for entry in systems:
        item = dict(entry)
        item["classification"] = _classify(entry)
        enriched.append(item)
    result: dict[str, object] = {
        "schema_version": 1,
        "status": "complete",
        "created_utc": datetime.now(UTC).isoformat(),
        "document_title": pages[0].document_title,
        "source_model": str(source.relative_to(workspace)),
        "source_rvt_sha256": _sha256(source),
        "page_count": len(pages),
        "page_request_ids": [page.request_id for page in pages],
        "electrical_system_count": len(enriched),
        "power_circuit_count": sum(
            1 for entry in enriched if entry["circuit"]["system_type"] == "PowerCircuit"
        ),
        "classification_counts": dict(sorted(classifications.items())),
        "panel_circuit_counts": dict(sorted(panels.items())),
        "systems": enriched,
        "limitations": [
            "Classification is deterministic from Revit system type, member categories, and load-classification text; it is not code approval.",
            "Empty or unpanelled systems remain explicit and are not silently inserted into a panel schedule.",
            "Modelled length and voltage drop are Revit observations, not verified installed-route or compliance conclusions.",
        ],
        "provider_egress_performed": False,
        "document_mutation_performed": False,
    }
    result["inventory_sha256"] = _json_hash(result)
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_name(f".{output.name}.tmp")
    temporary.write_text(json.dumps(result, ensure_ascii=False, indent=2, sort_keys=True), encoding="utf-8")
    temporary.replace(output)
    return result


def _merge_pages(pages: tuple[RevitElectricalSystemQuery, ...]) -> list[dict[str, object]]:
    if not pages or pages[0].start_after_element_id != 0:
        raise RevitIntegrationError("Revit electrical-system pages must start at cursor zero.")
    document = pages[0].document_title
    total = pages[0].total_system_count
    expected_cursor = 0
    systems: list[dict[str, object]] = []
    seen: set[int] = set()
    for index, page in enumerate(pages):
        if (
            page.document_title != document
            or page.total_system_count != total
            or page.start_after_element_id != expected_cursor
        ):
            raise RevitIntegrationError("Revit electrical-system page chain is inconsistent.")
        for entry in page.systems:
            system_id = int(entry["circuit"]["system_id"])
            if system_id in seen:
                raise RevitIntegrationError("Revit electrical-system page chain has a duplicate.")
            seen.add(system_id)
            systems.append(entry)
        expected_cursor = page.next_start_after_element_id
        if index < len(pages) - 1 and not page.truncated:
            raise RevitIntegrationError("Revit electrical-system page chain ended early.")
    if pages[-1].truncated or expected_cursor != 0 or len(systems) != total:
        raise RevitIntegrationError("Revit electrical-system page chain is incomplete.")
    ids = [int(entry["circuit"]["system_id"]) for entry in systems]
    if ids != sorted(ids):
        raise RevitIntegrationError("Revit electrical-system inventory is not ordered.")
    return systems


def _classify(entry: dict[str, object]) -> str:
    circuit = entry["circuit"]
    assert isinstance(circuit, dict)
    if circuit.get("system_type") != "PowerCircuit":
        return "non_power_system"
    categories = {
        str(item["category_key"]): int(item["count"])
        for item in entry.get("member_category_counts", [])
    }
    member_count = int(entry.get("member_count", 0))
    searchable = " ".join(
        str(value).lower()
        for value in (
            circuit.get("load_name", ""), circuit.get("circuit_notes", ""),
            entry.get("load_classifications", ""),
        )
    )
    if member_count == 0 or any(term in searchable for term in ("spare", "space")):
        return "spare_or_empty"
    has_lighting = categories.get("lighting_fixtures", 0) > 0
    has_receptacles = categories.get("electrical_fixtures", 0) > 0
    has_equipment = any(
        categories.get(key, 0) > 0
        for key in ("electrical_equipment", "mechanical_equipment", "speciality_equipment")
    )
    if sum((has_lighting, has_receptacles, has_equipment)) > 1:
        return "mixed_power"
    if has_lighting or "light" in searchable:
        return "lighting"
    if has_receptacles or any(term in searchable for term in ("recept", "socket", "outlet")):
        return "receptacles"
    if categories.get("electrical_equipment", 0) > 0:
        return "feeder_or_panel"
    if has_equipment or any(term in searchable for term in ("motor", "hvac", "mechanical", "equipment")):
        return "equipment_power"
    return "other_power"


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def _json_hash(value: dict[str, object]) -> str:
    encoded = json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
    return hashlib.sha256(encoded.encode("utf-8")).hexdigest().upper()
