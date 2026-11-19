from __future__ import annotations

import hashlib
import json
from pathlib import Path

import pytest

from agent.exceptions import RevitIntegrationError
from integrations.revit_hosted_family_placement_handoff import (
    submit_approved_hosted_family_placement_save_as,
    submit_hosted_family_handoff_verification,
    wait_for_hosted_family_handoff,
    wait_for_hosted_family_handoff_verification,
)
from integrations.revit_preview import create_placement_preview


def _fixture(tmp_path: Path) -> tuple[Path, Path, Path, dict[str, object], Path, Path]:
    root = tmp_path / "project"
    model_dir = root / "workspace" / "revit-test"
    model_dir.mkdir(parents=True)
    source = model_dir / "source.rvt"
    source.write_bytes(b"hosted-source-rvt")
    digest = hashlib.sha256(source.read_bytes()).hexdigest().upper()
    preview_path = model_dir / "hosted-preview.json"
    host = {
        "kind": "linked_face",
        "link_instance_id": 30,
        "linked_element_id": 40,
        "linked_document_title": "Architectural",
        "linked_category_key": "ceilings",
        "face_stable_reference": "stable-linked-face",
    }
    preview = create_placement_preview(
        workspace_root=root,
        source_model=source,
        expected_source_sha256=digest,
        document_title="Source",
        family_name="Downlight",
        type_name="4 inch",
        symbol_id=17,
        family_placement_type="WorkPlaneBased",
        host_requirement="work_plane_or_face",
        level_name="",
        level_id=-1,
        reference_element_id=27,
        elevation_from_level_mm=3000,
        points_mm=[(1000, 2000, 3000)],
        rotations_degrees=[0.0],
        host_faces=[host],
        output_path=preview_path,
    )
    return root, source, preview_path, preview, model_dir / "hosted-output.rvt", tmp_path / "bridge"


def test_hosted_request_is_exact_new_file_and_backup_bound(tmp_path: Path) -> None:
    root, source, preview_path, preview, output, bridge = _fixture(tmp_path)
    request_id, response, backup = submit_approved_hosted_family_placement_save_as(
        workspace_root=root,
        preview_path=preview_path,
        source_model=source,
        output_model=output,
        confirmed_preview_sha256=str(preview["preview_sha256"]),
        bridge_root=bridge,
    )
    request = json.loads((bridge / "requests" / "pending.json").read_text(encoding="utf-8"))
    assert request["request_id"] == request_id
    assert request["action"] == "execute_hosted_family_placement_save_as"
    assert request["document_mutation_permitted"] is True
    assert request["save_allowed"] is True
    assert response == bridge / "responses" / f"{request_id}.json"
    assert backup.read_bytes() == source.read_bytes()


def test_hosted_request_rejects_wrong_hash_rotation_and_overwrite(tmp_path: Path) -> None:
    root, source, preview_path, preview, output, bridge = _fixture(tmp_path)
    with pytest.raises(RevitIntegrationError, match="SHA-256"):
        submit_approved_hosted_family_placement_save_as(
            workspace_root=root,
            preview_path=preview_path,
            source_model=source,
            output_model=output,
            confirmed_preview_sha256="0" * 64,
            bridge_root=bridge,
        )
    altered = json.loads(preview_path.read_text(encoding="utf-8"))
    altered["placements"][0]["rotation_degrees"] = 15.0
    altered.pop("preview_sha256")
    from integrations.revit_preview import _artifact_hash

    altered["preview_sha256"] = _artifact_hash(altered)
    preview_path.write_text(json.dumps(altered), encoding="utf-8")
    with pytest.raises(RevitIntegrationError, match="zero-rotation"):
        submit_approved_hosted_family_placement_save_as(
            workspace_root=root,
            preview_path=preview_path,
            source_model=source,
            output_model=output,
            confirmed_preview_sha256=str(altered["preview_sha256"]),
            bridge_root=tmp_path / "bridge-rotation",
        )
    output.write_bytes(b"existing")
    with pytest.raises(RevitIntegrationError, match="overwrite"):
        submit_approved_hosted_family_placement_save_as(
            workspace_root=root,
            preview_path=preview_path,
            source_model=source,
            output_model=output,
            confirmed_preview_sha256=str(altered["preview_sha256"]),
            bridge_root=tmp_path / "bridge-overwrite",
        )


def test_hosted_reports_and_reopened_verification_are_strict(tmp_path: Path) -> None:
    root, source, preview_path, preview, output, bridge = _fixture(tmp_path)
    request_id, response_path, _ = submit_approved_hosted_family_placement_save_as(
        workspace_root=root,
        preview_path=preview_path,
        source_model=source,
        output_model=output,
        confirmed_preview_sha256=str(preview["preview_sha256"]),
        bridge_root=bridge,
    )
    (bridge / "requests" / "pending.json").unlink()
    output.write_bytes(b"saved-hosted")
    output_hash = hashlib.sha256(output.read_bytes()).hexdigest().upper()
    handoff = {
        "schema_version": 1,
        "request_id": request_id,
        "preview_id": preview["preview_id"],
        "preview_sha256": preview["preview_sha256"],
        "source_sha256": preview["source_sha256"],
        "output_path": str(output.resolve()),
        "output_sha256": output_hash,
        "created_family_instances": 1,
        "created_element_ids": [77],
        "transaction_committed": True,
        "saved_as_new_file": True,
        "provider_egress_performed": False,
        "document_mutation_performed": True,
    }
    handoff_path = bridge / "hosted-family-handoffs" / f"{request_id}.json"
    handoff_path.parent.mkdir(parents=True)
    handoff_path.write_text(json.dumps(handoff), encoding="utf-8")
    response_path.parent.mkdir(parents=True)
    response_path.write_text(json.dumps({
        "schema_version": 1,
        "request_id": request_id,
        "action": "execute_hosted_family_placement_save_as",
        "status": "completed",
        "output_file": f"hosted-family-handoffs/{request_id}.json",
        "provider_egress_performed": False,
        "document_mutation_performed": True,
    }), encoding="utf-8")
    assert wait_for_hosted_family_handoff(
        request_id, response_path, bridge_root=bridge, expected_output=output, timeout_seconds=1
    )["output_sha256"] == output_hash
    verify_id, verify_response = submit_hosted_family_handoff_verification(
        workspace_root=root, handoff_report=handoff_path, bridge_root=bridge
    )
    (bridge / "requests" / "pending.json").unlink()
    verification = bridge / "hosted-family-verifications" / f"{verify_id}.json"
    verification.parent.mkdir()
    verification.write_text(json.dumps({
        "schema_version": 1,
        "request_id": verify_id,
        "handoff_request_id": request_id,
        "output_sha256": output_hash,
        "verified_family_instances": 1,
        "verified_element_ids": [77],
        "placement_matches_preview": True,
        "provider_egress_performed": False,
        "document_mutation_performed": False,
    }), encoding="utf-8")
    verify_response.parent.mkdir(parents=True, exist_ok=True)
    verify_response.write_text(json.dumps({
        "schema_version": 1,
        "request_id": verify_id,
        "action": "verify_hosted_family_placement_handoff",
        "status": "completed",
        "output_file": f"hosted-family-verifications/{verify_id}.json",
        "provider_egress_performed": False,
        "document_mutation_performed": False,
    }), encoding="utf-8")
    assert wait_for_hosted_family_handoff_verification(
        verify_id,
        verify_response,
        bridge_root=bridge,
        handoff_request_id=request_id,
        timeout_seconds=1,
    )["placement_matches_preview"] is True
