"""Deterministic, evidence-bound review of one Revit lighting circuit."""

from __future__ import annotations

import hashlib
import json
import math
import uuid
from datetime import UTC, datetime
from pathlib import Path

from agent.exceptions import RevitIntegrationError
from integrations.revit_circuit_assignment_preview import load_circuit_assignment_preview
from integrations.revit_lighting_circuit_query import load_lighting_circuit_query


def create_circuit_engineering_review(
    *,
    workspace_root: Path,
    bridge_root: Path,
    source_model: Path,
    approved_preview_path: Path,
    post_assignment_query_path: Path,
    output_path: Path,
) -> dict[str, object]:
    """Create a self-hashed review without applying code-compliance thresholds."""
    workspace = workspace_root.resolve(strict=True)
    source = source_model.resolve(strict=True)
    output = output_path.resolve(strict=False)
    if (
        not source.is_file()
        or source.is_symlink()
        or not source.is_relative_to(workspace)
        or source.suffix.lower() != ".rvt"
        or output.exists()
        or output.suffix.lower() != ".json"
        or not output.parent.resolve(strict=True).is_relative_to(workspace)
    ):
        raise RevitIntegrationError("Revit circuit engineering review paths are invalid.")
    preview = load_circuit_assignment_preview(
        approved_preview_path, workspace_root=workspace, require_unexpired=False
    )
    query = load_lighting_circuit_query(post_assignment_query_path, allowed_root=bridge_root)
    if query.requested_element_id != preview["fixture_element_id"] or len(query.fixtures) != 1:
        raise RevitIntegrationError("Revit circuit review fixture evidence does not match the preview.")
    fixture = query.fixtures[0]
    circuits = fixture["circuits"]
    if len(circuits) != 1 or circuits[0]["system_id"] != preview["target_system_id"]:
        raise RevitIntegrationError("Revit circuit review membership does not match the preview.")
    circuit = circuits[0]
    panel = circuit.get("panel")
    if not isinstance(panel, dict):
        raise RevitIntegrationError("Revit circuit review requires panel evidence.")
    before = preview["target_system"]
    voltage = _positive(circuit.get("voltage_v"), "voltage_v")
    rating = _positive(circuit.get("rating_a"), "rating_a")
    apparent_current = _nonnegative(circuit.get("apparent_current_a"), "apparent_current_a")
    true_current = _nonnegative(circuit.get("true_current_a"), "true_current_a")
    after_va = _nonnegative(circuit.get("apparent_load_va"), "apparent_load_va")
    before_va = _nonnegative(before.get("apparent_load_va"), "before.apparent_load_va")
    after_w = _nonnegative(circuit.get("true_load_w"), "true_load_w")
    before_w = _nonnegative(before.get("true_load_w"), "before.true_load_w")
    phase_label = str(circuit.get("phase_label", ""))
    phase_currents = {
        phase: _optional_nonnegative(panel.get(f"current_phase_{phase.lower()}"), f"panel.current_phase_{phase.lower()}")
        for phase in ("A", "B", "C")
    }
    available_phases = {key: value for key, value in phase_currents.items() if value is not None}
    phase_review = _phase_review(available_phases)
    delta_current = (after_va - before_va) / voltage
    estimated_before_phases = dict(available_phases)
    if phase_label in estimated_before_phases:
        estimated_before_phases[phase_label] = max(0.0, estimated_before_phases[phase_label] - delta_current)
    estimated_before_review = _phase_review(estimated_before_phases)
    mcb = _optional_positive(panel.get("mcb_rating_a"), "panel.mcb_rating_a")
    connected = _optional_nonnegative(
        panel.get("total_connected_current_a"), "panel.total_connected_current_a"
    )
    demand = _optional_nonnegative(panel.get("total_demand_current_a"), "panel.total_demand_current_a")
    wire_type = circuit.get("wire_type")
    wire_type_complete = isinstance(wire_type, dict) and all(
        isinstance(wire_type.get(name), str) and bool(str(wire_type.get(name)).strip())
        for name in ("name", "material", "temperature_rating", "insulation")
    )
    short_circuit_rating = str(panel.get("short_circuit_rating_display", "")).strip()
    circuit_protection = circuit.get("protection_parameters", [])
    panel_protection = panel.get("protection_parameters", [])
    incoming_reviews = _incoming_system_reviews(panel.get("incoming_systems", []))
    unresolved = [
        "Confirm the governing Egyptian code edition and project voltage-drop criterion.",
        "Verify installation method, ambient correction, grouping, and derated cable ampacity; WireType maximum-size ampacity is not the selected conductor's derated ampacity.",
        "Verify breaker curve, fault current, selectivity, and protection coordination.",
        "Confirm Revit panel-current semantics and phase-balance acceptance criteria with the responsible engineer.",
        "Confirm the model's 120/208 V system is applicable to the intended Egyptian project.",
    ]
    if not wire_type_complete:
        unresolved.append("Conductor material, temperature rating, or insulation is missing from the Revit WireType evidence.")
    if not short_circuit_rating:
        unresolved.append("Panel short-circuit rating is missing from the Revit model.")
    if any(item["positive_rating_margin_observed"] is False for item in incoming_reviews):
        unresolved.append(
            "At least one Revit upstream feeder has apparent current above its modelled rating; verify circuit sizing and model data before engineering use."
        )
    if any(float(item["voltage_drop_percent"]) > 10.0 for item in incoming_reviews):
        unresolved.append(
            "At least one Revit upstream feeder reports modelled voltage drop above 10%; investigate the model value without treating 10% as the project acceptance criterion."
        )
    created = datetime.now(UTC).isoformat().replace("+00:00", "Z")
    review: dict[str, object] = {
        "schema_version": 1,
        "review_id": str(uuid.uuid4()),
        "created_utc": created,
        "status": "engineering_review_required",
        "source_sha256": _sha256(source),
        "preview_sha256": preview["preview_sha256"],
        "query_request_id": query.request_id,
        "fixture_element_id": fixture["element_id"],
        "target_system_id": circuit["system_id"],
        "panel_name": circuit["panel_name"],
        "circuit_number": circuit["circuit_number"],
        "observed": {
            "voltage_v": voltage,
            "rating_a": rating,
            "frame_a": circuit.get("frame_a"),
            "apparent_load_va": after_va,
            "true_load_w": after_w,
            "apparent_current_a": apparent_current,
            "true_current_a": true_current,
            "voltage_drop_percent": circuit.get("voltage_drop_percent"),
            "phase_label": phase_label,
            "wire_size": circuit.get("wire_size"),
            "length_mm": circuit.get("length_mm"),
            "runs": circuit.get("runs"),
            "ways": circuit.get("ways"),
            "circuit_notes": circuit.get("circuit_notes"),
            "wire_type": wire_type,
            "protection_parameters": circuit_protection,
            "hot_conductors": circuit.get("hot_conductors"),
            "neutral_conductors": circuit.get("neutral_conductors"),
            "ground_conductors": circuit.get("ground_conductors"),
            "panel": panel,
        },
        "calculated": {
            "added_apparent_load_va": _round(after_va - before_va),
            "added_true_load_w": _round(after_w - before_w),
            "added_current_a_at_observed_voltage": _round(delta_current),
            "branch_apparent_current_utilization_percent": _percent(apparent_current, rating),
            "branch_true_current_utilization_percent": _percent(true_current, rating),
            "branch_rating_headroom_a": _round(rating - apparent_current),
            "panel_connected_current_utilization_percent": _optional_percent(connected, mcb),
            "panel_demand_current_utilization_percent": _optional_percent(demand, mcb),
            "panel_mcb_headroom_from_demand_a": (
                None if mcb is None or demand is None else _round(mcb - demand)
            ),
            "panel_phase_balance_after": phase_review,
            "panel_phase_balance_before_estimated": estimated_before_review,
            "phase_imbalance_change_percentage_points": _imbalance_delta(
                phase_review, estimated_before_review
            ),
            "upstream_feeders": incoming_reviews,
        },
        "determinations": {
            "model_membership_verified": True,
            "positive_branch_rating_margin_observed": rating > apparent_current,
            "positive_panel_mcb_margin_observed": (
                None if mcb is None or demand is None else mcb > demand
            ),
            "voltage_drop_value_present": circuit.get("voltage_drop_percent") is not None,
            "circuit_length_present": circuit.get("length_mm") is not None,
            "wire_type_evidence_complete": wire_type_complete,
            "panel_short_circuit_rating_present": bool(short_circuit_rating),
            "circuit_protection_parameter_count": len(circuit_protection),
            "panel_protection_parameter_count": len(panel_protection),
            "upstream_feeder_count": len(incoming_reviews),
            "all_upstream_feeders_have_positive_rating_margin": (
                None if not incoming_reviews else all(
                    item["positive_rating_margin_observed"] is True
                    for item in incoming_reviews
                )
            ),
            "code_compliance_evaluated": False,
            "protection_coordination_evaluated": False,
            "cable_ampacity_evaluated": False,
        },
        "unresolved_reviews": unresolved,
        "provider_egress_performed": False,
        "document_mutation_performed": False,
    }
    review["review_sha256"] = _payload_hash(review)
    output.write_text(json.dumps(review, ensure_ascii=False, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return review


def _incoming_system_reviews(value: object) -> list[dict[str, object]]:
    if not isinstance(value, list):
        return []
    reviews: list[dict[str, object]] = []
    for incoming in value:
        if not isinstance(incoming, dict):
            continue
        rating = _positive(incoming.get("rating_a"), "incoming.rating_a")
        current = _nonnegative(
            incoming.get("apparent_current_a"), "incoming.apparent_current_a"
        )
        reviews.append({
            "system_id": incoming.get("system_id"),
            "panel_name": incoming.get("panel_name"),
            "circuit_number": incoming.get("circuit_number"),
            "upstream_panel": incoming.get("upstream_panel"),
            "voltage_v": incoming.get("voltage_v"),
            "rating_a": rating,
            "apparent_current_a": current,
            "rating_utilization_percent": _percent(current, rating),
            "rating_headroom_a": _round(rating - current),
            "positive_rating_margin_observed": rating > current,
            "voltage_drop_percent": incoming.get("voltage_drop_percent"),
            "length_mm": incoming.get("length_mm"),
            "wire_size": incoming.get("wire_size"),
        })
    return reviews


def _phase_review(values: dict[str, float]) -> dict[str, object] | None:
    if len(values) != 3:
        return None
    average = sum(values.values()) / 3.0
    maximum_phase = max(values, key=values.get)  # type: ignore[arg-type]
    minimum_phase = min(values, key=values.get)  # type: ignore[arg-type]
    maximum_deviation = max(abs(value - average) for value in values.values())
    return {
        "current_phase_a": _round(values["A"]),
        "current_phase_b": _round(values["B"]),
        "current_phase_c": _round(values["C"]),
        "average_current_a": _round(average),
        "maximum_phase": maximum_phase,
        "minimum_phase": minimum_phase,
        "maximum_deviation_percent_of_average": _percent(maximum_deviation, average),
        "phase_spread_a": _round(max(values.values()) - min(values.values())),
    }


def _imbalance_delta(after: dict[str, object] | None, before: dict[str, object] | None) -> float | None:
    if after is None or before is None:
        return None
    return _round(
        float(after["maximum_deviation_percent_of_average"])
        - float(before["maximum_deviation_percent_of_average"])
    )


def _positive(value: object, name: str) -> float:
    result = _nonnegative(value, name)
    if result <= 0:
        raise RevitIntegrationError(f"Revit circuit review value {name} must be positive.")
    return result


def _nonnegative(value: object, name: str) -> float:
    if not isinstance(value, (int, float)) or isinstance(value, bool) or not math.isfinite(float(value)) or value < 0:
        raise RevitIntegrationError(f"Revit circuit review value {name} is invalid.")
    return float(value)


def _optional_nonnegative(value: object, name: str) -> float | None:
    return None if value is None else _nonnegative(value, name)


def _optional_positive(value: object, name: str) -> float | None:
    return None if value is None else _positive(value, name)


def _round(value: float) -> float:
    return round(value, 6)


def _percent(numerator: float, denominator: float) -> float:
    return _round(100.0 * numerator / denominator)


def _optional_percent(numerator: float | None, denominator: float | None) -> float | None:
    return None if numerator is None or denominator is None else _percent(numerator, denominator)


def _payload_hash(value: dict[str, object]) -> str:
    payload = {key: item for key, item in value.items() if key != "review_sha256"}
    encoded = json.dumps(payload, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest().upper()


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(65_536), b""):
            digest.update(block)
    return digest.hexdigest().upper()
