from __future__ import annotations

import hashlib
import json
import uuid
from datetime import UTC, datetime, timedelta
from pathlib import Path

import pytest

from agent.exceptions import RevitIntegrationError
from integrations.revit_circuit_assignment_handoff import (
    submit_approved_circuit_assignment_save_as,
    submit_circuit_assignment_verification,
    wait_for_circuit_assignment_handoff,
    wait_for_circuit_assignment_verification,
)
from integrations.revit_circuit_assignment_preview import _payload_hash


def _fixture(tmp_path: Path) -> tuple[Path, Path, Path, dict[str, object], Path, Path]:
    root = tmp_path / "project"
    model_dir = root / "workspace" / "revit-test"
    model_dir.mkdir(parents=True)
    source = model_dir / "source.rvt"
    source.write_bytes(b"circuit-source-rvt")
    source_hash = hashlib.sha256(source.read_bytes()).hexdigest().upper()
    connector = {
        "connector_id": 1,
        "domain": "DomainElectrical",
        "connector_type": "End",
        "electrical_system_type": "PowerCircuit",
        "mep_system_id": -1,
    }
    preview: dict[str, object] = {
        "schema_version": 1,
        "preview_id": str(uuid.uuid4()),
        "created_utc": datetime.now(UTC).isoformat().replace("+00:00", "Z"),
        "expires_utc": (datetime.now(UTC) + timedelta(hours=24)).isoformat().replace("+00:00", "Z"),
        "action": "add_existing_fixture_to_existing_electrical_system",
        "document_title": "Source",
        "source_sha256": source_hash,
        "fixture_element_id": 1735503,
        "reference_element_id": 1622255,
        "target_system_id": 1622590,
        "target_system": {
            "system_id": 1622590,
            "system_type": "PowerCircuit",
            "circuit_number": "5",
            "panel_name": "P109",
            "load_name": "Lighting",
            "voltage_v": 120,
            "apparent_load_va": 80,
            "true_load_w": 76,
            "power_factor": 0.95,
            "poles": 1,
        },
        "fixture": {
            "family_name": "Recessed Lamp - Round - LED",
            "type_name": '4" Trimmed Downlight',
            "mark": "712",
            "connectors": [connector],
        },
        "reference": {
            "family_name": "Recessed Lamp - Round - LED",
            "type_name": '4" Trimmed Downlight',
            "mark": "647",
            "connectors": [connector],
        },
        "checks": {
            "fixture_unassigned": True,
            "same_family_and_type": True,
            "connector_signature_match": True,
            "power_circuit_connector_present": True,
            "target_membership_confirmed_on_reference": True,
        },
        "unresolved_reviews": ["Capacity remains an engineer review."],
        "status": "review_required",
        "execution_allowed": False,
        "provider_egress_performed": False,
        "document_mutation_performed": False,
    }
    preview["preview_sha256"] = _payload_hash(preview)
    preview_path = model_dir / "circuit-preview.json"
    preview_path.write_text(json.dumps(preview), encoding="utf-8")
    return root, source, preview_path, preview, model_dir / "circuit-output.rvt", tmp_path / "bridge"


def test_circuit_request_is_exact_new_file_and_backup_bound(tmp_path: Path) -> None:
    root, source, preview_path, preview, output, bridge = _fixture(tmp_path)
    request_id, response, backup = submit_approved_circuit_assignment_save_as(
        workspace_root=root,
        preview_path=preview_path,
        source_model=source,
        output_model=output,
        confirmed_preview_sha256=str(preview["preview_sha256"]),
        bridge_root=bridge,
    )
    request = json.loads((bridge / "requests" / "pending.json").read_text(encoding="utf-8"))
    assert request["request_id"] == request_id
    assert request["action"] == "execute_circuit_assignment_save_as"
    assert request["document_mutation_permitted"] is True
    assert request["save_allowed"] is True
    assert response == bridge / "responses" / f"{request_id}.json"
    assert backup.read_bytes() == source.read_bytes()


