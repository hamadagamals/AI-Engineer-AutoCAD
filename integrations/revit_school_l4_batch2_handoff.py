"""Approval-bound Revit handoff for the exact school L4 gym batch 2."""

from __future__ import annotations

import hashlib
import json
import os
import shutil
import time
import uuid
from datetime import UTC, datetime
from pathlib import Path

from agent.exceptions import RevitIntegrationError
from integrations.revit_request_bridge import default_bridge_root
from integrations.revit_school_l4_batch2_preview import load_preview


ACTION = "execute_school_l4_batch2_save_as"
VERIFY_ACTION = "verify_school_l4_batch2_handoff"
APPROVED_FINGERPRINT = "565529255B384C229F43B446A1A7169037E07DF63AD0F17D744FF37F50943801"


def _hash(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def _write(path: Path, request_id: str, value: dict[str, object]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(f".{request_id}.tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False), encoding="utf-8")
    os.replace(temporary, path)


def submit_batch2(
    *, workspace_root: Path, preview_path: Path, confirmed_fingerprint: str,
    bridge_root: Path | None = None,
) -> tuple[str, Path, Path, Path]:
    root = workspace_root.resolve(strict=True)
    preview = load_preview(preview_path, workspace_root=root)
    if confirmed_fingerprint.upper() != APPROVED_FINGERPRINT or preview["preview_sha256"] != APPROVED_FINGERPRINT:
        raise RevitIntegrationError("School L4 batch-2 approval fingerprint does not match.")
    source = (root / preview["source"]["model_relative_path"]).resolve(strict=True)
    output = (root / preview["output"]["model_relative_path"]).resolve(strict=False)
    if source.is_symlink() or _hash(source) != preview["source"]["model_sha256"]:
        raise RevitIntegrationError("School L4 batch-2 source changed after preview.")
    if output.exists() or output.is_symlink() or output.suffix.lower() != ".rvt" or not output.is_relative_to(root / "workspace"):
        raise RevitIntegrationError("School L4 batch-2 output is unsafe or already exists.")
    bridge = (bridge_root or default_bridge_root()).resolve(strict=False)
    pending = bridge / "requests" / "pending.json"
    if pending.exists():
        raise RevitIntegrationError("A Revit bridge request is already pending.")
    approved = bridge / "approved-previews" / f"{preview['preview_id']}.json"
    approved.parent.mkdir(parents=True, exist_ok=True)
    if approved.exists() and _hash(approved) != _hash(preview_path):
        raise RevitIntegrationError("Existing approved batch-2 preview differs.")
    if not approved.exists():
        shutil.copy2(preview_path, approved)
    backups = root / "backup" / "revit"
    backups.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now(UTC).strftime("%Y%m%dT%H%M%SZ")
    backup = backups / f"{source.stem}-{stamp}-{_hash(source)[:12]}.rvt"
    shutil.copy2(source, backup)
    if _hash(backup) != preview["source"]["model_sha256"]:
        raise RevitIntegrationError("School L4 batch-2 backup verification failed.")
    request_id = str(uuid.uuid4())
    _write(pending, request_id, {
        "schema_version": 1, "request_id": request_id, "action": ACTION,
        "expected_document_title": preview["source"]["document_title"],
        "provider_egress_permitted": False, "document_mutation_permitted": True,
        "approval_granted": True, "preview_id": preview["preview_id"],
        "preview_sha256": preview["preview_sha256"], "preview_file_sha256": _hash(approved),
        "source_sha256": preview["source"]["model_sha256"], "save_allowed": True,
        "output_path": str(output),
    })
    return request_id, bridge / "responses" / f"{request_id}.json", backup, output


def _wait(request_id: str, response_path: Path, *, action: str, bridge: Path, timeout_seconds: float) -> dict[str, object]:
    deadline = time.monotonic() + timeout_seconds
    while not response_path.is_file():
        if time.monotonic() >= deadline:
            raise RevitIntegrationError(f"Timed out waiting for Revit {action} response.")
        time.sleep(0.1)
    response = json.loads(response_path.read_text(encoding="utf-8-sig"))
    expected_mutation = action == ACTION
    if (response.get("request_id") != request_id or response.get("action") != action
            or response.get("status") != "completed" or response.get("provider_egress_performed") is not False
            or response.get("document_mutation_performed") is not expected_mutation):
        raise RevitIntegrationError("School L4 batch-2 Revit request failed: " + str(response.get("error", "")))
    report = (bridge / str(response.get("output_file", ""))).resolve(strict=True)
    if not report.is_file() or not report.is_relative_to(bridge):
        raise RevitIntegrationError("School L4 batch-2 report escaped its bridge root.")
    return json.loads(report.read_text(encoding="utf-8-sig"))


def wait_batch2(request_id: str, response_path: Path, *, output: Path, bridge_root: Path | None = None, timeout_seconds: float = 300) -> dict[str, object]:
    bridge = (bridge_root or default_bridge_root()).resolve(strict=True)
    report = _wait(request_id, response_path, action=ACTION, bridge=bridge, timeout_seconds=timeout_seconds)
    existing = report.get("existing_element_ids")
    created = report.get("created_element_ids")
    connectors = report.get("connector_counts")
    if (report.get("preview_sha256") != APPROVED_FINGERPRINT
            or not isinstance(existing, list) or len(existing) != 23
            or not isinstance(created, list) or len(created) != 22 or len(set(existing + created)) != 45
            or report.get("created_family_instances") != 22 or report.get("verified_total_instances") != 45
            or not isinstance(connectors, list) or connectors != [1] * 45
            or report.get("transaction_committed") is not True or report.get("saved_as_new_file") is not True
            or Path(str(report.get("output_path", ""))).resolve(strict=True) != output.resolve(strict=True)
            or report.get("output_sha256") != _hash(output.resolve(strict=True))):
        raise RevitIntegrationError("School L4 batch-2 handoff report failed validation.")
    return report


def submit_verification(*, handoff_request_id: str, output: Path, preview_id: str, bridge_root: Path | None = None) -> tuple[str, Path]:
    bridge = (bridge_root or default_bridge_root()).resolve(strict=True)
    preview = bridge / "approved-previews" / f"{preview_id}.json"
    handoff = bridge / "school-l4-batch2-handoffs" / f"{handoff_request_id}.json"
    if not output.is_file() or not preview.is_file() or not handoff.is_file():
        raise RevitIntegrationError("School L4 batch-2 verification evidence is missing.")
    pending = bridge / "requests" / "pending.json"
    if pending.exists():
        raise RevitIntegrationError("A Revit bridge request is already pending.")
    request_id = str(uuid.uuid4())
    _write(pending, request_id, {
        "schema_version": 1, "request_id": request_id, "action": VERIFY_ACTION,
        "expected_document_title": output.stem, "provider_egress_permitted": False,
        "document_mutation_permitted": False, "approval_granted": False,
        "save_allowed": False, "preview_id": preview_id,
        "preview_file_sha256": _hash(preview), "handoff_request_id": handoff_request_id,
    })
    return request_id, bridge / "responses" / f"{request_id}.json"


def wait_verification(request_id: str, response_path: Path, *, handoff_request_id: str, bridge_root: Path | None = None, timeout_seconds: float = 120) -> dict[str, object]:
    bridge = (bridge_root or default_bridge_root()).resolve(strict=True)
    report = _wait(request_id, response_path, action=VERIFY_ACTION, bridge=bridge, timeout_seconds=timeout_seconds)
    ids = report.get("verified_element_ids")
    connectors = report.get("connector_counts")
    if (report.get("handoff_request_id") != handoff_request_id or report.get("verified_total_instances") != 45
            or not isinstance(ids, list) or len(ids) != 45 or len(set(ids)) != 45
            or report.get("placement_matches_preview") is not True
            or not isinstance(connectors, list) or connectors != [1] * 45
            or report.get("document_mutation_performed") is not False):
        raise RevitIntegrationError("School L4 batch-2 reopened verification failed validation.")
    return report
