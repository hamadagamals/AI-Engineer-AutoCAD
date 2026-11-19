"""Build a source-bound intake for unresolved Revit circuit calculations."""

from __future__ import annotations

import hashlib
import json
import os
import uuid
from datetime import UTC, datetime
from pathlib import Path

from agent.exceptions import RevitIntegrationError


def create_circuit_calculation_intake(
    *,
    workspace_root: Path,
    engineering_review_path: Path,
    output_path: Path,
) -> dict[str, object]:
    """Create a fail-closed, self-hashed intake without performing calculations."""
    workspace = workspace_root.expanduser().resolve(strict=True)
    review_path = engineering_review_path.expanduser().resolve(strict=True)
    output = output_path.expanduser().resolve(strict=False)
    if (
        not review_path.is_file()
        or review_path.is_symlink()
        or not review_path.is_relative_to(workspace)
        or review_path.stat().st_size > 262_144
        or output.exists()
        or output.suffix.lower() != ".json"
        or not output.parent.resolve(strict=True).is_relative_to(workspace)
    ):
        raise RevitIntegrationError("Revit circuit calculation intake paths are invalid.")
    try:
        review = json.loads(review_path.read_text(encoding="utf-8-sig"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise RevitIntegrationError(f"Revit engineering review is invalid JSON: {exc}") from exc
    _validate_review(review)

    observed = review["observed"]
    panel = observed["panel"]
    wire_type = observed.get("wire_type") or {}
    calculated = review.get("calculated") if isinstance(review.get("calculated"), dict) else {}
    upstream_feeders = calculated.get("upstream_feeders", [])
    if not isinstance(upstream_feeders, list):
        upstream_feeders = []
    blocking_model_findings = [
        "upstream_feeder_negative_rating_margin"
        for item in upstream_feeders
        if isinstance(item, dict) and item.get("positive_rating_margin_observed") is False
    ]
    requirements = [
        _observed("system_voltage_v", "V", observed.get("voltage_v"), "Revit circuit voltage"),
        _observed("circuit_rating_a", "A", observed.get("rating_a"), "Revit circuit rating"),
        _observed("conductor_display", None, observed.get("wire_size"), "Revit wire-size display"),
        _observed("conductor_material", None, wire_type.get("material"), "Revit WireType material"),
        _observed("conductor_insulation", None, wire_type.get("insulation"), "Revit WireType insulation"),
        _missing("project_code_profile", None, "Pinned jurisdiction, standard, and project hierarchy"),
        _missing("code_edition", None, "Reviewed governing code/standard edition"),
        _missing("voltage_drop_limit_percent", "%", "Project voltage-drop acceptance criterion"),
        _missing("system_voltage_applicability_confirmed", None, "Engineer confirmation for modelled 120/208 V system"),
        _missing("installation_method", None, "Reviewed cable installation/reference method"),
        _missing("base_ampacity_a", "A", "Selected conductor base ampacity from reviewed table"),
        _missing("base_ampacity_basis_reference", None, "Table, clause, and source identity for base ampacity"),
        _missing("ambient_derating_factor", "ratio", "Reviewed ambient-temperature correction factor"),
        _missing("grouping_derating_factor", "ratio", "Reviewed grouping correction factor"),
        _missing("additional_derating_factors", "ratio[]", "Any additional reviewed installation factors"),
        _missing("fault_arrangement", None, "single_phase_loop or balanced_three_phase"),
        _missing("voltage_multiplier", "ratio", "Reviewed short-circuit voltage multiplier"),
        _missing("source_resistance_ohms", "ohm", "Reviewed source resistance at the fault basis"),
        _missing("source_reactance_ohms", "ohm", "Reviewed source reactance at the fault basis"),
        _missing("path_resistance_ohms", "ohm", "Reviewed complete path/loop resistance"),
        _missing("path_reactance_ohms", "ohm", "Reviewed complete path/loop reactance"),
        _missing("impedance_basis_reference", None, "Source for every impedance and temperature basis"),
        _missing("breaker_manufacturer", None, "Exact protective-device manufacturer"),
        _missing("breaker_model", None, "Exact protective-device model/reference"),
        _missing("breaker_curve_or_trip_settings", None, "Reviewed curve or electronic trip settings"),
        _missing("breaker_interrupting_capacity_a", "A", "Rated breaking capacity at applicable voltage"),
        _missing("breaker_standard", None, "Applicable breaker product standard"),
        _missing("breaker_evidence_reference", None, "Reviewed manufacturer datasheet identity"),
        _missing("selectivity_evidence_reference", None, "Reviewed coordination/selectivity study or table"),
        _missing("reviewed_by", None, "Responsible engineer identity"),
    ]
    missing = [item["input_id"] for item in requirements if item["status"] == "missing"]
    payload: dict[str, object] = {
        "schema_version": 1,
        "intake_id": str(uuid.uuid4()),
        "created_utc": datetime.now(UTC).isoformat().replace("+00:00", "Z"),
        "status": "blocked_missing_reviewed_inputs",
        "engineering_review_sha256": review["review_sha256"],
        "source_rvt_sha256": review["source_sha256"],
        "preview_sha256": review["preview_sha256"],
        "query_request_id": review["query_request_id"],
        "fixture_element_id": review["fixture_element_id"],
        "target_system_id": review["target_system_id"],
        "panel_name": review["panel_name"],
        "panel_element_id": panel["element_id"],
        "circuit_number": review["circuit_number"],
        "requirements": requirements,
        "upstream_feeder_observations": upstream_feeders,
        "blocking_model_findings": blocking_model_findings,
        "missing_input_ids": missing,
        "missing_input_count": len(missing),
        "ready_for_ampacity_calculation": False,
        "ready_for_short_circuit_calculation": False,
        "ready_for_protection_coordination": False,
        "code_compliance_evaluation_performed": False,
        "calculation_performed": False,
        "document_mutation_performed": False,
    }
    payload["intake_sha256"] = _payload_hash(payload, "intake_sha256")
    _atomic_write(output, payload)
    return payload


def _observed(input_id: str, unit: str | None, value: object, source: str) -> dict[str, object]:
    return {
        "input_id": input_id,
        "unit": unit,
        "value": value,
        "status": "model_observation_requires_engineer_confirmation",
        "source_reference": source,
    }


def _missing(input_id: str, unit: str | None, description: str) -> dict[str, object]:
    return {
        "input_id": input_id,
        "unit": unit,
        "value": None,
        "status": "missing",
        "source_reference": description,
    }


def _validate_review(value: object) -> None:
    if not isinstance(value, dict) or value.get("schema_version") != 1:
        raise RevitIntegrationError("Revit engineering review contract is invalid.")
    claimed = value.get("review_sha256")
    if not isinstance(claimed, str) or claimed != _payload_hash(value, "review_sha256"):
        raise RevitIntegrationError("Revit engineering review SHA-256 is invalid.")
    required = (
        "source_sha256", "preview_sha256", "query_request_id", "fixture_element_id",
        "target_system_id", "panel_name", "circuit_number", "observed",
    )
    if any(name not in value for name in required):
        raise RevitIntegrationError("Revit engineering review is incomplete.")
    observed = value["observed"]
    if not isinstance(observed, dict) or not isinstance(observed.get("panel"), dict):
        raise RevitIntegrationError("Revit engineering review panel evidence is invalid.")
    if value.get("document_mutation_performed") is not False:
        raise RevitIntegrationError("Revit engineering review reports document mutation.")


def _payload_hash(value: dict[str, object], hash_field: str) -> str:
    payload = {key: item for key, item in value.items() if key != hash_field}
    encoded = json.dumps(payload, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest().upper()


def _atomic_write(path: Path, value: dict[str, object]) -> None:
    encoded = json.dumps(value, ensure_ascii=False, indent=2, sort_keys=True).encode("utf-8") + b"\n"
    temporary = path.with_name(f".{path.name}.{uuid.uuid4().hex}.tmp")
    try:
        with temporary.open("xb") as stream:
            stream.write(encoded)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        if temporary.exists():
            temporary.unlink()
