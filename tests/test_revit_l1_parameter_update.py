from __future__ import annotations

import hashlib
import json
from pathlib import Path

import pytest

from agent.exceptions import RevitIntegrationError
import integrations.revit_l1_parameter_update as update


def _hash(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def _preview(workspace: Path) -> tuple[Path, Path]:
    scratch = workspace / "workspace/revit-school/scratch"
    library = workspace / "workspace/revit-school/official-library/extracted/ies"
    scratch.mkdir(parents=True)
    library.mkdir(parents=True)
    source = scratch / "School-Lighting-Family-Validation.rvt"
    source.write_bytes(b"source")
    ies = library / "L1.ies"
    ies.write_bytes(b"ies")
    value = {
        "schema_version": 1,
        "action": "update_l1_parameters_in_new_scratch_rvt",
        "status": "approval_required",
        "execution_allowed": False,
        "document_mutation_permitted": False,
        "save_allowed": False,
        "source_scratch_rvt_sha256": _hash(source),
        "photometry_preview_sha256": "A" * 64,
        "target": {"design_id": "L1", "family_name": "FAMILY", "type_name": "TYPE"},
        "changes": [],
        "ies_relative_path": "ies/L1.ies",
        "ies_sha256": _hash(ies),
        "ies_lumens": 4310.8,
        "ies_input_watts": 31,
        "output_relative_path": "workspace/revit-school/scratch/L1-output.rvt",
        "verification": [],
    }
    value["preview_sha256"] = update._canonical_hash(value)
    preview = scratch / "preview.json"
    preview.write_text(json.dumps(value), encoding="utf-8")
    return preview, source


def test_submit_stages_exact_ies_and_fixed_request(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    preview, _ = _preview(tmp_path)
    value = json.loads(preview.read_text())
    monkeypatch.setattr(update, "APPROVED_PREVIEW_SHA256", value["preview_sha256"])
    request_id, response = update.submit_approved_l1_parameter_update(
        preview_path=preview,
        confirmed_preview_sha256=value["preview_sha256"],
        workspace_root=tmp_path,
        bridge_root=tmp_path / "bridge",
    )
    payload = json.loads((tmp_path / "bridge/requests/pending.json").read_text())
    assert payload["request_id"] == request_id
    assert payload["action"] == update.ACTION
    assert payload["document_mutation_permitted"] is True
    assert payload["approval_granted"] is True
    assert response.name == f"{request_id}.json"
    assert (tmp_path / "bridge/scratch-parameter-intake" / payload["ies_relative_path"]).read_bytes() == b"ies"


def test_submit_rejects_wrong_confirmation(tmp_path: Path) -> None:
    preview, _ = _preview(tmp_path)
    with pytest.raises(RevitIntegrationError, match="exact approval"):
        update.submit_approved_l1_parameter_update(
            preview_path=preview,
            confirmed_preview_sha256="0" * 64,
            workspace_root=tmp_path,
            bridge_root=tmp_path / "bridge",
        )


def test_wait_rejects_failed_response(tmp_path: Path) -> None:
    request_id = "11111111-1111-1111-1111-111111111111"
    response = tmp_path / "response.json"
    response.write_text(json.dumps({
        "schema_version": 1, "request_id": request_id, "action": update.ACTION,
        "status": "failed", "document_mutation_performed": False,
        "provider_egress_performed": False, "error": "failure", "output_file": "",
    }), encoding="utf-8")
    with pytest.raises(RevitIntegrationError, match="strict validation"):
        update.wait_for_l1_parameter_response(request_id, response, timeout_seconds=1)


def test_publish_validated_new_output(tmp_path: Path) -> None:
    preview, source = _preview(tmp_path)
    value = json.loads(preview.read_text())
    bridge = tmp_path / "bridge"
    output = bridge / "scratch-output/result.rvt"
    output.parent.mkdir(parents=True)
    output.write_bytes(b"result")
    request_id = "11111111-1111-1111-1111-111111111111"
    report_path = bridge / f"scratch-reports/{request_id}.json"
    report_path.parent.mkdir(parents=True)
    report_path.write_text(json.dumps({
        "schema_version": 1, "request_id": request_id,
        "source_sha256_before": _hash(source), "source_sha256_after": _hash(source),
        "source_document_mutated": False, "output_rvt": str(output), "output_sha256": _hash(output),
        "ies_sha256": value["ies_sha256"], "family_name": "FAMILY", "type_name": "TYPE",
        "apparent_load_va": 31, "wattage_comments": "31W", "photometric_web_file": "L1.ies",
        "connector_count": 1, "reopened_verified": True,
    }), encoding="utf-8")
    result = update.validate_and_publish_l1_parameter_result(
        request_id=request_id,
        report_relative_path=f"scratch-reports/{request_id}.json",
        preview_path=preview,
        workspace_root=tmp_path,
        bridge_root=bridge,
    )
    assert Path(result["published_rvt"]).read_bytes() == b"result"
    assert Path(result["published_ies"]).read_bytes() == b"ies"
    assert Path(result["published_report"]).is_file()


def test_revit_writer_uses_absolute_verified_ies_path() -> None:
    source = (
        Path(__file__).parents[1]
        / "integrations/revit_readonly_plugin/ScratchL1ParameterUpdate.cs"
    ).read_text(encoding="utf-8")
    assert '"RVT " + activeSource.Application.VersionNumber, "IES"' in source
    assert "CopyExact(iesPath, revitIesPath, expectedIesHash);" in source
    assert "photometric.Set(revitIesPath)" in source
    assert "Path.GetFileName(photometric)" in source
