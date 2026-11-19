"""Approval-bound isolated Revit L2 and L5-L6 parameter update."""

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


ACTION = "execute_l2_l5_l6_parameter_update"
APPROVED_PREVIEW_SHA256 = "8359BB401FE1EC0221E116948B540AEC4118125E6A0BAB51B44672B22021781D"


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


def submit_approved_remaining_parameter_update(
    *, preview_path: Path, confirmed_preview_sha256: str, workspace_root: Path,
    bridge_root: Path | None = None,
) -> tuple[str, Path]:
    """Validate the exact preview and stage its two immutable IES inputs."""
    workspace = workspace_root.resolve(strict=True)
    try:
        preview = json.loads(preview_path.resolve(strict=True).read_text(encoding="utf-8-sig"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise RevitIntegrationError(f"Remaining parameter preview is invalid: {exc}") from exc
    embedded = preview.pop("preview_sha256", None)
    targets = preview.get("targets")
    if (
        confirmed_preview_sha256.upper() != APPROVED_PREVIEW_SHA256
        or embedded != APPROVED_PREVIEW_SHA256
        or _canonical_hash(preview) != APPROVED_PREVIEW_SHA256
        or preview.get("action") != "update_l2_l5_l6_parameters_in_new_scratch_rvt"
        or preview.get("execution_allowed") is not False
        or preview.get("save_allowed") is not False
        or not isinstance(targets, list)
        or [item.get("design_id") for item in targets] != ["L2", "L5-L6"]
    ):
        raise RevitIntegrationError("Remaining parameter preview does not match the exact approval.")
    source = workspace / "workspace/revit-school/scratch/School-Lighting-L1-Parameter-Validation.rvt"
    source = source.resolve(strict=True)
    if source.is_symlink() or _hash(source) != preview.get("source_scratch_rvt_sha256"):
        raise RevitIntegrationError("Remaining parameter source does not match the approved preview.")

    library = (workspace / "workspace/revit-school/official-library/extracted").resolve(strict=True)
    bridge = (bridge_root or default_bridge_root()).resolve(strict=False)
    staged_values: list[tuple[str, str]] = []
    for target in targets:
        ies = (library / str(target.get("ies_relative_path", ""))).resolve(strict=True)
        expected = str(target.get("ies_sha256", "")).upper()
        if ies.is_symlink() or not _inside(ies, library) or ies.suffix.lower() != ".ies" or _hash(ies) != expected:
            raise RevitIntegrationError("Remaining parameter IES evidence differs from approval.")
        intake = bridge / "scratch-parameter-intake" / expected
        intake.mkdir(parents=True, exist_ok=True)
        staged = intake / ies.name
        if staged.exists() and _hash(staged) != expected:
            raise RevitIntegrationError("Existing staged IES differs from approval.")
        if not staged.exists():
            shutil.copy2(ies, staged)
        staged_values.append((str(Path(expected) / ies.name), expected))

    pending = bridge / "requests/pending.json"
    if pending.exists():
        raise RevitIntegrationError("A Revit bridge request is already pending.")
    request_id = str(uuid.uuid4())
    payload = {
        "schema_version": 1, "request_id": request_id, "action": ACTION,
        "expected_document_title": "School-Lighting-L1-Parameter-Validation",
        "provider_egress_permitted": False, "document_mutation_permitted": True,
        "approval_granted": True, "preview_sha256": APPROVED_PREVIEW_SHA256,
        "source_sha256": _hash(source), "save_allowed": True,
        "ies_relative_path": staged_values[0][0], "ies_sha256": staged_values[0][1],
        "ies_relative_path_2": staged_values[1][0], "ies_sha256_2": staged_values[1][1],
    }
    pending.parent.mkdir(parents=True, exist_ok=True)
    temporary = pending.with_name(f".{request_id}.tmp")
    temporary.write_text(json.dumps(payload, ensure_ascii=False), encoding="utf-8")
    os.replace(temporary, pending)
    return request_id, bridge / "responses" / f"{request_id}.json"


def wait_for_remaining_parameter_response(
    request_id: str, response_path: Path, *, timeout_seconds: float = 240.0,
) -> str:
    """Wait for and validate the exact fixed-action response."""
    try:
        uuid.UUID(request_id)
    except ValueError as exc:
        raise RevitIntegrationError("Remaining parameter request id is invalid.") from exc
    if not 0 < timeout_seconds <= 300:
        raise RevitIntegrationError("Remaining parameter timeout must be within 0..300 seconds.")
    deadline = time.monotonic() + timeout_seconds
    while not response_path.is_file():
        if time.monotonic() >= deadline:
            raise RevitIntegrationError("Timed out waiting for remaining parameter response.")
        time.sleep(0.1)
    try:
        response = json.loads(response_path.read_text(encoding="utf-8-sig"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise RevitIntegrationError(f"Remaining parameter response is invalid: {exc}") from exc
    if (
        response.get("schema_version") != 1 or response.get("request_id") != request_id
        or response.get("action") != ACTION or response.get("status") != "completed"
        or response.get("document_mutation_performed") is not True
        or response.get("provider_egress_performed") is not False or response.get("error") != ""
    ):
        raise RevitIntegrationError("Remaining parameter response failed strict validation.")
    output = response.get("output_file")
    if not isinstance(output, str) or not output.startswith("scratch-reports/") or len(output) > 100:
        raise RevitIntegrationError("Remaining parameter report path is invalid.")
    return output


def validate_and_publish_remaining_parameter_result(
    *, request_id: str, report_relative_path: str, preview_path: Path,
    workspace_root: Path, bridge_root: Path | None = None,
) -> dict[str, Any]:
    """Validate reopened evidence and publish the new RVT, two IES files, and report."""
    workspace = workspace_root.resolve(strict=True)
    bridge = (bridge_root or default_bridge_root()).resolve(strict=True)
    report_path = (bridge / report_relative_path).resolve(strict=True)
    if not _inside(report_path, bridge / "scratch-reports") or report_path.stat().st_size > 131_072:
        raise RevitIntegrationError("Remaining parameter report path or size is invalid.")
    try:
        report = json.loads(report_path.read_text(encoding="utf-8-sig"))
        preview = json.loads(preview_path.resolve(strict=True).read_text(encoding="utf-8-sig"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise RevitIntegrationError(f"Remaining parameter publication evidence is invalid: {exc}") from exc
    targets = report.get("targets")
    expected_targets = preview.get("targets")
    if (
        report.get("schema_version") != 1 or report.get("request_id") != request_id
        or report.get("source_document_mutated") is not False or report.get("reopened_verified") is not True
        or report.get("source_sha256_before") != preview.get("source_scratch_rvt_sha256")
        or report.get("source_sha256_after") != preview.get("source_scratch_rvt_sha256")
        or report.get("l1_preserved") is not True or not isinstance(targets, list) or len(targets) != 2
        or not isinstance(expected_targets, list)
    ):
        raise RevitIntegrationError("Remaining parameter report failed source or reopen validation.")
    by_id = {item.get("design_id"): item for item in targets if isinstance(item, dict)}
    expected_by_id = {item.get("design_id"): item for item in expected_targets if isinstance(item, dict)}
    checks = (("L2", 19, "19W"), ("L5-L6", 30, "30W"))
    for design_id, load, watts in checks:
        item = by_id.get(design_id)
        expected = expected_by_id.get(design_id)
        if (
            not isinstance(item, dict) or not isinstance(expected, dict)
            or item.get("family_name") != expected.get("family_name")
            or item.get("type_name") != expected.get("type_name")
            or item.get("apparent_load_va") != load or item.get("wattage_comments") != watts
            or item.get("photometric_web_file") != Path(str(expected.get("ies_relative_path"))).name
            or item.get("ies_sha256") != expected.get("ies_sha256") or item.get("connector_count") != 1
        ):
            raise RevitIntegrationError(f"Remaining parameter verification failed for {design_id}.")
    output = Path(str(report.get("output_rvt", ""))).resolve(strict=True)
    if not _inside(output, bridge / "scratch-output") or _hash(output) != str(report.get("output_sha256", "")).upper():
        raise RevitIntegrationError("Remaining parameter output hash or path is invalid.")
    destination = (workspace / str(preview.get("output_relative_path", ""))).resolve(strict=False)
    if not _inside(destination, workspace / "workspace") or destination.suffix.lower() != ".rvt" or destination.exists():
        raise RevitIntegrationError("Remaining parameter destination is invalid or already exists.")
    report_destination = destination.with_suffix(".verification.json")
    ies_pairs = []
    library = workspace / "workspace/revit-school/official-library/extracted"
    for item in expected_targets:
        source = (library / str(item["ies_relative_path"])).resolve(strict=True)
        target = destination.parent / source.name
        if target.exists() and _hash(target) != item["ies_sha256"]:
            raise RevitIntegrationError("Published remaining IES destination contains different data.")
        ies_pairs.append((source, target, item["ies_sha256"]))
    if report_destination.exists():
        raise RevitIntegrationError("Remaining verification report destination already exists.")
    temporary = destination.with_name(f".{request_id}.tmp")
    shutil.copy2(output, temporary)
    if _hash(temporary) != report["output_sha256"]:
        temporary.unlink(missing_ok=True)
        raise RevitIntegrationError("Published remaining RVT hash differs from verified output.")
    os.replace(temporary, destination)
    for source, target, _ in ies_pairs:
        if not target.exists():
            shutil.copy2(source, target)
    shutil.copy2(report_path, report_destination)
    report["published_rvt"] = str(destination)
    report["published_ies"] = [str(target) for _, target, _ in ies_pairs]
    report["published_report"] = str(report_destination)
    return report
