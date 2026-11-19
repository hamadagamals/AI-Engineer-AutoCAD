from __future__ import annotations

import hashlib
import json
from pathlib import Path

import pytest

from agent.exceptions import RevitIntegrationError
from integrations.revit_family_placement_handoff import (
    submit_approved_family_placement_save_as,
    submit_family_handoff_verification,
    wait_for_family_handoff,
    wait_for_family_handoff_verification,
)
from integrations.revit_preview import create_placement_preview


def _fixture(
    tmp_path: Path,
    *,
    points: list[tuple[float, float, float]] | None = None,
) -> tuple[Path, Path, Path, dict[str, object], Path]:
    root = tmp_path / "project"
    model_dir = root / "workspace" / "revit-test"
    model_dir.mkdir(parents=True)
    source = model_dir / "source.rvt"
    source.write_bytes(b"source-rvt")
    digest = hashlib.sha256(source.read_bytes()).hexdigest().upper()
    preview_path = model_dir / "preview.json"
    preview = create_placement_preview(
        workspace_root=root, source_model=source, expected_source_sha256=digest,
        document_title="Source", family_name="Downlight", type_name="120V",
        symbol_id=17, family_placement_type="OneLevelBased", host_requirement="level",
        level_name="L2", level_id=9, reference_element_id=27,
        elevation_from_level_mm=0,
        points_mm=points or [(1000, 2000, 3000)],
        output_path=preview_path,
    )
    return root, source, preview_path, preview, model_dir / "handoff.rvt"


def test_family_request_is_bound_to_one_new_output_and_backup(tmp_path: Path) -> None:
    root, source, preview_path, preview, output = _fixture(tmp_path)
    bridge = tmp_path / "bridge"
    request_id, response, backup = submit_approved_family_placement_save_as(
        workspace_root=root, preview_path=preview_path, source_model=source, output_model=output,
        confirmed_preview_sha256=str(preview["preview_sha256"]), bridge_root=bridge,
    )
    request = json.loads((bridge / "requests" / "pending.json").read_text(encoding="utf-8"))
    assert request["request_id"] == request_id
    assert request["action"] == "execute_family_placement_save_as"
    assert request["save_allowed"] is True
    assert response == bridge / "responses" / f"{request_id}.json"
    assert backup.read_bytes() == source.read_bytes()


def test_family_request_rejects_unsafe_preview_or_output(tmp_path: Path) -> None:
    root, source, preview_path, preview, output = _fixture(tmp_path)
    output.write_bytes(b"existing")
    with pytest.raises(RevitIntegrationError, match="overwrite"):
        submit_approved_family_placement_save_as(
            workspace_root=root, preview_path=preview_path, source_model=source, output_model=output,
            confirmed_preview_sha256=str(preview["preview_sha256"]), bridge_root=tmp_path / "bridge-a",
        )
    altered = json.loads(preview_path.read_text(encoding="utf-8"))
    altered["family_placement_type"] = "WorkPlaneBased"
    altered["host_requirement"] = "work_plane_or_face"
    preview_path.write_text(json.dumps(altered), encoding="utf-8")
    with pytest.raises(RevitIntegrationError, match="hash"):
        submit_approved_family_placement_save_as(
            workspace_root=root, preview_path=preview_path, source_model=source,
            output_model=root / "workspace" / "revit-test" / "other.rvt",
            confirmed_preview_sha256=str(preview["preview_sha256"]), bridge_root=tmp_path / "bridge-b",
        )


def test_family_request_accepts_one_bounded_same_type_batch(tmp_path: Path) -> None:
    root, source, preview_path, preview, output = _fixture(
        tmp_path,
        points=[(1000, 2000, 3000), (2500, 2000, 3000), (4000, 2000, 3000)],
    )
    bridge = tmp_path / "bridge"
    request_id, response, backup = submit_approved_family_placement_save_as(
        workspace_root=root,
        preview_path=preview_path,
        source_model=source,
        output_model=output,
        confirmed_preview_sha256=str(preview["preview_sha256"]),
        bridge_root=bridge,
    )
    assert request_id
    assert response == bridge / "responses" / f"{request_id}.json"
    assert backup.read_bytes() == source.read_bytes()
    approved = json.loads(
        (bridge / "approved-previews" / f"{preview['preview_id']}.json").read_text(encoding="utf-8")
    )
    assert len(approved["placements"]) == 3


