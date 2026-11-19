"""Strict loader for bounded Revit lighting-fixture circuit relationships."""

from __future__ import annotations

import json
import math
from dataclasses import dataclass
from pathlib import Path

from agent.exceptions import RevitIntegrationError


@dataclass(frozen=True, slots=True)
class RevitLightingCircuitQuery:
    request_id: str
    document_title: str
    requested_element_id: int
    start_after_element_id: int
    next_start_after_element_id: int
    total_fixture_count: int
    returned_fixture_count: int
    truncated: bool
    fixtures: tuple[dict[str, object], ...]


def load_lighting_circuit_query(
    path: Path, *, allowed_root: Path
) -> RevitLightingCircuitQuery:
    root = allowed_root.expanduser().resolve(strict=True)
    candidate = path.expanduser().resolve(strict=True)
    if not candidate.is_file() or candidate.is_symlink() or not candidate.is_relative_to(root):
        raise RevitIntegrationError("Revit lighting circuit result escaped its allowed root.")
    if candidate.stat().st_size > 262_144:
        raise RevitIntegrationError("Revit lighting circuit result exceeds 256 KiB.")
    try:
        value = json.loads(candidate.read_text(encoding="utf-8-sig"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise RevitIntegrationError(f"Revit lighting circuit result is invalid JSON: {exc}") from exc
    if (
        not isinstance(value, dict)
        or value.get("schema_version") != 1
        or value.get("provider_egress_performed") is not False
        or value.get("document_mutation_performed") is not False
    ):
        raise RevitIntegrationError("Revit lighting circuit contract is invalid.")

    def bounded_text(item: object, name: str) -> str:
        if not isinstance(item, str) or len(item) > 260:
            raise RevitIntegrationError(f"Revit lighting circuit field {name} is invalid.")
        return item

    def identity(item: object, name: str, *, allow_zero: bool = False) -> int:
        minimum = 0 if allow_zero else 1
        if not isinstance(item, int) or isinstance(item, bool) or item < minimum:
            raise RevitIntegrationError(f"Revit lighting circuit identity {name} is invalid.")
        return item

    def measurement(item: object, name: str, *, optional: bool = False) -> float | None:
        if optional and item is None:
            return None
        if (
            not isinstance(item, (int, float))
            or isinstance(item, bool)
            or not math.isfinite(float(item))
            or abs(float(item)) > 1_000_000_000
        ):
            raise RevitIntegrationError(f"Revit electrical measurement {name} is invalid.")
        return float(item)

    def protection_parameters(item: object, owner: str) -> None:
        if not isinstance(item, list) or len(item) > 32:
            raise RevitIntegrationError(
                f"Revit {owner} protection-parameter evidence is invalid."
            )
        for entry in item:
            if not isinstance(entry, dict):
                raise RevitIntegrationError(
                    f"Revit {owner} protection-parameter entry is invalid."
                )
            source = bounded_text(entry.get("source"), f"{owner}.protection.source")
            if source not in {"instance", "type"}:
                raise RevitIntegrationError(
                    f"Revit {owner} protection-parameter source is invalid."
                )
            for field in ("name", "storage_type", "value_display"):
                if not bounded_text(
                    entry.get(field), f"{owner}.protection.{field}"
                ):
                    raise RevitIntegrationError(
                        f"Revit {owner} protection-parameter {field} is empty."
                    )
            parameter_id = entry.get("parameter_id")
            if (
                not isinstance(parameter_id, int)
                or isinstance(parameter_id, bool)
                or abs(parameter_id) > 10_000_000_000
            ):
                raise RevitIntegrationError(
                    f"Revit {owner} protection-parameter identity is invalid."
                )

    def incoming_systems(item: object) -> None:
        if not isinstance(item, list) or len(item) > 8:
            raise RevitIntegrationError("Revit panel incoming-system evidence is invalid.")
        for incoming in item:
            if not isinstance(incoming, dict):
                raise RevitIntegrationError("Revit panel incoming-system entry is invalid.")
            identity(incoming.get("system_id"), "incoming.system_id")
            for field in ("system_type", "circuit_number", "panel_name", "load_name", "wire_size"):
                bounded_text(incoming.get(field), f"incoming.{field}")
            for field in (
                "voltage_v", "apparent_load_va", "true_load_w", "power_factor",
                "rating_a", "frame_a", "apparent_current_a", "voltage_drop_percent", "length_mm",
            ):
                measurement(incoming.get(field), f"incoming.{field}")
            poles = incoming.get("poles")
            if not isinstance(poles, int) or isinstance(poles, bool) or not 0 <= poles <= 100:
                raise RevitIntegrationError("Revit incoming-system poles value is invalid.")
            protection_parameters(incoming.get("protection_parameters"), "incoming")
            upstream = incoming.get("upstream_panel")
            if not isinstance(upstream, dict):
                raise RevitIntegrationError("Revit upstream-panel evidence is invalid.")
            identity(upstream.get("element_id"), "upstream_panel.element_id")
            for field in ("family_name", "type_name", "short_circuit_rating_display"):
                bounded_text(upstream.get(field), f"upstream_panel.{field}")
            measurement(
                upstream.get("mcb_rating_a"), "upstream_panel.mcb_rating_a", optional=True
            )
            protection_parameters(upstream.get("protection_parameters"), "upstream_panel")

    request_id = bounded_text(value.get("request_id"), "request_id")
    document_title = bounded_text(value.get("document_title"), "document_title")
    requested = identity(value.get("requested_element_id"), "requested_element_id", allow_zero=True)
    start_after = identity(
        value.get("start_after_element_id", 0), "start_after_element_id", allow_zero=True
    )
    total = value.get("total_fixture_count")
    returned = value.get("returned_fixture_count")
    fixtures = value.get("fixtures")
    if (
        not isinstance(total, int) or isinstance(total, bool) or total < 0
        or not isinstance(returned, int) or isinstance(returned, bool) or returned < 0
        or not isinstance(fixtures, list) or returned != len(fixtures)
        or returned > 32 or total < returned
        or not isinstance(value.get("truncated"), bool)
        or (
            requested == 0
            and start_after == 0
            and value["truncated"] != (total > returned)
        )
    ):
        raise RevitIntegrationError("Revit lighting circuit counts are invalid.")
    if requested > 0 and (total != 1 or returned != 1 or value["truncated"]):
        raise RevitIntegrationError("Exact Revit lighting circuit result is incomplete.")
    fixture_ids: list[int] = []
    for fixture in fixtures:
        if not isinstance(fixture, dict):
            raise RevitIntegrationError("Revit lighting fixture circuit entry is invalid.")
        fixture_ids.append(identity(fixture.get("element_id"), "element_id"))
        for name in ("family_name", "type_name", "mark"):
            bounded_text(fixture.get(name), name)
        circuits = fixture.get("circuits")
        count = fixture.get("circuit_count")
        if not isinstance(circuits, list) or len(circuits) > 16 or count != len(circuits):
            raise RevitIntegrationError("Revit fixture circuit count is invalid.")
        for circuit in circuits:
            if not isinstance(circuit, dict):
                raise RevitIntegrationError("Revit electrical system entry is invalid.")
            identity(circuit.get("system_id"), "system_id")
            for name in ("system_type", "circuit_number", "panel_name", "load_name"):
                bounded_text(circuit.get(name), name)
            for name in ("voltage_v", "apparent_load_va", "true_load_w", "power_factor"):
                measurement(circuit.get(name), name)
            poles = circuit.get("poles")
            if not isinstance(poles, int) or isinstance(poles, bool) or not 0 <= poles <= 100:
                raise RevitIntegrationError("Revit electrical poles value is invalid.")
            for name in (
                "rating_a", "frame_a", "apparent_current_a", "true_current_a",
                "voltage_drop_percent", "apparent_current_phase_a",
                "apparent_current_phase_b", "apparent_current_phase_c", "length_mm",
            ):
                if name in circuit:
                    measurement(circuit.get(name), name)
            for name in ("phase_label", "wire_size", "circuit_notes"):
                if name in circuit:
                    bounded_text(circuit.get(name), name)
            if "protection_parameters" in circuit:
                protection_parameters(circuit.get("protection_parameters"), "circuit")
            if "balanced_load" in circuit and not isinstance(circuit.get("balanced_load"), bool):
                raise RevitIntegrationError("Revit circuit balanced-load value is invalid.")
            for name in (
                "start_slot", "hot_conductors", "neutral_conductors", "ground_conductors",
                "runs", "ways",
            ):
                item = circuit.get(name)
                if name in circuit and (
                    not isinstance(item, int) or isinstance(item, bool) or not -1 <= item <= 10000
                ):
                    raise RevitIntegrationError(f"Revit circuit integer {name} is invalid.")
            panel = circuit.get("panel")
            if panel is not None:
                if not isinstance(panel, dict):
                    raise RevitIntegrationError("Revit circuit panel evidence is invalid.")
                identity(panel.get("element_id"), "panel.element_id")
                for name in ("family_name", "type_name", "supply_from", "mains_type"):
                    bounded_text(panel.get(name), f"panel.{name}")
                if "short_circuit_rating_display" in panel:
                    bounded_text(
                        panel.get("short_circuit_rating_display"),
                        "panel.short_circuit_rating_display",
                    )
                if "protection_parameters" in panel:
                    protection_parameters(panel.get("protection_parameters"), "panel")
                if "incoming_systems" in panel:
                    incoming_systems(panel.get("incoming_systems"))
                for name in (
                    "mcb_rating_a", "voltage_v", "total_connected_current_a",
                    "total_demand_current_a", "current_phase_a", "current_phase_b",
                    "current_phase_c",
                ):
                    measurement(panel.get(name), f"panel.{name}", optional=True)
                for name in ("number_of_phases", "number_of_wires"):
                    item = panel.get(name)
                    if item is not None and (
                        not isinstance(item, int) or isinstance(item, bool) or not 0 <= item <= 100
                    ):
                        raise RevitIntegrationError(f"Revit panel integer {name} is invalid.")
            wire_type = circuit.get("wire_type")
            if wire_type is not None:
                if not isinstance(wire_type, dict):
                    raise RevitIntegrationError("Revit circuit wire-type evidence is invalid.")
                identity(wire_type.get("element_id"), "wire_type.element_id")
                for name in (
                    "name", "material", "temperature_rating", "insulation", "conduit",
                    "maximum_size", "neutral_mode",
                ):
                    bounded_text(wire_type.get(name), f"wire_type.{name}")
                ampacity = wire_type.get("maximum_size_ampacity_a")
                if not isinstance(ampacity, int) or isinstance(ampacity, bool) or not 0 <= ampacity <= 1_000_000:
                    raise RevitIntegrationError("Revit wire-type ampacity evidence is invalid.")
                if not isinstance(wire_type.get("neutral_required"), bool):
                    raise RevitIntegrationError("Revit wire-type neutral evidence is invalid.")
        connectors = fixture.get("connectors")
        connector_count = fixture.get("connector_count")
        if (
            not isinstance(connectors, list)
            or len(connectors) > 32
            or connector_count != len(connectors)
        ):
            raise RevitIntegrationError("Revit lighting connector count is invalid.")
        for connector in connectors:
            if not isinstance(connector, dict):
                raise RevitIntegrationError("Revit lighting connector entry is invalid.")
            connector_id = connector.get("connector_id")
            system_id = connector.get("mep_system_id")
            if (
                not isinstance(connector_id, int) or isinstance(connector_id, bool) or connector_id < 0
                or not isinstance(system_id, int) or isinstance(system_id, bool) or system_id < -1
            ):
                raise RevitIntegrationError("Revit lighting connector identity is invalid.")
            for name in ("domain", "connector_type", "electrical_system_type"):
                bounded_text(connector.get(name), name)
        for name in ("voltage_v", "apparent_load_va"):
            measurement(fixture.get(name), name, optional=True)
    if fixture_ids != sorted(set(fixture_ids)) or any(item <= start_after for item in fixture_ids):
        raise RevitIntegrationError("Revit lighting circuit page identities are invalid.")
    default_next = fixture_ids[-1] if value["truncated"] and fixture_ids else 0
    next_after = identity(
        value.get("next_start_after_element_id", default_next),
        "next_start_after_element_id",
        allow_zero=True,
    )
    if requested > 0 and (start_after != 0 or next_after != 0):
        raise RevitIntegrationError("Exact Revit lighting circuit result cannot be paged.")
    if requested == 0:
        if value["truncated"] and (not fixture_ids or next_after != fixture_ids[-1]):
            raise RevitIntegrationError("Revit lighting circuit page continuation is invalid.")
        if not value["truncated"] and next_after != 0:
            raise RevitIntegrationError("Final Revit lighting circuit page has a continuation.")
    return RevitLightingCircuitQuery(
        request_id=request_id,
        document_title=document_title,
        requested_element_id=requested,
        start_after_element_id=start_after,
        next_start_after_element_id=next_after,
        total_fixture_count=total,
        returned_fixture_count=returned,
        truncated=value["truncated"],
        fixtures=tuple(fixtures),
    )
