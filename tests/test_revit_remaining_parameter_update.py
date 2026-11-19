from __future__ import annotations

import hashlib
import json
from pathlib import Path

import pytest

from agent.exceptions import RevitIntegrationError
import integrations.revit_remaining_parameter_update as update


def _hash(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def _fixture(root: Path) -> tuple[Path, dict[str, object]]:
    scratch = root / "workspace/revit-school/scratch"
    library = root / "workspace/revit-school/official-library/extracted"
    scratch.mkdir(parents=True)
    (library / "l2").mkdir(parents=True)
    (library / "l5").mkdir(parents=True)
    source = scratch / "School-Lighting-L1-Parameter-Validation.rvt"
    source.write_bytes(b"source")
    l2 = library / "l2/L2.ies"
    l5 = library / "l5/L5.ies"
    l2.write_bytes(b"l2")
    l5.write_bytes(b"l5")
    value: dict[str, object] = {
        "schema_version": 1,
        "action": "update_l2_l5_l6_parameters_in_new_scratch_rvt",
        "status": "approval_required", "execution_allowed": False,
        "document_mutation_permitted": False, "save_allowed": False,
        "source_scratch_rvt_sha256": _hash(source), "official_refresh_sha256": "A" * 64,
        "targets": [
            {"design_id": "L2", "family_name": "L2F", "type_name": "L2T", "changes": [],
             "ies_relative_path": "l2/L2.ies", "ies_sha256": _hash(l2),
             "ies_lumens": 2500.4, "ies_input_watts": 19},
            {"design_id": "L5-L6", "family_name": "L5F", "type_name": "L5T", "changes": [],
             "ies_relative_path": "l5/L5.ies", "ies_sha256": _hash(l5),
             "ies_lumens": 4000.6, "ies_input_watts": 30},
        ],
        "held_targets": [],
        "output_relative_path": "workspace/revit-school/scratch/result.rvt",
        "verification": [],
    }
    value["preview_sha256"] = update._canonical_hash(value)
    preview = scratch / "preview.json"
    preview.write_text(json.dumps(value), encoding="utf-8")
    return preview, value


def test_submit_stages_two_exact_ies(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    preview, value = _fixture(tmp_path)
    monkeypatch.setattr(update, "APPROVED_PREVIEW_SHA256", value["preview_sha256"])
    request_id, response = update.submit_approved_remaining_parameter_update(
        preview_path=preview, confirmed_preview_sha256=str(value["preview_sha256"]),
        workspace_root=tmp_path, bridge_root=tmp_path / "bridge",
    )
    payload = json.loads((tmp_path / "bridge/requests/pending.json").read_text())
    assert payload["action"] == update.ACTION
    assert payload["preview_sha256"] == value["preview_sha256"]
    assert (tmp_path / "bridge/scratch-parameter-intake" / payload["ies_relative_path"]).read_bytes() == b"l2"
    assert (tmp_path / "bridge/scratch-parameter-intake" / payload["ies_relative_path_2"]).read_bytes() == b"l5"
    assert response.name == f"{request_id}.json"


def test_submit_rejects_wrong_confirmation(tmp_path: Path) -> None:
    preview, _ = _fixture(tmp_path)
    with pytest.raises(RevitIntegrationError, match="exact approval"):
        update.submit_approved_remaining_parameter_update(
            preview_path=preview, confirmed_preview_sha256="0" * 64,
            workspace_root=tmp_path, bridge_root=tmp_path / "bridge",
        )


def test_wait_rejects_failed_response(tmp_path: Path) -> None:
    request_id = "11111111-1111-1111-1111-111111111111"
    response = tmp_path / "response.json"
    response.write_text(json.dumps({
        "schema_version": 1, "request_id": request_id, "action": update.ACTION,
        "status": "failed", "document_mutation_performed": False,
        "provider_egress_performed": False, "error": "failure", "output_file": "",
    }))
    with pytest.raises(RevitIntegrationError, match="strict validation"):
        update.wait_for_remaining_parameter_response(request_id, response, timeout_seconds=1)


def test_publish_strict_reopened_result(tmp_path: Path) -> None:
    preview, value = _fixture(tmp_path)
    bridge = tmp_path / "bridge"
    output = bridge / "scratch-output/result.rvt"
    output.parent.mkdir(parents=True)
    output.write_bytes(b"result")
    request_id = "11111111-1111-1111-1111-111111111111"
    report_path = bridge / f"scratch-reports/{request_id}.json"
    report_path.parent.mkdir(parents=True)
    targets = []
    for expected, load, watts in zip(value["targets"], (19, 30), ("19W", "30W"), strict=True):
        targets.append({
            "design_id": expected["design_id"], "family_name": expected["family_name"],
            "type_name": expected["type_name"], "apparent_load_va": load,
            "wattage_comments": watts,
            "photometric_web_file": Path(expected["ies_relative_path"]).name,
            "ies_sha256": expected["ies_sha256"], "connector_count": 1,
        })
    report_path.write_text(json.dumps({
        "schema_version": 1, "request_id": request_id,
        "source_sha256_before": value["source_scratch_rvt_sha256"],
        "source_sha256_after": value["source_scratch_rvt_sha256"],
        "source_document_mutated": False, "output_rvt": str(output),
        "output_sha256": _hash(output), "targets": targets,
        "l1_preserved": True, "reopened_verified": True,
    }))
    result = update.validate_and_publish_remaining_parameter_result(
        request_id=request_id, report_relative_path=f"scratch-reports/{request_id}.json",
        preview_path=preview, workspace_root=tmp_path, bridge_root=bridge,
    )
    assert Path(result["published_rvt"]).read_bytes() == b"result"
    assert len(result["published_ies"]) == 2
    assert Path(result["published_report"]).is_file()


def test_bridge_source_contains_exact_fixed_action() -> None:
    root = Path(__file__).parents[1]
    bridge = (root / "integrations/revit_readonly_plugin/ReadOnlyRequestBridge.cs").read_text(encoding="utf-8")
    writer = (root / "integrations/revit_readonly_plugin/ScratchRemainingParameterUpdate.cs").read_text(encoding="utf-8")
    assert update.APPROVED_PREVIEW_SHA256 in bridge
    assert 'string.Equals(request.Action, "execute_l2_l5_l6_parameter_update"' in bridge
    assert "VerifyValues(l1, 31, \"31W\", L1Ies" in writer
    assert "VerifyValues(l5, 30, \"30W\", L5Ies" in writer
