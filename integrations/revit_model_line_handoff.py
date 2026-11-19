"""Approval-bound new-file handoff and reopened verification for Revit Model Lines."""

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


def _atomic_request(pending: Path, request_id: str, payload: dict[str, object]) -> None:
    pending.parent.mkdir(parents=True, exist_ok=True)
    temporary = pending.with_name(f".{request_id}.tmp")
    temporary.write_text(json.dumps(payload, ensure_ascii=False), encoding="utf-8")
    os.replace(temporary, pending)


def _new_workspace_output(root: Path, output_model: Path) -> Path:
    workspace = (root / "workspace").resolve(strict=True)
    output = output_model.resolve(strict=False)
    if output.suffix.lower() != ".rvt" or not output.is_relative_to(workspace):
        raise RevitIntegrationError("Revit handoff output must be an RVT inside workspace.")
    if output.exists() or output.is_symlink():
        raise RevitIntegrationError("Revit handoff refuses to overwrite an existing output.")
    parent = output.parent.resolve(strict=True)
    if parent.is_symlink() or not parent.is_relative_to(workspace):
        raise RevitIntegrationError("Revit handoff output directory is unsafe.")
    return output


def submit_approved_model_lines_save_as(
    *,
    workspace_root: Path,
    preview_path: Path,
    source_model: Path,
    output_model: Path,
    confirmed_preview_sha256: str,
    bridge_root: Path | None = None,
) -> tuple[str, Path, Path]:
    """Submit one exact four-line transaction that may only save to a new RVT."""
    root = workspace_root.resolve(strict=True)
    preview = load_model_line_preview(preview_path, workspace_root=root)
    source = source_model.resolve(strict=True)
    output = _new_workspace_output(root, output_model)
    segments = preview.get("segments")
    if not isinstance(segments, list) or len(segments) != 4:
        raise RevitIntegrationError("First Revit handoff requires exactly four Model Lines.")
    if not source.is_file() or source.is_symlink() or not source.is_relative_to(root / "workspace"):
        raise RevitIntegrationError("Approved Revit source must be a workspace copy.")
    if source == output:
        raise RevitIntegrationError("Revit handoff output cannot replace its source.")
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
        raise RevitIntegrationError("Revit handoff backup verification failed.")
    request_id = str(uuid.uuid4())
    payload: dict[str, object] = {
        "schema_version": 1,
        "request_id": request_id,
        "action": "execute_model_lines_save_as",
        "expected_document_title": preview["document_title"],
        "provider_egress_permitted": False,
        "document_mutation_permitted": True,
        "approval_granted": True,
        "preview_id": preview["preview_id"],
        "preview_sha256": preview["preview_sha256"],
        "preview_file_sha256": preview_file_hash,
        "source_sha256": source_hash,
        "save_allowed": True,
        "output_path": str(output),
    }
    _atomic_request(pending, request_id, payload)
    return request_id, bridge / "responses" / f"{request_id}.json", backup


def wait_for_handoff(
    request_id: str,
    response_path: Path,
    *,
    bridge_root: Path,
    expected_output: Path,
    timeout_seconds: float = 60,
) -> dict[str, object]:
    """Validate the response, handoff evidence, output identity, and output hash."""
    report = _wait_report(
        request_id, response_path, bridge_root=bridge_root,
        action="execute_model_lines_save_as", timeout_seconds=timeout_seconds,
        mutation_expected=True,
    )
    output = expected_output.resolve(strict=True)
    ids = report.get("created_element_ids")
    if (
        report.get("request_id") != request_id
        or Path(str(report.get("output_path", ""))).resolve(strict=True) != output
        or report.get("created_model_lines") != 4
        or not isinstance(ids, list) or len(ids) != 4
        or report.get("transaction_committed") is not True
        or report.get("saved_as_new_file") is not True
        or report.get("output_sha256") != _hash(output)
    ):
        raise RevitIntegrationError("Revit handoff report is invalid.")
    return report


