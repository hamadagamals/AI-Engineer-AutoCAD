"""Create a non-executable, hash-bound Revit circuit-assignment review preview."""

from __future__ import annotations

import hashlib
import json
import uuid
from datetime import UTC, datetime, timedelta
from pathlib import Path

from agent.exceptions import RevitIntegrationError
from integrations.revit_lighting_circuit_query import load_lighting_circuit_query


def load_circuit_assignment_preview(
    path: Path,
    *,
    workspace_root: Path,
    require_unexpired: bool = True,
) -> dict[str, object]:
    """Load and strictly validate one circuit-assignment review preview."""
    workspace = workspace_root.expanduser().resolve(strict=True)
    candidate = path.expanduser().resolve(strict=True)
    if (
        not candidate.is_file()
        or candidate.is_symlink()
        or not candidate.is_relative_to(workspace)
        or candidate.suffix.lower() != ".json"
        or candidate.stat().st_size > 65_536
    ):
        raise RevitIntegrationError("Revit circuit preview path is invalid.")
    try:
        value = json.loads(candidate.read_text(encoding="utf-8-sig"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise RevitIntegrationError("Revit circuit preview JSON is invalid.") from exc
    if not isinstance(value, dict):
        raise RevitIntegrationError("Revit circuit preview must be an object.")
    preview_hash = value.get("preview_sha256")
    try:
        uuid.UUID(str(value.get("preview_id", "")))
        expires = datetime.fromisoformat(str(value.get("expires_utc", "")).replace("Z", "+00:00"))
    except ValueError as exc:
        raise RevitIntegrationError("Revit circuit preview identity or expiry is invalid.") from exc
    checks = value.get("checks")
    fixture = value.get("fixture")
    reference = value.get("reference")
    target = value.get("target_system")
    if (
        value.get("schema_version") != 1
        or value.get("action") != "add_existing_fixture_to_existing_electrical_system"
        or value.get("status") != "review_required"
        or value.get("execution_allowed") is not False
        or value.get("provider_egress_performed") is not False
        or value.get("document_mutation_performed") is not False
        or not isinstance(preview_hash, str)
        or len(preview_hash) != 64
        or preview_hash != _payload_hash(value)
        or not isinstance(value.get("source_sha256"), str)
        or len(str(value["source_sha256"])) != 64
        or not isinstance(value.get("fixture_element_id"), int)
        or isinstance(value.get("fixture_element_id"), bool)
        or int(value["fixture_element_id"]) <= 0
        or not isinstance(value.get("reference_element_id"), int)
        or isinstance(value.get("reference_element_id"), bool)
        or int(value["reference_element_id"]) <= 0
        or not isinstance(value.get("target_system_id"), int)
        or isinstance(value.get("target_system_id"), bool)
        or int(value["target_system_id"]) <= 0
        or not isinstance(checks, dict)
        or set(checks) != {
            "fixture_unassigned", "same_family_and_type", "connector_signature_match",
            "power_circuit_connector_present", "target_membership_confirmed_on_reference",
        }
        or any(item is not True for item in checks.values())
        or not isinstance(fixture, dict)
        or not isinstance(reference, dict)
        or not isinstance(target, dict)
        or target.get("system_id") != value.get("target_system_id")
        or target.get("system_type") != "PowerCircuit"
        or fixture.get("family_name") != reference.get("family_name")
        or fixture.get("type_name") != reference.get("type_name")
        or not isinstance(fixture.get("connectors"), list)
        or not isinstance(reference.get("connectors"), list)
        or fixture.get("connectors") != reference.get("connectors")
        or not isinstance(value.get("unresolved_reviews"), list)
        or not value.get("unresolved_reviews")
        or expires.tzinfo is None
        or (require_unexpired and expires.astimezone(UTC) <= datetime.now(UTC))
    ):
        raise RevitIntegrationError("Revit circuit preview contract is invalid or expired.")
    return value


def create_circuit_assignment_preview(
    *,
    workspace_root: Path,
    bridge_root: Path,
    source_model: Path,
    fixture_query_path: Path,
    reference_query_path: Path,
    target_system_id: int,
    output_path: Path,
    lifetime_hours: int = 24,
) -> dict[str, object]:
    workspace = workspace_root.expanduser().resolve(strict=True)
    source = source_model.expanduser().resolve(strict=True)
    destination = output_path.expanduser().resolve(strict=False)
    if (
        not source.is_file()
        or source.is_symlink()
        or not source.is_relative_to(workspace)
        or source.suffix.lower() != ".rvt"
        or not destination.parent.resolve(strict=True).is_relative_to(workspace)
        or destination.suffix.lower() != ".json"
        or destination.exists()
    ):
        raise RevitIntegrationError("Revit circuit preview paths are invalid.")
    if not isinstance(target_system_id, int) or isinstance(target_system_id, bool) or target_system_id <= 0:
        raise RevitIntegrationError("Revit circuit preview target system is invalid.")
    if not isinstance(lifetime_hours, int) or not 1 <= lifetime_hours <= 72:
        raise RevitIntegrationError("Revit circuit preview lifetime must be within 1..72 hours.")
    fixture_query = load_lighting_circuit_query(fixture_query_path, allowed_root=bridge_root)
    reference_query = load_lighting_circuit_query(reference_query_path, allowed_root=bridge_root)
    if fixture_query.document_title != reference_query.document_title:
        raise RevitIntegrationError("Revit circuit preview query documents do not match.")
    fixture = _exact_fixture(fixture_query.fixtures, fixture_query.requested_element_id)
    reference = _exact_fixture(reference_query.fixtures, reference_query.requested_element_id)
    target = next(
        (item for item in reference["circuits"] if item["system_id"] == target_system_id),
        None,
    )
    if target is None:
        raise RevitIntegrationError("Reference fixture is not a member of the target Revit system.")
    fixture_signature = _connector_signature(fixture)
    reference_signature = _connector_signature(reference)
    checks = {
        "fixture_unassigned": fixture["circuit_count"] == 0,
        "same_family_and_type": (
            fixture["family_name"] == reference["family_name"]
            and fixture["type_name"] == reference["type_name"]
        ),
        "connector_signature_match": fixture_signature == reference_signature,
        "power_circuit_connector_present": any(
            item[3] == "PowerCircuit" for item in fixture_signature
        ),
        "target_membership_confirmed_on_reference": True,
    }
    if not all(checks.values()):
        raise RevitIntegrationError("Revit circuit assignment compatibility checks did not pass.")
    created = datetime.now(UTC)
    preview: dict[str, object] = {
        "schema_version": 1,
        "preview_id": str(uuid.uuid4()),
        "created_utc": created.isoformat().replace("+00:00", "Z"),
        "expires_utc": (created + timedelta(hours=lifetime_hours)).isoformat().replace("+00:00", "Z"),
        "action": "add_existing_fixture_to_existing_electrical_system",
        "document_title": fixture_query.document_title,
        "source_sha256": _sha256(source),
        "fixture_element_id": fixture["element_id"],
        "reference_element_id": reference["element_id"],
        "target_system_id": target_system_id,
        "target_system": target,
        "fixture": {
            "family_name": fixture["family_name"],
            "type_name": fixture["type_name"],
            "mark": fixture["mark"],
            "connectors": fixture["connectors"],
        },
        "reference": {
            "family_name": reference["family_name"],
            "type_name": reference["type_name"],
            "mark": reference["mark"],
            "connectors": reference["connectors"],
        },
        "checks": checks,
        "unresolved_reviews": [
            "Fixture instance voltage and apparent-load parameters are unavailable in the live query.",
            "Target circuit spare capacity, demand, protection, and phase impact are not reviewed.",
            "Circuit membership still requires explicit engineer approval and reopened verification.",
        ],
        "status": "review_required",
        "execution_allowed": False,
        "provider_egress_performed": False,
        "document_mutation_performed": False,
    }
    preview["preview_sha256"] = _payload_hash(preview)
    destination.write_text(
        json.dumps(preview, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    return preview


def _exact_fixture(fixtures: tuple[dict[str, object], ...], element_id: int) -> dict[str, object]:
    if element_id <= 0 or len(fixtures) != 1 or fixtures[0]["element_id"] != element_id:
        raise RevitIntegrationError("Revit circuit preview requires one exact fixture query.")
    return fixtures[0]


def _connector_signature(fixture: dict[str, object]) -> tuple[tuple[int, str, str, str], ...]:
    connectors = fixture["connectors"]
    return tuple(sorted(
        (
            item["connector_id"], item["domain"], item["connector_type"],
            item["electrical_system_type"],
        )
        for item in connectors
    ))


def _payload_hash(value: dict[str, object]) -> str:
    payload = {key: item for key, item in value.items() if key != "preview_sha256"}
    encoded = json.dumps(payload, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest().upper()


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while block := stream.read(65_536):
            digest.update(block)
    return digest.hexdigest().upper()