def test_family_request_checks_bridge_output_alias_directory(tmp_path: Path) -> None:
    root, source, preview_path, preview, output = _fixture(tmp_path)
    other = tmp_path / "other"
    other.mkdir()
    with pytest.raises(RevitIntegrationError, match="alias"):
        submit_approved_family_placement_save_as(
            workspace_root=root, preview_path=preview_path, source_model=source,
            output_model=output, confirmed_preview_sha256=str(preview["preview_sha256"]),
            bridge_root=tmp_path / "bad-bridge", bridge_output_path=other / output.name,
        )
    bridge = tmp_path / "good-bridge"
    submit_approved_family_placement_save_as(
        workspace_root=root, preview_path=preview_path, source_model=source,
        output_model=output, confirmed_preview_sha256=str(preview["preview_sha256"]),
        bridge_root=bridge, bridge_output_path=output,
    )
    request = json.loads((bridge / "requests" / "pending.json").read_text(encoding="utf-8"))
    assert Path(request["output_path"]).name == output.name


def test_family_reports_and_reopened_verification_are_strict(tmp_path: Path) -> None:
    root, source, preview_path, preview, output = _fixture(
        tmp_path,
        points=[(1000, 2000, 3000), (2500, 2000, 3000), (4000, 2000, 3000)],
    )
    bridge = tmp_path / "bridge"
    request_id, response_path, _ = submit_approved_family_placement_save_as(
        workspace_root=root, preview_path=preview_path, source_model=source, output_model=output,
        confirmed_preview_sha256=str(preview["preview_sha256"]), bridge_root=bridge,
    )
    (bridge / "requests" / "pending.json").unlink()
    output.write_bytes(b"saved")
    output_hash = hashlib.sha256(output.read_bytes()).hexdigest().upper()
    handoff = {
        "schema_version": 1, "request_id": request_id, "preview_id": preview["preview_id"],
        "preview_sha256": preview["preview_sha256"], "source_sha256": preview["source_sha256"],
        "output_path": str(output.resolve()), "output_sha256": output_hash,
        "created_family_instances": 3, "created_element_ids": [77, 78, 79], "transaction_committed": True,
        "saved_as_new_file": True, "provider_egress_performed": False,
        "document_mutation_performed": True,
    }
    handoff_path = bridge / "family-handoffs" / f"{request_id}.json"
    handoff_path.parent.mkdir(parents=True)
    handoff_path.write_text(json.dumps(handoff), encoding="utf-8")
    response_path.parent.mkdir(parents=True)
    response_path.write_text(json.dumps({
        "schema_version": 1, "request_id": request_id, "action": "execute_family_placement_save_as",
        "status": "completed", "output_file": f"family-handoffs/{request_id}.json",
        "provider_egress_performed": False, "document_mutation_performed": True,
    }), encoding="utf-8")
    assert wait_for_family_handoff(request_id, response_path, bridge_root=bridge, expected_output=output, timeout_seconds=1)["output_sha256"] == output_hash
    verify_id, verify_response = submit_family_handoff_verification(
        workspace_root=root, handoff_report=handoff_path, bridge_root=bridge,
    )
    assert json.loads((bridge / "requests" / "pending.json").read_text(encoding="utf-8"))["action"] == "verify_family_placement_handoff"
    (bridge / "requests" / "pending.json").unlink()
    verification = bridge / "family-verifications" / f"{verify_id}.json"
    verification.parent.mkdir()
    verification.write_text(json.dumps({
        "schema_version": 1, "request_id": verify_id, "handoff_request_id": request_id,
        "output_sha256": output_hash, "verified_family_instances": 3,
        "verified_element_ids": [77, 78, 79],
        "placement_matches_preview": True, "provider_egress_performed": False,
        "document_mutation_performed": False,
    }), encoding="utf-8")
    verify_response.parent.mkdir(parents=True, exist_ok=True)
    verify_response.write_text(json.dumps({
        "schema_version": 1, "request_id": verify_id, "action": "verify_family_placement_handoff",
        "status": "completed", "output_file": f"family-verifications/{verify_id}.json",
        "provider_egress_performed": False, "document_mutation_performed": False,
    }), encoding="utf-8")
    assert wait_for_family_handoff_verification(
        verify_id, verify_response, bridge_root=bridge, handoff_request_id=request_id, timeout_seconds=1,
    )["placement_matches_preview"] is True