def test_circuit_request_rejects_wrong_hash_tamper_and_overwrite(tmp_path: Path) -> None:
    root, source, preview_path, preview, output, bridge = _fixture(tmp_path)
    with pytest.raises(RevitIntegrationError, match="SHA-256"):
        submit_approved_circuit_assignment_save_as(
            workspace_root=root,
            preview_path=preview_path,
            source_model=source,
            output_model=output,
            confirmed_preview_sha256="0" * 64,
            bridge_root=bridge,
        )
    altered = json.loads(preview_path.read_text(encoding="utf-8"))
    altered["target_system_id"] = 99
    preview_path.write_text(json.dumps(altered), encoding="utf-8")
    with pytest.raises(RevitIntegrationError, match="contract"):
        submit_approved_circuit_assignment_save_as(
            workspace_root=root,
            preview_path=preview_path,
            source_model=source,
            output_model=output,
            confirmed_preview_sha256=str(preview["preview_sha256"]),
            bridge_root=tmp_path / "bridge-tamper",
        )
    preview_path.write_text(json.dumps(preview), encoding="utf-8")
    output.write_bytes(b"existing")
    with pytest.raises(RevitIntegrationError, match="overwrite"):
        submit_approved_circuit_assignment_save_as(
            workspace_root=root,
            preview_path=preview_path,
            source_model=source,
            output_model=output,
            confirmed_preview_sha256=str(preview["preview_sha256"]),
            bridge_root=tmp_path / "bridge-overwrite",
        )


def test_circuit_reports_and_reopened_verification_are_strict(tmp_path: Path) -> None:
    root, source, preview_path, preview, output, bridge = _fixture(tmp_path)
    request_id, response_path, _ = submit_approved_circuit_assignment_save_as(
        workspace_root=root,
        preview_path=preview_path,
        source_model=source,
        output_model=output,
        confirmed_preview_sha256=str(preview["preview_sha256"]),
        bridge_root=bridge,
    )
    (bridge / "requests" / "pending.json").unlink()
    output.write_bytes(b"saved-circuit")
    output_hash = hashlib.sha256(output.read_bytes()).hexdigest().upper()
    handoff = {
        "schema_version": 1,
        "request_id": request_id,
        "preview_id": preview["preview_id"],
        "preview_sha256": preview["preview_sha256"],
        "source_sha256": preview["source_sha256"],
        "output_path": str(output.resolve()),
        "output_sha256": output_hash,
        "fixture_element_id": 1735503,
        "target_system_id": 1622590,
        "panel_name": "P109",
        "circuit_number": "5",
        "assignment_verified": True,
        "transaction_committed": True,
        "saved_as_new_file": True,
        "provider_egress_performed": False,
        "document_mutation_performed": True,
    }
    handoff_path = bridge / "circuit-assignment-handoffs" / f"{request_id}.json"
    handoff_path.parent.mkdir(parents=True)
    handoff_path.write_text(json.dumps(handoff), encoding="utf-8")
    response_path.parent.mkdir(parents=True)
    response_path.write_text(json.dumps({
        "schema_version": 1,
        "request_id": request_id,
        "action": "execute_circuit_assignment_save_as",
        "status": "completed",
        "output_file": f"circuit-assignment-handoffs/{request_id}.json",
        "provider_egress_performed": False,
        "document_mutation_performed": True,
    }), encoding="utf-8")
    assert wait_for_circuit_assignment_handoff(
        request_id, response_path, bridge_root=bridge, expected_output=output, timeout_seconds=1
    )["assignment_verified"] is True
    verify_id, verify_response = submit_circuit_assignment_verification(
        workspace_root=root, handoff_report=handoff_path, bridge_root=bridge
    )
    (bridge / "requests" / "pending.json").unlink()
    verification = bridge / "circuit-assignment-verifications" / f"{verify_id}.json"
    verification.parent.mkdir()
    verification.write_text(json.dumps({
        "schema_version": 1,
        "request_id": verify_id,
        "handoff_request_id": request_id,
        "output_sha256": output_hash,
        "fixture_element_id": 1735503,
        "target_system_id": 1622590,
        "assignment_matches_preview": True,
        "provider_egress_performed": False,
        "document_mutation_performed": False,
    }), encoding="utf-8")
    verify_response.parent.mkdir(parents=True, exist_ok=True)
    verify_response.write_text(json.dumps({
        "schema_version": 1,
        "request_id": verify_id,
        "action": "verify_circuit_assignment_handoff",
        "status": "completed",
        "output_file": f"circuit-assignment-verifications/{verify_id}.json",
        "provider_egress_performed": False,
        "document_mutation_performed": False,
    }), encoding="utf-8")
    assert wait_for_circuit_assignment_verification(
        verify_id,
        verify_response,
        bridge_root=bridge,
        handoff_request_id=request_id,
        timeout_seconds=1,
    )["assignment_matches_preview"] is True