def submit_handoff_verification(
    *,
    workspace_root: Path,
    handoff_report: Path,
    bridge_root: Path | None = None,
) -> tuple[str, Path]:
    """Submit one read-only verification request for the reopened handoff RVT."""
    root = workspace_root.resolve(strict=True)
    bridge = (bridge_root or default_bridge_root()).resolve(strict=True)
    report_path = handoff_report.resolve(strict=True)
    if not report_path.is_file() or not report_path.is_relative_to(bridge / "handoffs"):
        raise RevitIntegrationError("Revit handoff evidence escaped the bridge root.")
    report = json.loads(report_path.read_text(encoding="utf-8-sig"))
    handoff_id = str(report.get("request_id", ""))
    preview_id = str(report.get("preview_id", ""))
    try:
        uuid.UUID(handoff_id)
        uuid.UUID(preview_id)
    except ValueError as exc:
        raise RevitIntegrationError("Revit handoff evidence identifiers are invalid.") from exc
    output = Path(str(report.get("output_path", ""))).resolve(strict=True)
    if not output.is_file() or not output.is_relative_to((root / "workspace").resolve(strict=True)):
        raise RevitIntegrationError("Revit handoff output escaped the workspace.")
    if report.get("output_sha256") != _hash(output):
        raise RevitIntegrationError("Revit handoff output hash changed before verification.")
    preview_path = bridge / "approved-previews" / f"{preview_id}.json"
    preview_hash = _hash(preview_path.resolve(strict=True))
    pending = bridge / "requests" / "pending.json"
    if pending.exists():
        raise RevitIntegrationError("A Revit bridge request is already pending.")
    request_id = str(uuid.uuid4())
    payload: dict[str, object] = {
        "schema_version": 1,
        "request_id": request_id,
        "action": "verify_model_lines_handoff",
        "expected_document_title": output.stem,
        "provider_egress_permitted": False,
        "document_mutation_permitted": False,
        "approval_granted": False,
        "save_allowed": False,
        "preview_id": preview_id,
        "preview_file_sha256": preview_hash,
        "handoff_request_id": handoff_id,
    }
    _atomic_request(pending, request_id, payload)
    return request_id, bridge / "responses" / f"{request_id}.json"


def wait_for_handoff_verification(
    request_id: str,
    response_path: Path,
    *,
    bridge_root: Path,
    handoff_request_id: str,
    timeout_seconds: float = 30,
) -> dict[str, object]:
    report = _wait_report(
        request_id, response_path, bridge_root=bridge_root,
        action="verify_model_lines_handoff", timeout_seconds=timeout_seconds,
        mutation_expected=False,
    )
    ids = report.get("verified_element_ids")
    if (
        report.get("request_id") != request_id
        or report.get("handoff_request_id") != handoff_request_id
        or report.get("verified_model_lines") != 4
        or not isinstance(ids, list) or len(ids) != 4
        or report.get("geometry_matches_preview") is not True
        or report.get("document_mutation_performed") is not False
    ):
        raise RevitIntegrationError("Revit reopened handoff verification is invalid.")
    return report


def _wait_report(
    request_id: str,
    response_path: Path,
    *,
    bridge_root: Path,
    action: str,
    timeout_seconds: float,
    mutation_expected: bool,
) -> dict[str, object]:
    if timeout_seconds <= 0 or timeout_seconds > 300:
        raise RevitIntegrationError("Revit handoff timeout must be within 0..300 seconds.")
    deadline = time.monotonic() + timeout_seconds
    while not response_path.is_file():
        if time.monotonic() >= deadline:
            raise RevitIntegrationError("Timed out waiting for Revit handoff response.")
        time.sleep(0.1)
    response = json.loads(response_path.read_text(encoding="utf-8-sig"))
    if (
        response.get("request_id") != request_id
        or response.get("action") != action
        or response.get("status") != "completed"
        or response.get("provider_egress_performed") is not False
        or response.get("document_mutation_performed") is not mutation_expected
    ):
        raise RevitIntegrationError("Revit handoff failed: " + str(response.get("error", "")))
    root = bridge_root.resolve(strict=True)
    output = (root / str(response.get("output_file", ""))).resolve(strict=True)
    if not output.is_file() or not output.is_relative_to(root):
        raise RevitIntegrationError("Revit handoff result escaped its bridge root.")
    value = json.loads(output.read_text(encoding="utf-8-sig"))
    if not isinstance(value, dict) or value.get("schema_version") != 1:
        raise RevitIntegrationError("Revit handoff result schema is invalid.")
    return value
