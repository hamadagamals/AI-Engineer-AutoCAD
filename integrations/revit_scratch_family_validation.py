"""Approval-bound Revit scratch-family validation and verified publication."""

from __future__ import annotations

import hashlib
import csv
import io
import json
import os
import shutil
import time
import uuid
from dataclasses import dataclass
from pathlib import Path

from agent.exceptions import RevitIntegrationError
from integrations.revit_request_bridge import default_bridge_root


APPROVED_PREVIEW_SHA256 = "3F0ABF992B84973005619711766FDD5F5785DDC631D14DC87843DB42C7D28E7E"
ACTION = "execute_scratch_family_validation"


@dataclass(frozen=True, slots=True)
class ScratchFamilySpec:
    design_id: str
    source_rfa: Path
    expected_sha256: str
    type_name: str
    x_mm: float
    y_mm: float
    z_mm: float


def _hash(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def _inside(path: Path, root: Path) -> bool:
    return path == root or path.is_relative_to(root)


def _atomic_json(path: Path, request_id: str, value: dict[str, object]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(f".{request_id}.tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False), encoding="utf-8")
    os.replace(temporary, path)


def _sanitized_catalog_bytes(source: Path) -> bytes:
    """Remove only the malformed anonymous payload column from a reviewed type catalog."""
    try:
        with source.open("r", encoding="utf-16", newline="") as stream:
            rows = list(csv.reader(stream))
    except (OSError, UnicodeError, csv.Error) as exc:
        raise RevitIntegrationError(f"Revit type catalog cannot be parsed: {exc}") from exc
    if len(rows) != 2 or len(rows[0]) != len(rows[1]) or not rows[1][0].strip():
        raise RevitIntegrationError("Revit type catalog must contain one balanced reviewed type row.")
    keep = [index for index, header in enumerate(rows[0]) if header != "None##OTHER##"]
    if len(keep) == len(rows[0]):
        raise RevitIntegrationError("Revit type catalog lacks the reviewed malformed payload column.")
    output = io.StringIO(newline="")
    writer = csv.writer(output, lineterminator="\r\n")
    for row in rows:
        writer.writerow([row[index] for index in keep])
    return output.getvalue().encode("utf-16")


def _stage_catalog(source: Path, target: Path, request_id: str) -> None:
    original = target.with_name(target.stem + ".source.txt")
    if original.exists() and _hash(original) != _hash(source):
        raise RevitIntegrationError("Existing original Revit type catalog differs from its source.")
    if not original.exists():
        shutil.copy2(source, original)
    desired = _sanitized_catalog_bytes(source)
    if target.exists() and target.read_bytes() == desired:
        return
    temporary = target.with_name(f".{request_id}-{target.name}.tmp")
    temporary.write_bytes(desired)
    os.replace(temporary, target)


def submit_approved_scratch_family_validation(
    *,
    preview_path: Path,
    confirmed_preview_sha256: str,
    families: list[ScratchFamilySpec],
    allowed_family_root: Path,
    bridge_root: Path | None = None,
    expected_document_title: str = "School-Architectural",
) -> tuple[str, Path]:
    """Stage four exact RFA/type bindings and submit the approved scratch action."""
    preview = preview_path.resolve(strict=True)
    if preview.is_symlink() or _hash(preview) != APPROVED_PREVIEW_SHA256:
        raise RevitIntegrationError("Scratch-family preview file does not match the approved SHA-256.")
    if confirmed_preview_sha256.upper() != APPROVED_PREVIEW_SHA256:
        raise RevitIntegrationError("Scratch-family approval SHA-256 does not match.")
    if len(families) != 4 or len({item.design_id for item in families}) != 4:
        raise RevitIntegrationError("Scratch-family validation requires four unique design items.")
    title = expected_document_title.strip()
    if not title or len(title) > 260:
        raise RevitIntegrationError("Expected Revit document title is invalid.")

    allowed = allowed_family_root.resolve(strict=True)
    bridge = (bridge_root or default_bridge_root()).resolve(strict=False)
    pending = bridge / "requests" / "pending.json"
    if pending.exists():
        raise RevitIntegrationError("A Revit bridge request is already pending.")
    staged_items: list[dict[str, object]] = []
    for item in families:
        source = item.source_rfa.resolve(strict=True)
        expected_hash = item.expected_sha256.upper()
        if (
            source.is_symlink()
            or not _inside(source, allowed)
            or source.suffix.lower() != ".rfa"
            or len(expected_hash) != 64
            or _hash(source) != expected_hash
        ):
            raise RevitIntegrationError(f"Scratch-family source is invalid: {item.design_id}")
        if not item.design_id or len(item.design_id) > 32 or not item.type_name or len(item.type_name) > 260:
            raise RevitIntegrationError("Scratch-family design/type identity is invalid.")
        target_directory = bridge / "scratch-family-intake" / expected_hash
        target_directory.mkdir(parents=True, exist_ok=True)
        target = target_directory / source.name
        if target.exists() and _hash(target) != expected_hash:
            raise RevitIntegrationError("Existing staged Revit family has an unexpected hash.")
        if not target.exists():
            shutil.copy2(source, target)
        catalog = source.with_suffix(".txt")
        if catalog.is_file() and not catalog.is_symlink():
            staged_catalog = target.with_suffix(".txt")
            _stage_catalog(catalog, staged_catalog, "catalog")
        staged_items.append({
            "design_id": item.design_id,
            "rfa_relative_path": str(Path(expected_hash) / source.name),
            "type_name": item.type_name,
            "x_mm": item.x_mm,
            "y_mm": item.y_mm,
            "z_mm": item.z_mm,
        })

    request_id = str(uuid.uuid4())
    _atomic_json(pending, request_id, {
        "schema_version": 1,
        "request_id": request_id,
        "action": ACTION,
        "expected_document_title": title,
        "provider_egress_permitted": False,
        "document_mutation_permitted": True,
        "approval_granted": True,
        "preview_sha256": APPROVED_PREVIEW_SHA256,
        "save_allowed": True,
        "scratch_family_items": staged_items,
    })
    return request_id, bridge / "responses" / f"{request_id}.json"


def wait_for_scratch_family_response(
    request_id: str,
    response_path: Path,
    *,
    timeout_seconds: float = 180.0,
) -> str:
    """Wait for the one approved mutation response and return its bounded report path."""
    try:
        uuid.UUID(request_id)
    except ValueError as exc:
        raise RevitIntegrationError("Scratch-family request id is invalid.") from exc
    if not 0 < timeout_seconds <= 300:
        raise RevitIntegrationError("Scratch-family timeout must be within 0..300 seconds.")
    deadline = time.monotonic() + timeout_seconds
    while not response_path.is_file():
        if time.monotonic() >= deadline:
            raise RevitIntegrationError("Timed out waiting for the scratch-family response.")
        time.sleep(0.1)
    if response_path.stat().st_size > 65_536:
        raise RevitIntegrationError("Scratch-family response exceeds 64 KiB.")
    try:
        response = json.loads(response_path.read_text(encoding="utf-8-sig"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise RevitIntegrationError(f"Scratch-family response is invalid JSON: {exc}") from exc
    if (
        response.get("schema_version") != 1
        or response.get("request_id") != request_id
        or response.get("action") != ACTION
        or response.get("status") != "completed"
        or response.get("provider_egress_performed") is not False
        or response.get("document_mutation_performed") is not True
        or response.get("error") != ""
    ):
        raise RevitIntegrationError("Scratch-family response failed strict validation.")
    output = response.get("output_file")
    if not isinstance(output, str) or not output.startswith("scratch-reports/") or len(output) > 100:
        raise RevitIntegrationError("Scratch-family response report path is invalid.")
    return output


def validate_and_publish_scratch_result(
    *,
    request_id: str,
    report_relative_path: str,
    workspace_root: Path,
    destination: Path,
    bridge_root: Path | None = None,
) -> dict[str, object]:
    """Validate reopened Revit evidence and atomically publish the new RVT without overwrite."""
    bridge = (bridge_root or default_bridge_root()).resolve(strict=True)
    report = (bridge / report_relative_path).resolve(strict=True)
    if not _inside(report, bridge / "scratch-reports") or report.stat().st_size > 262_144:
        raise RevitIntegrationError("Scratch-family report path or size is invalid.")
    try:
        value = json.loads(report.read_text(encoding="utf-8-sig"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise RevitIntegrationError(f"Scratch-family report is invalid JSON: {exc}") from exc
    instances = value.get("instances")
    if (
        value.get("schema_version") != 1
        or value.get("request_id") != request_id
        or value.get("source_document_mutated") is not False
        or value.get("reopened_verified") is not True
        or not isinstance(instances, list)
        or len(instances) != 4
        or {item.get("design_id") for item in instances if isinstance(item, dict)} != {"L1", "L2", "L4", "L5-L6"}
    ):
        raise RevitIntegrationError("Scratch-family report failed identity or reopen verification.")
    for item in instances:
        if (
            not isinstance(item, dict)
            or item.get("category") != "Lighting Fixtures"
            or not isinstance(item.get("connector_count"), int)
            or item["connector_count"] < 1
            or item.get("bounding_box_present") is not True
            or not isinstance(item.get("geometry_object_count"), int)
            or item["geometry_object_count"] < 1
            or not isinstance(item.get("parameters"), list)
        ):
            raise RevitIntegrationError("Scratch-family instance verification is incomplete.")
    output = Path(str(value.get("output_rvt", ""))).resolve(strict=True)
    if not _inside(output, bridge / "scratch-output") or output.suffix.lower() != ".rvt":
        raise RevitIntegrationError("Scratch-family output path is outside the bridge output directory.")
    output_hash = value.get("output_sha256")
    if not isinstance(output_hash, str) or _hash(output) != output_hash.upper():
        raise RevitIntegrationError("Scratch-family output hash is invalid.")
    workspace = workspace_root.resolve(strict=True)
    final = destination.resolve(strict=False)
    if final.suffix.lower() != ".rvt" or not _inside(final, workspace / "workspace"):
        raise RevitIntegrationError("Scratch-family destination must be an RVT inside workspace.")
    if final.exists() or final.is_symlink():
        raise RevitIntegrationError("Scratch-family publication refuses to overwrite an output.")
    final.parent.resolve(strict=True)
    temporary = final.with_name(f".{request_id}.tmp")
    shutil.copy2(output, temporary)
    if _hash(temporary) != output_hash.upper():
        temporary.unlink(missing_ok=True)
        raise RevitIntegrationError("Scratch-family published copy hash verification failed.")
    os.replace(temporary, final)
    value["published_rvt"] = str(final)
    return value
