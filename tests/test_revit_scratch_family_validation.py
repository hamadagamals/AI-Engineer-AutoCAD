from __future__ import annotations

import hashlib
import csv
import json
from pathlib import Path

import pytest

from agent.exceptions import RevitIntegrationError
import integrations.revit_scratch_family_validation as scratch


def _hash(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def _fixture(tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
    preview = tmp_path / "preview.json"
    preview.write_text("approved preview", encoding="utf-8")
    monkeypatch.setattr(scratch, "APPROVED_PREVIEW_SHA256", _hash(preview))
    families = tmp_path / "families"
    families.mkdir()
    specs = []
    for index, design_id in enumerate(("L1", "L2", "L4", "L5-L6")):
        rfa = families / f"{design_id}.rfa"
        rfa.write_bytes(f"family-{design_id}".encode())
        if design_id != "L4":
            with rfa.with_suffix(".txt").open("w", encoding="utf-16", newline="") as stream:
                csv.writer(stream).writerows([
                    ["", "Model##OTHER##", "None##OTHER##"],
                    [f"Type {design_id}", design_id, "malformed payload"],
                ])
        specs.append(scratch.ScratchFamilySpec(
            design_id, rfa, _hash(rfa), f"Type {design_id}", index * 3000, 0, 3000
        ))
    return preview, families, specs


def test_submit_stages_exact_rfas_and_catalogs(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    preview, families, specs = _fixture(tmp_path, monkeypatch)
    bridge = tmp_path / "bridge"
    request_id, response = scratch.submit_approved_scratch_family_validation(
        preview_path=preview,
        confirmed_preview_sha256=_hash(preview),
        families=specs,
        allowed_family_root=families,
        bridge_root=bridge,
    )
    payload = json.loads((bridge / "requests" / "pending.json").read_text(encoding="utf-8"))
    assert payload["request_id"] == request_id
    assert payload["action"] == scratch.ACTION
    assert payload["document_mutation_permitted"] is True
    assert payload["provider_egress_permitted"] is False
    assert len(payload["scratch_family_items"]) == 4
    first = payload["scratch_family_items"][0]
    assert (bridge / "scratch-family-intake" / first["rfa_relative_path"]).read_bytes() == b"family-L1"
    catalog = bridge / "scratch-family-intake" / Path(first["rfa_relative_path"]).with_suffix(".txt")
    assert "None##OTHER##" not in catalog.read_text(encoding="utf-16")
    assert catalog.with_name(catalog.stem + ".source.txt").is_file()
    assert response == bridge / "responses" / f"{request_id}.json"


def test_submit_rejects_wrong_approval_and_escape(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    preview, families, specs = _fixture(tmp_path, monkeypatch)
    with pytest.raises(RevitIntegrationError, match="approval"):
        scratch.submit_approved_scratch_family_validation(
            preview_path=preview, confirmed_preview_sha256="0" * 64, families=specs,
            allowed_family_root=families, bridge_root=tmp_path / "bridge",
        )
    outside = tmp_path / "outside.rfa"
    outside.write_bytes(b"outside")
    escaped = list(specs)
    escaped[0] = scratch.ScratchFamilySpec("L1", outside, _hash(outside), "Type L1", 0, 0, 3000)
    with pytest.raises(RevitIntegrationError, match="source"):
        scratch.submit_approved_scratch_family_validation(
            preview_path=preview, confirmed_preview_sha256=_hash(preview), families=escaped,
            allowed_family_root=families, bridge_root=tmp_path / "bridge2",
        )


def test_wait_accepts_only_approved_mutation_response(tmp_path: Path) -> None:
    request_id = "5f951098-2f23-47e8-b33d-e03685364828"
    response = tmp_path / "response.json"
    response.write_text(json.dumps({
        "schema_version": 1, "request_id": request_id, "action": scratch.ACTION,
        "status": "completed", "provider_egress_performed": False,
        "document_mutation_performed": True, "output_file": f"scratch-reports/{request_id}.json",
        "error": "",
    }), encoding="utf-8")
    assert scratch.wait_for_scratch_family_response(request_id, response) == f"scratch-reports/{request_id}.json"
    value = json.loads(response.read_text())
    value["document_mutation_performed"] = False
    response.write_text(json.dumps(value), encoding="utf-8")
    with pytest.raises(RevitIntegrationError, match="strict"):
        scratch.wait_for_scratch_family_response(request_id, response)


def test_validate_and_publish_checks_reopen_evidence_and_no_overwrite(tmp_path: Path) -> None:
    bridge = tmp_path / "bridge"
    output = bridge / "scratch-output" / "test.rvt"
    output.parent.mkdir(parents=True)
    output.write_bytes(b"verified-rvt")
    request_id = "5f951098-2f23-47e8-b33d-e03685364828"
    report = bridge / "scratch-reports" / f"{request_id}.json"
    report.parent.mkdir()
    instances = [{
        "design_id": item, "category": "Lighting Fixtures", "connector_count": 1,
        "bounding_box_present": True, "geometry_object_count": 2, "parameters": [],
    } for item in ("L1", "L2", "L4", "L5-L6")]
    report.write_text(json.dumps({
        "schema_version": 1, "request_id": request_id, "output_rvt": str(output),
        "output_sha256": _hash(output), "source_document_mutated": False,
        "reopened_verified": True, "instances": instances,
    }), encoding="utf-8")
    root = tmp_path / "project"
    destination = root / "workspace" / "revit-school" / "scratch" / "published.rvt"
    destination.parent.mkdir(parents=True)
    result = scratch.validate_and_publish_scratch_result(
        request_id=request_id, report_relative_path=f"scratch-reports/{request_id}.json",
        workspace_root=root, destination=destination, bridge_root=bridge,
    )
    assert destination.read_bytes() == output.read_bytes()
    assert result["published_rvt"] == str(destination)
    with pytest.raises(RevitIntegrationError, match="overwrite"):
        scratch.validate_and_publish_scratch_result(
            request_id=request_id, report_relative_path=f"scratch-reports/{request_id}.json",
            workspace_root=root, destination=destination, bridge_root=bridge,
        )
