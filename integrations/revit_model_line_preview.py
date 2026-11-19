"""Create and validate exact, non-mutating Revit model-line previews."""

from __future__ import annotations

import hashlib
import json
import math
import os
import uuid
from datetime import UTC, datetime, timedelta
from pathlib import Path

from agent.exceptions import RevitIntegrationError


MAXIMUM_LINES = 16
MAXIMUM_COORDINATE_MM = 1_000_000.0


def _file_hash(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def _preview_hash(value: dict[str, object]) -> str:
    unsigned = {key: item for key, item in value.items() if key != "preview_sha256"}
    data = json.dumps(unsigned, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
    return hashlib.sha256(data.encode("utf-8")).hexdigest().upper()


def create_model_line_preview(
    *,
    workspace_root: Path,
    source_model: Path,
    expected_source_sha256: str,
    document_title: str,
    segments_mm: list[tuple[tuple[float, float, float], tuple[float, float, float]]],
    output_path: Path,
    expires_minutes: int = 30,
) -> dict[str, object]:
    root = workspace_root.resolve(strict=True)
    source = source_model.resolve(strict=True)
    output = output_path.resolve(strict=False)
    if not source.is_file() or source.is_symlink() or not source.is_relative_to(root):
        raise RevitIntegrationError("Revit line-preview source must be a workspace file.")
    if not output.is_relative_to(root):
        raise RevitIntegrationError("Revit line preview must stay inside the workspace.")
    source_hash = _file_hash(source)
    if source_hash != expected_source_sha256.upper():
        raise RevitIntegrationError("Revit line-preview source SHA-256 does not match.")
    if not document_title.strip() or len(document_title) > 260:
        raise RevitIntegrationError("Revit line-preview document title is invalid.")
    if not 1 <= len(segments_mm) <= MAXIMUM_LINES:
        raise RevitIntegrationError("Revit line preview requires 1..16 segments.")
    segments: list[dict[str, object]] = []
    for index, (start, end) in enumerate(segments_mm, start=1):
        values = (*start, *end)
        if any(
            not isinstance(value, (int, float)) or isinstance(value, bool)
            or not math.isfinite(value) or abs(value) > MAXIMUM_COORDINATE_MM
            for value in values
        ):
            raise RevitIntegrationError("Revit line-preview coordinate is invalid.")
        if abs(start[2] - end[2]) > 0.001:
            raise RevitIntegrationError("First Revit model-line gate requires horizontal segments.")
        length = math.dist(start, end)
        if length < 10.0 or length > 100_000.0:
            raise RevitIntegrationError("Revit model-line length must be within 10..100000 mm.")
        segments.append({
            "index": index,
            "start_mm": [round(float(item), 3) for item in start],
            "end_mm": [round(float(item), 3) for item in end],
            "length_mm": round(length, 3),
        })
    if not 1 <= expires_minutes <= 120:
        raise RevitIntegrationError("Revit line-preview expiry must be within 1..120 minutes.")
    created = datetime.now(UTC)
    value: dict[str, object] = {
        "schema_version": 1,
        "preview_id": str(uuid.uuid4()),
        "created_utc": created.isoformat(),
        "expires_utc": (created + timedelta(minutes=expires_minutes)).isoformat(),
        "source_file": source.name,
        "source_sha256": source_hash,
        "document_title": document_title.strip(),
        "action": "create_model_lines",
        "segments": segments,
        "status": "review_required",
        "execution_allowed": False,
        "save_allowed": False,
        "provider_egress_performed": False,
        "document_mutation_performed": False,
        "preview_sha256": "",
    }
    value["preview_sha256"] = _preview_hash(value)
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_name(f".{output.name}.{uuid.uuid4().hex}.tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")
    os.replace(temporary, output)
    return value


def load_model_line_preview(path: Path, *, workspace_root: Path) -> dict[str, object]:
    root = workspace_root.resolve(strict=True)
    candidate = path.resolve(strict=True)
    if not candidate.is_file() or candidate.is_symlink() or not candidate.is_relative_to(root):
        raise RevitIntegrationError("Revit line preview escaped the workspace.")
    value = json.loads(candidate.read_text(encoding="utf-8"))
    if not isinstance(value, dict) or value.get("schema_version") != 1:
        raise RevitIntegrationError("Revit line-preview schema is invalid.")
    if value.get("preview_sha256") != _preview_hash(value):
        raise RevitIntegrationError("Revit line-preview hash is invalid.")
    if value.get("action") != "create_model_lines" or value.get("status") != "review_required":
        raise RevitIntegrationError("Revit line-preview action or status is invalid.")
    if value.get("execution_allowed") is not False or value.get("save_allowed") is not False:
        raise RevitIntegrationError("Revit line preview unexpectedly permits execution or saving.")
    segments = value.get("segments")
    if not isinstance(segments, list) or not 1 <= len(segments) <= MAXIMUM_LINES:
        raise RevitIntegrationError("Revit line-preview segments are invalid.")
    return value
