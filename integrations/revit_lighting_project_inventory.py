"""Build a source-bound project inventory from complete Revit lighting-circuit pages."""

from __future__ import annotations

import hashlib
import json
import uuid
from datetime import UTC, datetime
from pathlib import Path
from typing import Sequence

from agent.exceptions import RevitIntegrationError
from integrations.revit_lighting_circuit_query import (
    RevitLightingCircuitQuery,
    load_lighting_circuit_query,
)


def create_lighting_project_inventory(
    *,
    workspace_root: Path,
    bridge_root: Path,
    source_model: Path,
    page_paths: Sequence[Path],
    output_path: Path,
) -> dict[str, object]:
    """Validate complete ordered pages and write one immutable inventory."""
    workspace = workspace_root.expanduser().resolve(strict=True)
    bridge = bridge_root.expanduser().resolve(strict=True)
    source = source_model.expanduser().resolve(strict=True)
    output = output_path.expanduser().resolve(strict=False)
    if (
        not source.is_file()
        or source.is_symlink()
        or not source.is_relative_to(workspace)
        or source.suffix.lower() != ".rvt"
        or output.exists()
        or output.suffix.lower() != ".json"
        or not output.parent.resolve(strict=True).is_relative_to(workspace)
        or not 1 <= len(page_paths) <= 4096
    ):
        raise RevitIntegrationError("Revit lighting project inventory paths are invalid.")
    pages = tuple(
        load_lighting_circuit_query(path, allowed_root=bridge) for path in page_paths
    )
    report = merge_lighting_circuit_pages(pages)
    if report["document_title"] != source.stem:
        raise RevitIntegrationError("Revit lighting pages do not match the source model title.")
    report.update(
        {
            "inventory_id": str(uuid.uuid4()),
            "created_utc": datetime.now(UTC).isoformat().replace("+00:00", "Z"),
            "source_rvt_sha256": _sha256_file(source),
            "source_model": source.name,
        }
    )
    report["inventory_sha256"] = _self_hash(report)
    temporary = output.with_name(f".{output.name}.{uuid.uuid4().hex}.tmp")
    try:
        temporary.write_text(
            json.dumps(report, ensure_ascii=False, sort_keys=True, indent=2) + "\n",
            encoding="utf-8",
        )
        temporary.replace(output)
    finally:
        if temporary.exists():
            temporary.unlink()
    return report


