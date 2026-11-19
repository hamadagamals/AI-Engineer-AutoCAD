"""Approval-bound request and result validation for Model Lines on a disposable RVT."""

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
from integrations.revit_model_line_preview import load_model_line_preview
from integrations.revit_request_bridge import default_bridge_root


def _hash(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def submit_approved_model_lines(
    *,
    workspace_root: Path,
    preview_path: Path,
    source_model: Path,
    confirmed_preview_sha256: str,
    bridge_root: Path | None = None,
) -> tuple[str, Path, Path]:
    root = workspace_root.resolve(strict=True)
    preview = load_model_line_preview(preview_path, workspace_root=root)
    source = source_model.resolve(strict=True)
    if not source.is_file() or source.is_symlink() or not source.is_relative_to(root):
        raise RevitIntegrationError("Approved Revit source must be a workspace copy.")
    if preview["preview_sha256"] != confirmed_preview_sha256.upper():
        raise RevitIntegrationError("Approved Revit preview SHA-256 does not match.")
    source_hash = _hash(source)
    if preview["source_sha256"] != source_hash or preview["source_file"] != source.name:
        raise RevitIntegrationError("Approved Revit preview is not bound to this source.")
    bridge = (bridge_root or default_bridge_root()).resolve(strict=False)
    pending = bridge / "requests" / "pending.json"
    if pending.exists():
        raise RevitIntegrationError("A Revit bridge request is already pending.")
    approved = bridge / "approved-previews" / f"{preview['preview_id']}.json"
    approved.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(preview_path, approved)
    preview_file_hash = _hash(approved)
    backup_root = root / "backup" / "revit"
    backup_root.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now(UTC).strftime("%Y%m%dT%H%M%SZ")
    backup = backup_root / f"{source.stem}-{stamp}-{source_hash[:12]}.rvt"
    shutil.copy2(source, backup)
    if _hash(backup) != source_hash:
        raise RevitIntegrationError("Revit execution backup verification failed.")
    request_id = str(uuid.uuid4())
    payload = {
        "schema_version": 1,
        "request_id": request_id,
        "action": "execute_model_lines",
        "expected_document_title": preview["document_title"],
        "provider_egress_permitted": False,
        "document_mutation_permitted": True,
        "approval_granted": True,
        "preview_id": preview["preview_id"],
        "preview_sha256": preview["preview_sha256"],
        "preview_file_sha256": preview_file_hash,
        "source_sha256": source_hash,
        "save_allowed": False,
    }
    pending.parent.mkdir(parents=True, exist_ok=True)
    temporary = pending.with_name(f".{request_id}.tmp")
    temporary.write_text(json.dumps(payload, ensure_ascii=False), encoding="utf-8")
    os.replace(temporary, pending)
    return request_id, bridge / "responses" / f"{request_id}.json", backup


def wait_for_execution(
    request_id: str, response_path: Path, *, bridge_root: Path, timeout_seconds: float = 30
) -> dict[str, object]:
    deadline = time.monotonic() + timeout_seconds
    while not response_path.is_file():
        if time.monotonic() >= deadline:
            raise RevitIntegrationError("Timed out waiting for Revit Model Lines execution.")
        time.sleep(0.1)
    response = json.loads(response_path.read_text(encoding="utf-8-sig"))
    if response.get("request_id") != request_id or response.get("action") != "execute_model_lines":
        raise RevitIntegrationError("Revit execution response correlation is invalid.")
    if response.get("status") != "completed" or response.get("document_mutation_performed") is not True:
        raise RevitIntegrationError("Revit execution failed: " + str(response.get("error", "")))
    if response.get("provider_egress_performed") is not False:
        raise RevitIntegrationError("Revit execution unexpectedly reports provider egress.")
    output = (bridge_root / str(response.get("output_file", ""))).resolve(strict=True)
    root = bridge_root.resolve(strict=True)
    if not output.is_file() or not output.is_relative_to(root):
        raise RevitIntegrationError("Revit execution result escaped its bridge root.")
    report = json.loads(output.read_text(encoding="utf-8-sig"))
    if (
        report.get("request_id") != request_id
        or report.get("created_model_lines") != 4
        or report.get("transaction_committed") is not True
        or report.get("saved") is not False
        or report.get("document_mutation_performed") is not True
    ):
        raise RevitIntegrationError("Revit execution report is invalid.")
    return report
