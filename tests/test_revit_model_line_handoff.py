from __future__ import annotations

import hashlib
import json
from pathlib import Path

import pytest

from agent.exceptions import RevitIntegrationError
from integrations.revit_model_line_handoff import (
    submit_approved_model_lines_save_as,
    submit_handoff_verification,
    wait_for_handoff,
    wait_for_handoff_verification,
)
from integrations.revit_model_line_preview import create_model_line_preview


def _fixture(tmp_path: Path) -> tuple[Path, Path, Path, dict[str, object], Path]:
    root = tmp_path / "project"
    model_root = root / "workspace" / "revit-test"
    model_root.mkdir(parents=True)
    source = model_root / "source.rvt"
    source.write_bytes(b"source-rvt")
    digest = hashlib.sha256(source.read_bytes()).hexdigest().upper()
    preview_path = model_root / "preview.json"
    preview = create_model_line_preview(
        workspace_root=root,
        source_model=source,
        expected_source_sha256=digest,
        document_title="Source",
        segments_mm=[
            ((0, 0, 0), (1000, 0, 0)),
            ((1000, 0, 0), (1000, 1000, 0)),
            ((1000, 1000, 0), (0, 1000, 0)),
            ((0, 1000, 0), (0, 0, 0)),
        ],
        output_path=preview_path,
    )
    return root, source, preview_path, preview, model_root / "handoff.rvt"


def test_save_as_request_is_new_workspace_file_and_backup_bound(tmp_path: Path) -> None:
    root, source, preview_path, preview, output = _fixture(tmp_path)
    bridge = tmp_path / "bridge"
    request_id, response_path, backup = submit_approved_model_lines_save_as(
        workspace_root=root,
        preview_path=preview_path,
        source_model=source,
        output_model=output,
        confirmed_preview_sha256=str(preview["preview_sha256"]),
        bridge_root=bridge,
    )
    request = json.loads(
        (bridge / "requests" / "pending.json").read_text(encoding="utf-8")
    )
    assert request["request_id"] == request_id
    assert request["action"] == "execute_model_lines_save_as"
    assert request["save_allowed"] is True
    assert Path(request["output_path"]).resolve() == output.resolve()
    assert response_path == bridge / "responses" / f"{request_id}.json"
    assert backup.read_bytes() == source.read_bytes()


def test_save_as_request_refuses_existing_or_external_output(tmp_path: Path) -> None:
    root, source, preview_path, preview, output = _fixture(tmp_path)
    output.write_bytes(b"existing")
    with pytest.raises(RevitIntegrationError, match="overwrite"):
        submit_approved_model_lines_save_as(
            workspace_root=root, preview_path=preview_path, source_model=source,
            output_model=output, confirmed_preview_sha256=str(preview["preview_sha256"]),
            bridge_root=tmp_path / "bridge-a",
        )
    with pytest.raises(RevitIntegrationError, match="inside workspace"):
        submit_approved_model_lines_save_as(
            workspace_root=root, preview_path=preview_path, source_model=source,
            output_model=root / "outside.rvt",
            confirmed_preview_sha256=str(preview["preview_sha256"]),
            bridge_root=tmp_path / "bridge-b",
        )


def test_handoff_and_reopened_verification_reports_are_strict(tmp_path: Path) -> None:
    root, source, preview_path, preview, output = _fixture(tmp_path)
    bridge = tmp_path / "bridge"
    request_id, response_path, _ = submit_approved_model_lines_save_as(
        workspace_root=root, preview_path=preview_path, source_model=source,
        output_model=output, confirmed_preview_sha256=str(preview["preview_sha256"]),
        bridge_root=bridge,
    )
    (bridge / "requests" / "pending.json").unlink()
    output.write_bytes(b"saved-output")
    output_hash = hashlib.sha256(output.read_bytes()).hexdigest().upper()
    handoffs = bridge / "handoffs"
    handoffs.mkdir()
    handoff_path = handoffs / f"{request_id}.json"
    handoff = {
        "schema_version": 1, "request_id": request_id,
        "preview_id": preview["preview_id"], "preview_sha256": preview["preview_sha256"],
        "source_sha256": preview["source_sha256"], "output_path": str(output.resolve()),
        "output_sha256": output_hash, "created_model_lines": 4,
        "created_element_ids": [11, 12, 13, 14], "transaction_committed": True,
        "saved_as_new_file": True, "provider_egress_performed": False,
        "document_mutation_performed": True,
    }
    handoff_path.write_text(json.dumps(handoff), encoding="utf-8")
    response_path.parent.mkdir(parents=True)
    response_path.write_text(json.dumps({
        "schema_version": 1, "request_id": request_id,
        "action": "execute_model_lines_save_as", "status": "completed",
        "document_title": output.stem, "output_file": f"handoffs/{request_id}.json",
        "provider_egress_performed": False, "document_mutation_performed": True,
        "error": "",
    }), encoding="utf-8")
    assert wait_for_handoff(
        request_id, response_path, bridge_root=bridge, expected_output=output,
        timeout_seconds=1,
    )["output_sha256"] == output_hash

    verify_id, verify_response = submit_handoff_verification(
        workspace_root=root, handoff_report=handoff_path, bridge_root=bridge,
    )
    verify_request = json.loads(
        (bridge / "requests" / "pending.json").read_text(encoding="utf-8")
    )
    assert verify_request["document_mutation_permitted"] is False
    assert verify_request["handoff_request_id"] == request_id
    (bridge / "requests" / "pending.json").unlink()
    verifications = bridge / "verifications"
    verifications.mkdir()
    verification_path = verifications / f"{verify_id}.json"
    verification_path.write_text(json.dumps({
        "schema_version": 1, "request_id": verify_id,
        "handoff_request_id": request_id, "output_sha256": output_hash,
        "verified_model_lines": 4, "verified_element_ids": [11, 12, 13, 14],
        "geometry_matches_preview": True, "provider_egress_performed": False,
        "document_mutation_performed": False,
    }), encoding="utf-8")
    verify_response.parent.mkdir(parents=True, exist_ok=True)
    verify_response.write_text(json.dumps({
        "schema_version": 1, "request_id": verify_id,
        "action": "verify_model_lines_handoff", "status": "completed",
        "document_title": output.stem,
        "output_file": f"verifications/{verify_id}.json",
        "provider_egress_performed": False, "document_mutation_performed": False,
        "error": "",
    }), encoding="utf-8")
    result = wait_for_handoff_verification(
        verify_id, verify_response, bridge_root=bridge,
        handoff_request_id=request_id, timeout_seconds=1,
    )
    assert result["geometry_matches_preview"] is True
