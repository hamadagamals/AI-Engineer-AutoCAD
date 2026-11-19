"""Approval-bound isolated Revit L1 parameter update and verified publication."""

from __future__ import annotations

import hashlib
import json
import os
import shutil
import time
import uuid
from pathlib import Path
from typing import Any

from agent.exceptions import RevitIntegrationError
from integrations.revit_request_bridge import default_bridge_root


ACTION = "execute_l1_parameter_update"
APPROVED_PREVIEW_SHA256 = "310E62C9FBAD3B1AE50A06B3D9983AA10355AABD26718134E527C3EFACFB8FDA"


def _hash(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def _canonical_hash(value: dict[str, Any]) -> str:
    encoded = json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest().upper()


def _inside(path: Path, root: Path) -> bool:
    return path == root or path.is_relative_to(root)


def submit_approved_l1_parameter_update(
    *,
    preview_path: Path,
    confirmed_preview_sha256: str,
    workspace_root: Path,
    expected_document_title: str = "School-Lighting-Family-Validation",
    bridge_root: Path | None = None,
) -> tuple[str, Path]:
    """Validate the exact preview/source/IES evidence and submit one fixed action."""
    workspace = workspace_root.resolve(strict=True)
    preview_file = preview_path.resolve(strict=True)
    try:
        preview = json.loads(preview_file.read_text(encoding="utf-8-sig"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise RevitIntegrationError(f"L1 parameter preview is invalid: {exc}") from exc
    embedded = preview.pop("preview_sha256", None)
    if (
        confirmed_preview_sha256.upper() != APPROVED_PREVIEW_SHA256
        or embedded != APPROVED_PREVIEW_SHA256
        or _canonical_hash(preview) != APPROVED_PREVIEW_SHA256
        or preview.get("action") != "update_l1_parameters_in_new_scratch_rvt"
        or preview.get("execution_allowed") is not False
        or preview.get("save_allowed") is not False
    ):
        raise RevitIntegrationError("L1 parameter preview does not match the exact approval.")
    source = workspace / "workspace/revit-school/scratch/School-Lighting-Family-Validation.rvt"
    source = source.resolve(strict=True)
    if source.is_symlink() or _hash(source) != preview.get("source_scratch_rvt_sha256"):
        raise RevitIntegrationError("L1 scratch source does not match the approved preview.")
    ies = workspace / "workspace/revit-school/official-library/extracted" / str(preview.get("ies_relative_path", ""))
    ies = ies.resolve(strict=True)
    library = (workspace / "workspace/revit-school/official-library/extracted").resolve(strict=True)
    if (
        ies.is_symlink()
        or not _inside(ies, library)
        or ies.suffix.lower() != ".ies"
        or _hash(ies) != str(preview.get("ies_sha256", "")).upper()
    ):
        raise RevitIntegrationError("L1 IES evidence does not match the approved preview.")

    bridge = (bridge_root or default_bridge_root()).resolve(strict=False)
    pending = bridge / "requests" / "pending.json"
    if pending.exists():
        raise RevitIntegrationError("A Revit bridge request is already pending.")
    ies_hash = _hash(ies)
    intake = bridge / "scratch-parameter-intake" / ies_hash
    intake.mkdir(parents=True, exist_ok=True)
    staged = intake / ies.name
    if staged.exists() and _hash(staged) != ies_hash:
        raise RevitIntegrationError("Existing staged L1 IES has an unexpected hash.")
    if not staged.exists():
        shutil.copy2(ies, staged)
    request_id = str(uuid.uuid4())
    payload = {
        "schema_version": 1,
        "request_id": request_id,
        "action": ACTION,
        "expected_document_title": expected_document_title,
        "provider_egress_permitted": False,
        "document_mutation_permitted": True,
        "approval_granted": True,
        "preview_sha256": APPROVED_PREVIEW_SHA256,
        "source_sha256": _hash(source),
        "save_allowed": True,
        "ies_relative_path": str(Path(ies_hash) / ies.name),
        "ies_sha256": ies_hash,
    }
    pending.parent.mkdir(parents=True, exist_ok=True)
    temporary = pending.with_name(f".{request_id}.tmp")
    temporary.write_text(json.dumps(payload, ensure_ascii=False), encoding="utf-8")
    os.replace(temporary, pending)
    return request_id, bridge / "responses" / f"{request_id}.json"


def wait_for_l1_parameter_response(
    request_id: str, response_path: Path, *, timeout_seconds: float = 180.0
) -> str:
    """Wait for and strictly validate the fixed-action bridge response."""
    try:
        uuid.UUID(request_id)
    except ValueError as exc:
        raise RevitIntegrationError("L1 parameter request id is invalid.") from exc
    if not 0 < timeout_seconds <= 300:
        raise RevitIntegrationError("L1 parameter timeout must be within 0..300 seconds.")
    deadline = time.monotonic() + timeout_seconds
    while not response_path.is_file():
        if time.monotonic() >= deadline:
            raise RevitIntegrationError("Timed out waiting for the L1 parameter response.")
        time.sleep(0.1)
    try:
        response = json.loads(response_path.read_text(encoding="utf-8-sig"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise RevitIntegrationError(f"L1 parameter response is invalid: {exc}") from exc
    if (
        response.get("schema_version") != 1
        or response.get("request_id") != request_id
        or response.get("action") != ACTION
        or response.get("status") != "completed"
        or response.get("document_mutation_performed") is not True
        or response.get("provider_egress_performed") is not False
        or response.get("error") != ""
    ):
        raise RevitIntegrationError("L1 parameter response failed strict validation.")
    output = response.get("output_file")
    if not isinstance(output, str) or not output.startswith("scratch-reports/") or len(output) > 100:
        raise RevitIntegrationError("L1 parameter report path is invalid.")
    return output


def validate_and_publish_l1_parameter_result(
    *,
    request_id: str,
    report_relative_path: str,
    preview_path: Path,
    workspace_root: Path,
    bridge_root: Path | None = None,
) -> dict[str, Any]:
    """Verify reopened evidence, then atomically publish the new RVT, IES, and report."""
    workspace = workspace_root.resolve(strict=True)
    bridge = (bridge_root or default_bridge_root()).resolve(strict=True)
    report_path = (bridge / report_relative_path).resolve(strict=True)
    if not _inside(report_path, bridge / "scratch-reports") or report_path.stat().st_size > 131_072:
        raise RevitIntegrationError("L1 parameter report path or size is invalid.")
    try:
        report = json.loads(report_path.read_text(encoding="utf-8-sig"))
        preview = json.loads(preview_path.resolve(strict=True).read_text(encoding="utf-8-sig"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise RevitIntegrationError(f"L1 parameter publication evidence is invalid: {exc}") from exc
    if (
        report.get("schema_version") != 1
        or report.get("request_id") != request_id
        or report.get("source_document_mutated") is not False
        or report.get("reopened_verified") is not True
        or report.get("source_sha256_before") != preview.get("source_scratch_rvt_sha256")
        or report.get("source_sha256_after") != preview.get("source_scratch_rvt_sha256")
        or report.get("ies_sha256") != preview.get("ies_sha256")
        or report.get("family_name") != preview.get("target", {}).get("family_name")
        or report.get("type_name") != preview.get("target", {}).get("type_name")
        or report.get("apparent_load_va") != 31
        or report.get("wattage_comments") != "31W"
        or report.get("photometric_web_file") != Path(str(preview.get("ies_relative_path"))).name
        or report.get("connector_count") != 1
    ):
        raise RevitIntegrationError("L1 parameter report failed reopened verification.")
    output = Path(str(report.get("output_rvt", ""))).resolve(strict=True)
    if not _inside(output, bridge / "scratch-output") or _hash(output) != str(report.get("output_sha256", "")).upper():
        raise RevitIntegrationError("L1 parameter output hash or path is invalid.")
    destination = (workspace / str(preview.get("output_relative_path", ""))).resolve(strict=False)
    if not _inside(destination, workspace / "workspace") or destination.suffix.lower() != ".rvt" or destination.exists():
        raise RevitIntegrationError("L1 parameter destination is invalid or already exists.")
    destination.parent.resolve(strict=True)
    ies_source = workspace / "workspace/revit-school/official-library/extracted" / str(preview["ies_relative_path"])
    ies_destination = destination.parent / Path(str(preview["ies_relative_path"])).name
    report_destination = destination.with_suffix(".verification.json")
    if ies_destination.exists() and _hash(ies_destination) != preview["ies_sha256"]:
        raise RevitIntegrationError("Published L1 IES destination contains different data.")
    if report_destination.exists():
        raise RevitIntegrationError("L1 verification report destination already exists.")

    temporary = destination.with_name(f".{request_id}.tmp")
    shutil.copy2(output, temporary)
    if _hash(temporary) != report["output_sha256"]:
        temporary.unlink(missing_ok=True)
        raise RevitIntegrationError("Published L1 RVT hash differs from verified output.")
    os.replace(temporary, destination)

    if not ies_destination.exists():
        shutil.copy2(ies_source, ies_destination)
    shutil.copy2(report_path, report_destination)
    report["published_rvt"] = str(destination)
    report["published_ies"] = str(ies_destination)
    report["published_report"] = str(report_destination)
    return report
