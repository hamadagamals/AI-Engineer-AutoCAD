"""Source-bound preview and approved Save-As correction for Revit fixture Marks."""

from __future__ import annotations

import hashlib
import json
import os
import shutil
import uuid
from datetime import UTC, datetime, timedelta
from pathlib import Path

from agent.exceptions import RevitIntegrationError
from integrations.revit_family_placement_handoff import _bridge_output, _hash, _new_output, _wait, _write_request
from integrations.revit_request_bridge import default_bridge_root


def _preview_hash(value: dict[str, object]) -> str:
    payload = {key: item for key, item in value.items() if key != "preview_sha256"}
    encoded = json.dumps(payload, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
    return hashlib.sha256(encoded.encode("utf-8")).hexdigest().upper()


def create_classroom1501_mark_preview(
    *, workspace_root: Path, source_model: Path, category_evidence: Path,
    original_placement_preview: Path, output_model: Path, preview_path: Path,
    expires_minutes: int = 120,
) -> dict[str, object]:
    """Bind the eight known fixture IDs, exact locations and old/new room Marks."""
    root = workspace_root.resolve(strict=True)
    workspace = (root / "workspace").resolve(strict=True)
    source = source_model.resolve(strict=True)
    output = _new_output(root, output_model)
    evidence_path = category_evidence.resolve(strict=True)
    original_path = original_placement_preview.resolve(strict=True)
    destination = preview_path.resolve(strict=False)
    if not all(path.is_relative_to(workspace) for path in (source, output, evidence_path, original_path, destination)):
        raise RevitIntegrationError("Fixture Mark preview paths must stay inside workspace.")
    if not 1 <= expires_minutes <= 120 or source == output or destination.exists():
        raise RevitIntegrationError("Fixture Mark preview expiry or new output is invalid.")
    original = json.loads(original_path.read_text(encoding="utf-8-sig"))
    category = json.loads(evidence_path.read_text(encoding="utf-8-sig"))
    fixtures = category.get("elements", [])
    positions = original.get("placements", [])
    if (category.get("category_key") != "lighting_fixtures"
            or category.get("total_count") != 61 or category.get("returned_count") != 8
            or category.get("truncated") is not False or len(fixtures) != 8
            or len(positions) != 8 or original.get("symbol_id") != 447405
            or original.get("level_id") != 30 or _hash(source) != "66405403F8A34A3F4E444E09E94B5FCD34D78AD67A2625F56756A3658EA8E3AB"):
        raise RevitIntegrationError("Fixture Mark source evidence is not the verified sample.")
    items: list[dict[str, object]] = []
    for index, (fixture, point) in enumerate(zip(fixtures, positions, strict=True), start=1):
        old = f"AIENG-L1-CLASSROOM-1500-{index + 8:02d}"
        new = f"AIENG-L1-CLASSROOM-1501-{index:02d}"
        location = fixture.get("location_point", {})
        if (fixture.get("element_id") != 457164 + index or fixture.get("type_id") != 447405
                or fixture.get("level_id") != 30 or fixture.get("parameters", {}).get("mark") != old
                or any(abs(float(location.get(f"{axis}_mm", float("inf"))) - float(point[f"{axis}_mm"])) > 0.01
                       for axis in ("x", "y", "z"))):
            raise RevitIntegrationError(f"Fixture Mark evidence item {index} changed.")
        items.append({
            "element_id": fixture["element_id"], "old_mark": old, "new_mark": new,
            "type_id": 447405, "level_id": 30,
            "x_mm": point["x_mm"], "y_mm": point["y_mm"], "z_mm": point["z_mm"],
        })
    now = datetime.now(UTC)
    preview: dict[str, object] = {
        "schema_version": 1, "preview_id": str(uuid.uuid4()),
        "created_utc": now.isoformat(),
        "expires_utc": (now + timedelta(minutes=expires_minutes)).isoformat(),
        "source_file": source.name, "source_sha256": _hash(source),
        "document_title": source.stem, "output_file": output.name,
        "action": "correct_fixture_marks", "items": items,
        "status": "review_required", "execution_allowed": False,
        "provider_egress_performed": False, "document_mutation_performed": False,
    }
    preview["preview_sha256"] = _preview_hash(preview)
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(json.dumps(preview, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return preview


def submit_approved_mark_correction(
    *, workspace_root: Path, preview_path: Path, source_model: Path, output_model: Path,
    confirmed_preview_sha256: str, bridge_root: Path | None = None,
    bridge_output_path: Path | None = None,
) -> tuple[str, Path, Path]:
    """Submit only the exact unexpired, operator-approved correction to Revit."""
    root = workspace_root.resolve(strict=True)
    workspace = (root / "workspace").resolve(strict=True)
    preview_file = preview_path.resolve(strict=True)
    source = source_model.resolve(strict=True)
    output = _new_output(root, output_model)
    if not preview_file.is_relative_to(workspace) or not source.is_relative_to(workspace):
        raise RevitIntegrationError("Fixture Mark source or preview escaped workspace.")
    preview = json.loads(preview_file.read_text(encoding="utf-8-sig"))
    try:
        expires = datetime.fromisoformat(str(preview["expires_utc"]))
        uuid.UUID(str(preview["preview_id"]))
    except (KeyError, ValueError, TypeError) as exc:
        raise RevitIntegrationError("Fixture Mark preview identity is invalid.") from exc
    if (expires.tzinfo is None or expires <= datetime.now(UTC)
            or preview.get("preview_sha256") != confirmed_preview_sha256.upper()
            or _preview_hash(preview) != confirmed_preview_sha256.upper()
            or preview.get("source_file") != source.name or preview.get("source_sha256") != _hash(source)
            or preview.get("document_title") != source.stem or preview.get("output_file") != output.name
            or preview.get("action") != "correct_fixture_marks"
            or preview.get("execution_allowed") is not False
            or preview.get("provider_egress_performed") is not False
            or preview.get("document_mutation_performed") is not False
            or not isinstance(preview.get("items"), list) or len(preview["items"]) != 8):
        raise RevitIntegrationError("Approved fixture Mark preview does not match its source or output.")
    alias = _bridge_output(output, bridge_output_path)
    bridge = (bridge_root or default_bridge_root()).resolve(strict=False)
    pending = bridge / "requests" / "pending.json"
    if pending.exists():
        raise RevitIntegrationError("A Revit bridge request is already pending.")
    approved = bridge / "approved-previews" / f"{preview['preview_id']}.json"
    approved.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(preview_file, approved)
    approved_hash = _hash(approved)
    backups = root / "backup" / "revit"
    backups.mkdir(parents=True, exist_ok=True)
    source_hash = _hash(source)
    backup = backups / f"{source.stem}-{datetime.now(UTC):%Y%m%dT%H%M%SZ}-{source_hash[:12]}.rvt"
    shutil.copy2(source, backup)
    if _hash(backup) != source_hash:
        raise RevitIntegrationError("Fixture Mark source backup verification failed.")
    request_id = str(uuid.uuid4())
    _write_request(pending, request_id, {
        "schema_version": 1, "request_id": request_id,
        "action": "execute_fixture_mark_correction_save_as",
        "expected_document_title": preview["document_title"],
        "provider_egress_permitted": False, "document_mutation_permitted": True,
        "approval_granted": True, "preview_id": preview["preview_id"],
        "preview_sha256": preview["preview_sha256"], "preview_file_sha256": approved_hash,
        "source_sha256": source_hash, "save_allowed": True, "output_path": str(alias),
    })
    return request_id, bridge / "responses" / f"{request_id}.json", backup


def wait_for_mark_correction(
    request_id: str, response_path: Path, *, bridge_root: Path, expected_output: Path,
    timeout_seconds: float = 60,
) -> dict[str, object]:
    report = _wait(request_id, response_path, bridge_root=bridge_root,
                   action="execute_fixture_mark_correction_save_as", mutation_expected=True,
                   timeout_seconds=timeout_seconds)
    output = expected_output.resolve(strict=True)
    ids = report.get("corrected_element_ids")
    reported = Path(str(report.get("output_path", "")))
    if (report.get("request_id") != request_id or not reported.is_file()
            or not os.path.samefile(reported, output) or report.get("output_sha256") != _hash(output)
            or report.get("corrected_count") != 8 or ids != list(range(457165, 457173))
            or report.get("transaction_committed") is not True
            or report.get("saved_as_new_file") is not True):
        raise RevitIntegrationError("Fixture Mark correction handoff report is invalid.")
    return report


def submit_mark_correction_verification(
    *, workspace_root: Path, handoff_report: Path, expected_output: Path,
    bridge_root: Path | None = None,
) -> tuple[str, Path]:
    root = workspace_root.resolve(strict=True)
    bridge = (bridge_root or default_bridge_root()).resolve(strict=True)
    report_path = handoff_report.resolve(strict=True)
    if not report_path.is_relative_to(bridge / "fixture-mark-handoffs"):
        raise RevitIntegrationError("Fixture Mark report escaped the bridge.")
    report = json.loads(report_path.read_text(encoding="utf-8-sig"))
    physical = expected_output.resolve(strict=True)
    reported = Path(str(report.get("output_path", ""))).resolve(strict=True)
    if (not physical.is_relative_to((root / "workspace").resolve(strict=True))
            or not os.path.samefile(physical, reported)
            or report.get("output_sha256") != _hash(physical)):
        raise RevitIntegrationError("Fixture Mark output changed before verification.")
    preview_id = str(report.get("preview_id", ""))
    handoff_id = str(report.get("request_id", ""))
    uuid.UUID(preview_id)
    uuid.UUID(handoff_id)
    preview = bridge / "approved-previews" / f"{preview_id}.json"
    preview_hash = _hash(preview.resolve(strict=True))
    pending = bridge / "requests" / "pending.json"
    if pending.exists():
        raise RevitIntegrationError("A Revit bridge request is already pending.")
    request_id = str(uuid.uuid4())
    _write_request(pending, request_id, {
        "schema_version": 1, "request_id": request_id,
        "action": "verify_fixture_mark_correction_handoff",
        "expected_document_title": physical.stem,
        "provider_egress_permitted": False, "document_mutation_permitted": False,
        "approval_granted": False, "preview_id": preview_id,
        "preview_file_sha256": preview_hash, "handoff_request_id": handoff_id,
        "save_allowed": False,
    })
    return request_id, bridge / "responses" / f"{request_id}.json"


def wait_for_mark_correction_verification(
    request_id: str, response_path: Path, *, bridge_root: Path,
    handoff_request_id: str, timeout_seconds: float = 30,
) -> dict[str, object]:
    report = _wait(request_id, response_path, bridge_root=bridge_root,
                   action="verify_fixture_mark_correction_handoff", mutation_expected=False,
                   timeout_seconds=timeout_seconds)
    handoff_path = bridge_root.resolve(strict=True) / "fixture-mark-handoffs" / f"{handoff_request_id}.json"
    handoff = json.loads(handoff_path.read_text(encoding="utf-8-sig"))
    if (report.get("request_id") != request_id
            or report.get("preview_id") != handoff.get("preview_id")
            or report.get("preview_sha256") != handoff.get("preview_sha256")
            or report.get("output_sha256") != handoff.get("output_sha256")
            or report.get("corrected_element_ids") != list(range(457165, 457173))
            or report.get("corrected_count") != 8
            or report.get("document_mutation_performed") is not False):
        raise RevitIntegrationError("Fixture Mark post-save verification is invalid.")
    return report
