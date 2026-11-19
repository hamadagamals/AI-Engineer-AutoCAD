"""Approved bounded Revit family handoff with backup and re-open checks."""

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
from integrations.revit_preview import load_placement_preview
from integrations.revit_request_bridge import default_bridge_root


def _hash(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def _write_request(path: Path, request_id: str, value: dict[str, object]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(f".{request_id}.tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False), encoding="utf-8")
    os.replace(temporary, path)


def _new_output(root: Path, candidate: Path) -> Path:
    workspace = (root / "workspace").resolve(strict=True)
    output = candidate.resolve(strict=False)
    if output.suffix.lower() != ".rvt" or not output.is_relative_to(workspace):
        raise RevitIntegrationError("Revit family output must be an RVT inside workspace.")
    if output.exists() or output.is_symlink():
        raise RevitIntegrationError("Revit family placement refuses to overwrite an output.")
    parent = output.parent.resolve(strict=True)
    if parent.is_symlink() or not parent.is_relative_to(workspace):
        raise RevitIntegrationError("Revit family output directory is unsafe.")
    return output


def _bridge_output(physical_output: Path, bridge_output_path: Path | None) -> Path:
    """Accept a verified Windows drive alias for the same output directory."""
    if bridge_output_path is None:
        return physical_output
    alias = bridge_output_path.expanduser().absolute()
    if (
        alias.name != physical_output.name
        or alias.suffix.lower() != ".rvt"
        or alias.exists()
        or alias.is_symlink()
        or not alias.parent.is_dir()
        or not os.path.samefile(alias.parent, physical_output.parent)
    ):
        raise RevitIntegrationError("Revit output alias does not identify the approved workspace directory.")
    return alias


def submit_approved_family_placement_save_as(
    *,
    workspace_root: Path,
    preview_path: Path,
    source_model: Path,
    output_model: Path,
    confirmed_preview_sha256: str,
    bridge_root: Path | None = None,
    bridge_output_path: Path | None = None,
) -> tuple[str, Path, Path]:
    """Submit 1..32 approved OneLevelBased placements to a new workspace RVT."""
    root = workspace_root.resolve(strict=True)
    preview = load_placement_preview(preview_path, workspace_root=root)
    source = source_model.resolve(strict=True)
    output = _new_output(root, output_model)
    bridge_output = _bridge_output(output, bridge_output_path)
    if preview.get("family_placement_type") != "OneLevelBased" or preview.get("host_requirement") != "level":
        raise RevitIntegrationError("Only OneLevelBased family placement on a level is supported.")
    if not isinstance(preview.get("placements"), list) or not 1 <= len(preview["placements"]) <= 32:
        raise RevitIntegrationError("Family handoff requires 1..32 approved placements.")
    if not source.is_file() or source.is_symlink() or not source.is_relative_to(root / "workspace"):
        raise RevitIntegrationError("Approved Revit source must be a workspace copy.")
    if source == output:
        raise RevitIntegrationError("Revit family output cannot replace its source.")
    if preview["preview_sha256"] != confirmed_preview_sha256.upper():
        raise RevitIntegrationError("Approved Revit family preview SHA-256 does not match.")
    source_hash = _hash(source)
    if preview["source_sha256"] != source_hash or preview["source_file"] != source.name:
        raise RevitIntegrationError("Approved Revit family preview is not bound to this source.")
    bridge = (bridge_root or default_bridge_root()).resolve(strict=False)
    pending = bridge / "requests" / "pending.json"
    if pending.exists():
        raise RevitIntegrationError("A Revit bridge request is already pending.")
    approved = bridge / "approved-previews" / f"{preview['preview_id']}.json"
    approved.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(preview_path, approved)
    approved_hash = _hash(approved)
    backups = root / "backup" / "revit"
    backups.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now(UTC).strftime("%Y%m%dT%H%M%SZ")
    backup = backups / f"{source.stem}-{stamp}-{source_hash[:12]}.rvt"
    shutil.copy2(source, backup)
    if _hash(backup) != source_hash:
        raise RevitIntegrationError("Revit family backup verification failed.")
    request_id = str(uuid.uuid4())
    _write_request(pending, request_id, {
        "schema_version": 1, "request_id": request_id,
        "action": "execute_family_placement_save_as",
        "expected_document_title": preview["document_title"],
        "provider_egress_permitted": False, "document_mutation_permitted": True,
        "approval_granted": True, "preview_id": preview["preview_id"],
        "preview_sha256": preview["preview_sha256"], "preview_file_sha256": approved_hash,
        "source_sha256": source_hash, "save_allowed": True, "output_path": str(bridge_output),
    })
    return request_id, bridge / "responses" / f"{request_id}.json", backup


def _wait(
    request_id: str, response_path: Path, *, bridge_root: Path, action: str,
    mutation_expected: bool, timeout_seconds: float,
) -> dict[str, object]:
    if not 0 < timeout_seconds <= 300:
        raise RevitIntegrationError("Revit family handoff timeout must be within 0..300 seconds.")
    deadline = time.monotonic() + timeout_seconds
    while not response_path.is_file():
        if time.monotonic() >= deadline:
            raise RevitIntegrationError("Timed out waiting for Revit family handoff response.")
        time.sleep(0.1)
    response = json.loads(response_path.read_text(encoding="utf-8-sig"))
    if (response.get("request_id") != request_id or response.get("action") != action
            or response.get("status") != "completed" or response.get("provider_egress_performed") is not False
            or response.get("document_mutation_performed") is not mutation_expected):
        raise RevitIntegrationError("Revit family handoff failed: " + str(response.get("error", "")))
    root = bridge_root.resolve(strict=True)
    result = (root / str(response.get("output_file", ""))).resolve(strict=True)
    if not result.is_file() or not result.is_relative_to(root):
        raise RevitIntegrationError("Revit family handoff result escaped its bridge root.")
    value = json.loads(result.read_text(encoding="utf-8-sig"))
    if not isinstance(value, dict) or value.get("schema_version") != 1:
        raise RevitIntegrationError("Revit family handoff result schema is invalid.")
    return value


def wait_for_family_handoff(
    request_id: str, response_path: Path, *, bridge_root: Path, expected_output: Path,
    timeout_seconds: float = 60,
) -> dict[str, object]:
    report = _wait(request_id, response_path, bridge_root=bridge_root,
                   action="execute_family_placement_save_as", mutation_expected=True,
                   timeout_seconds=timeout_seconds)
    output = expected_output.resolve(strict=True)
    ids = report.get("created_element_ids")
    count = report.get("created_family_instances")
    preview_path = (
        bridge_root.resolve(strict=True)
        / "approved-previews"
        / f"{report.get('preview_id', '')}.json"
    ).resolve(strict=True)
    preview = json.loads(preview_path.read_text(encoding="utf-8-sig"))
    expected_count = len(preview.get("placements", [])) if isinstance(preview, dict) else 0
    reported_output = Path(str(report.get("output_path", "")))
    if (report.get("request_id") != request_id
            or not reported_output.is_file() or not os.path.samefile(reported_output, output)
            or not isinstance(count, int) or isinstance(count, bool) or not 1 <= count <= 32
            or count != expected_count or report.get("preview_sha256") != preview.get("preview_sha256")
            or not isinstance(ids, list) or len(ids) != count or len(set(ids)) != count
            or any(not isinstance(element_id, int) or isinstance(element_id, bool) or element_id <= 0 for element_id in ids)
            or report.get("transaction_committed") is not True or report.get("saved_as_new_file") is not True
            or report.get("output_sha256") != _hash(output)):
        raise RevitIntegrationError("Revit family handoff report is invalid.")
    return report


def submit_family_handoff_verification(
    *, workspace_root: Path, handoff_report: Path, bridge_root: Path | None = None,
    expected_output: Path | None = None,
) -> tuple[str, Path]:
    root = workspace_root.resolve(strict=True)
    bridge = (bridge_root or default_bridge_root()).resolve(strict=True)
    report_path = handoff_report.resolve(strict=True)
    allowed = bridge / "family-handoffs"
    if not report_path.is_file() or not report_path.is_relative_to(allowed):
        raise RevitIntegrationError("Revit family handoff evidence escaped the bridge root.")
    report = json.loads(report_path.read_text(encoding="utf-8-sig"))
    handoff_id, preview_id = str(report.get("request_id", "")), str(report.get("preview_id", ""))
    try:
        uuid.UUID(handoff_id); uuid.UUID(preview_id)
    except ValueError as exc:
        raise RevitIntegrationError("Revit family handoff identifiers are invalid.") from exc
    output = Path(str(report.get("output_path", ""))).resolve(strict=True)
    workspace = (root / "workspace").resolve(strict=True)
    physical = expected_output.resolve(strict=True) if expected_output is not None else output
    if (
        not output.is_file() or not physical.is_file()
        or not physical.is_relative_to(workspace)
        or not os.path.samefile(output, physical)
        or report.get("output_sha256") != _hash(physical)
    ):
        raise RevitIntegrationError("Revit family output changed before verification.")
    preview = bridge / "approved-previews" / f"{preview_id}.json"
    preview_hash = _hash(preview.resolve(strict=True))
    pending = bridge / "requests" / "pending.json"
    if pending.exists():
        raise RevitIntegrationError("A Revit bridge request is already pending.")
    request_id = str(uuid.uuid4())
    _write_request(pending, request_id, {
        "schema_version": 1, "request_id": request_id,
        "action": "verify_family_placement_handoff", "expected_document_title": output.stem,
        "provider_egress_permitted": False, "document_mutation_permitted": False,
        "approval_granted": False, "save_allowed": False, "preview_id": preview_id,
        "preview_file_sha256": preview_hash, "handoff_request_id": handoff_id,
    })
    return request_id, bridge / "responses" / f"{request_id}.json"


def wait_for_family_handoff_verification(
    request_id: str, response_path: Path, *, bridge_root: Path, handoff_request_id: str,
    timeout_seconds: float = 30,
) -> dict[str, object]:
    report = _wait(request_id, response_path, bridge_root=bridge_root,
                   action="verify_family_placement_handoff", mutation_expected=False,
                   timeout_seconds=timeout_seconds)
    ids = report.get("verified_element_ids")
    count = report.get("verified_family_instances")
    handoff_path = (
        bridge_root.resolve(strict=True) / "family-handoffs" / f"{handoff_request_id}.json"
    ).resolve(strict=True)
    handoff = json.loads(handoff_path.read_text(encoding="utf-8-sig"))
    expected_ids = handoff.get("created_element_ids") if isinstance(handoff, dict) else None
    if (report.get("request_id") != request_id or report.get("handoff_request_id") != handoff_request_id
            or not isinstance(count, int) or isinstance(count, bool) or not 1 <= count <= 32
            or not isinstance(ids, list) or len(ids) != count or len(set(ids)) != count
            or any(not isinstance(element_id, int) or isinstance(element_id, bool) or element_id <= 0 for element_id in ids)
            or ids != expected_ids or report.get("output_sha256") != handoff.get("output_sha256")
            or report.get("placement_matches_preview") is not True
            or report.get("document_mutation_performed") is not False):
        raise RevitIntegrationError("Revit family reopened handoff verification is invalid.")
    return report