def merge_lighting_circuit_pages(
    pages: Sequence[RevitLightingCircuitQuery],
) -> dict[str, object]:
    """Merge already validated pages and fail closed on any gap or conflict."""
    if not pages:
        raise RevitIntegrationError("At least one Revit lighting circuit page is required.")
    title = pages[0].document_title
    total = pages[0].total_fixture_count
    expected_start = 0
    fixtures: list[dict[str, object]] = []
    seen_fixture_ids: set[int] = set()
    page_requests: list[str] = []
    for index, page in enumerate(pages):
        if (
            page.document_title != title
            or page.requested_element_id != 0
            or page.total_fixture_count != total
            or page.start_after_element_id != expected_start
        ):
            raise RevitIntegrationError("Revit lighting circuit page chain is inconsistent.")
        if index < len(pages) - 1 and not page.truncated:
            raise RevitIntegrationError("A non-final Revit lighting circuit page ended early.")
        if index == len(pages) - 1 and page.truncated:
            raise RevitIntegrationError("Revit lighting circuit page chain is incomplete.")
        for fixture in page.fixtures:
            element_id = int(fixture["element_id"])
            if element_id in seen_fixture_ids:
                raise RevitIntegrationError("Revit lighting circuit pages overlap.")
            seen_fixture_ids.add(element_id)
            fixtures.append(fixture)
        page_requests.append(page.request_id)
        expected_start = page.next_start_after_element_id
    fixture_ids = [int(item["element_id"]) for item in fixtures]
    if len(fixtures) != total or fixture_ids != sorted(fixture_ids) or expected_start != 0:
        raise RevitIntegrationError("Revit lighting circuit pages are not a complete inventory.")

    circuits: dict[int, dict[str, object]] = {}
    circuit_fixture_ids: dict[int, list[int]] = {}
    panels: dict[int, dict[str, object]] = {}
    fixture_types: dict[tuple[str, str], dict[str, object]] = {}
    unassigned = 0
    for fixture in fixtures:
        key = (str(fixture["family_name"]), str(fixture["type_name"]))
        quantity = fixture_types.setdefault(
            key,
            {
                "family_name": key[0],
                "type_name": key[1],
                "quantity": 0,
                "connected_quantity": 0,
                "unassigned_quantity": 0,
            },
        )
        quantity["quantity"] = int(quantity["quantity"]) + 1
        fixture_circuits = fixture["circuits"]
        if fixture_circuits:
            quantity["connected_quantity"] = int(quantity["connected_quantity"]) + 1
        else:
            quantity["unassigned_quantity"] = int(quantity["unassigned_quantity"]) + 1
            unassigned += 1
        for circuit in fixture_circuits:
            system_id = int(circuit["system_id"])
            existing = circuits.get(system_id)
            if existing is not None and _canonical(existing) != _canonical(circuit):
                raise RevitIntegrationError("A Revit circuit changed across inventory pages.")
            circuits[system_id] = circuit
            circuit_fixture_ids.setdefault(system_id, []).append(int(fixture["element_id"]))
            panel = circuit.get("panel")
            if isinstance(panel, dict):
                panel_id = int(panel["element_id"])
                existing_panel = panels.get(panel_id)
                if existing_panel is not None and _canonical(existing_panel) != _canonical(panel):
                    raise RevitIntegrationError("A Revit panel changed across inventory pages.")
                panels[panel_id] = panel

    circuit_rows: list[dict[str, object]] = []
    cable_rows: list[dict[str, object]] = []
    for system_id in sorted(circuits):
        circuit = circuits[system_id]
        length_m = float(circuit.get("length_mm", 0.0)) / 1000.0
        runs = int(circuit.get("runs", 0))
        hot = int(circuit.get("hot_conductors", 0))
        neutral = int(circuit.get("neutral_conductors", 0))
        ground = int(circuit.get("ground_conductors", 0))
        circuit_rows.append(
            {
                "system_id": system_id,
                "panel_name": circuit["panel_name"],
                "circuit_number": circuit["circuit_number"],
                "load_name": circuit["load_name"],
                "phase_label": circuit.get("phase_label", ""),
                "poles": circuit["poles"],
                "voltage_v": circuit["voltage_v"],
                "apparent_load_va": circuit["apparent_load_va"],
                "true_load_w": circuit["true_load_w"],
                "power_factor": circuit["power_factor"],
                "apparent_current_a": circuit.get("apparent_current_a"),
                "rating_a": circuit.get("rating_a"),
                "frame_a": circuit.get("frame_a"),
                "voltage_drop_percent": circuit.get("voltage_drop_percent"),
                "wire_size": circuit.get("wire_size", ""),
                "length_m": round(length_m, 6),
                "runs": runs,
                "ways": circuit.get("ways"),
                "fixture_count": len(circuit_fixture_ids[system_id]),
                "fixture_element_ids": sorted(circuit_fixture_ids[system_id]),
            }
        )
        cable_rows.append(
            {
                "system_id": system_id,
                "panel_name": circuit["panel_name"],
                "circuit_number": circuit["circuit_number"],
                "wire_size": circuit.get("wire_size", ""),
                "modelled_route_length_m": round(length_m, 6),
                "runs": runs,
                "hot_conductors": hot,
                "neutral_conductors": neutral,
                "ground_conductors": ground,
                "hot_length_m": round(length_m * runs * hot, 6),
                "neutral_length_m": round(length_m * runs * neutral, 6),
                "ground_length_m": round(length_m * runs * ground, 6),
                "total_conductor_length_m": round(length_m * runs * (hot + neutral + ground), 6),
                "quantity_status": "modelled_length_only",
            }
        )

    return {
        "schema_version": 1,
        "status": "complete_read_only_lighting_inventory",
        "document_title": title,
        "page_count": len(pages),
        "page_request_ids": page_requests,
        "fixture_count": len(fixtures),
        "connected_fixture_count": len(fixtures) - unassigned,
        "unassigned_fixture_count": unassigned,
        "unique_circuit_count": len(circuits),
        "unique_panel_count": len(panels),
        "fixtures": fixtures,
        "fixture_quantities": sorted(fixture_types.values(), key=lambda item: (str(item["family_name"]), str(item["type_name"]))),
        "circuits": circuit_rows,
        "panels": [panels[key] for key in sorted(panels)],
        "cable_quantities": cable_rows,
        "quantity_limitations": [
            "Circuit length is the Revit modelled ElectricalSystem.Length, not verified installed route length.",
            "Conductor quantities exclude vertical allowances, terminations, slack, waste, and parallel-route verification.",
            "Conduit, cable tray, boxes, switches, receptacles, and non-lighting equipment are outside this lighting-circuit query.",
            "No code-compliance, breaker coordination, short-circuit, or final cable-size approval is performed.",
        ],
        "provider_egress_performed": False,
        "document_mutation_performed": False,
    }


def _canonical(value: object) -> str:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"))


def _self_hash(value: dict[str, object]) -> str:
    return hashlib.sha256(_canonical(value).encode("utf-8")).hexdigest().upper()


def _sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while block := stream.read(65_536):
            digest.update(block)
    return digest.hexdigest().upper()
