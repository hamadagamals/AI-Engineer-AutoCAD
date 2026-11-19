"""Bounded read-only intake and result validation for external Revit family files."""

from __future__ import annotations

import hashlib
import json
import os
import shutil
import uuid
from dataclasses import dataclass
from pathlib import Path

from agent.exceptions import RevitIntegrationError
from integrations.revit_request_bridge import default_bridge_root


MAXIMUM_FAMILY_FILES = 8
MAXIMUM_FAMILY_BYTES = 50 * 1024 * 1024
ALLOWED_HOST_REQUIREMENTS = frozenset({
    "level", "hosted_element_unresolved", "two_levels", "work_plane_or_face",
    "view", "curve", "detail_curve", "unsupported_or_unresolved",
})


@dataclass(frozen=True, slots=True)
class RevitFamilyFileQuery:
    request_id: str
    document_title: str
    files: tuple[dict[str, object], ...]


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def submit_family_file_request(
    expected_document_title: str,
    family_paths: list[Path] | tuple[Path, ...],
    *,
    allowed_root: Path,
    bridge_root: Path | None = None,
) -> tuple[str, Path]:
    """Stage and submit a bounded family-file query for the exact active document."""
    title = expected_document_title.strip()
    if not title or len(title) > 260:
        raise RevitIntegrationError("Expected Revit document title is invalid.")
    if not isinstance(family_paths, (list, tuple)) or not 1 <= len(family_paths) <= MAXIMUM_FAMILY_FILES:
        raise RevitIntegrationError("Revit family-file count must be within 1..8.")
    root = allowed_root.expanduser().resolve(strict=True)
    if not root.is_dir() or root.is_symlink():
        raise RevitIntegrationError("Revit family-file allowed root is invalid.")
    resolved: list[Path] = []
    for supplied in family_paths:
        candidate = supplied.expanduser().resolve(strict=True)
        if (
            not candidate.is_file()
            or candidate.is_symlink()
            or not candidate.is_relative_to(root)
            or candidate.suffix.lower() != ".rfa"
            or not 0 < candidate.stat().st_size <= MAXIMUM_FAMILY_BYTES
        ):
            raise RevitIntegrationError("Revit family file escaped or violated the intake contract.")
        resolved.append(candidate)

    bridge = (bridge_root or default_bridge_root()).expanduser().resolve(strict=False)
    pending = bridge / "requests" / "pending.json"
    if pending.exists():
        raise RevitIntegrationError("A Revit read-only request is already pending.")
    intake = bridge / "family-intake"
    intake.mkdir(parents=True, exist_ok=True)
    names: list[str] = []
    for candidate in resolved:
        digest = _sha256(candidate)
        name = f"{digest}.rfa"
        staged = intake / name
        if staged.exists():
            if staged.is_symlink() or not staged.is_file() or _sha256(staged) != digest:
                raise RevitIntegrationError("Staged Revit family hash is invalid.")
        else:
            temporary = intake / f".{uuid.uuid4()}.tmp"
            shutil.copyfile(candidate, temporary)
            if _sha256(temporary) != digest:
                temporary.unlink(missing_ok=True)
                raise RevitIntegrationError("Staged Revit family copy failed verification.")
            os.replace(temporary, staged)
        names.append(name)

    request_id = str(uuid.uuid4())
    value = {
        "schema_version": 1,
        "request_id": request_id,
        "action": "family_file_query",
        "expected_document_title": title,
        "provider_egress_permitted": False,
        "document_mutation_permitted": False,
        "family_files": names,
    }
    pending.parent.mkdir(parents=True, exist_ok=True)
    response_path = bridge / "responses" / f"{request_id}.json"
    response_path.parent.mkdir(parents=True, exist_ok=True)
    temporary = pending.with_name(f".{request_id}.tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False), encoding="utf-8")
    os.replace(temporary, pending)
    return request_id, response_path


def load_family_file_query(path: Path, *, allowed_root: Path) -> RevitFamilyFileQuery:
    root = allowed_root.expanduser().resolve(strict=True)
    candidate = path.expanduser().resolve(strict=True)
    if not candidate.is_file() or candidate.is_symlink() or not candidate.is_relative_to(root):
        raise RevitIntegrationError("Revit family-file result escaped its configured root.")
    if candidate.stat().st_size > 1_048_576:
        raise RevitIntegrationError("Revit family-file result exceeds 1 MiB.")
    try:
        value = json.loads(candidate.read_text(encoding="utf-8-sig"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise RevitIntegrationError(f"Revit family-file result is invalid JSON: {exc}") from exc
    if not isinstance(value, dict) or value.get("schema_version") != 1:
        raise RevitIntegrationError("Revit family-file result schema is invalid.")
    if value.get("provider_egress_performed") is not False or value.get("document_mutation_performed") is not False:
        raise RevitIntegrationError("Revit family-file result violated the read-only boundary.")
    files = value.get("files")
    if not isinstance(files, list) or not 1 <= len(files) <= MAXIMUM_FAMILY_FILES:
        raise RevitIntegrationError("Revit family-file result count is invalid.")
    for item in files:
        if not isinstance(item, dict):
            raise RevitIntegrationError("Revit family-file result item is invalid.")
        for field in ("file_sha256", "format", "family_name", "category", "family_placement_type"):
            if not isinstance(item.get(field), str) or len(item[field]) > 260:
                raise RevitIntegrationError(f"Revit family-file field {field} is invalid.")
        if len(item["file_sha256"]) != 64 or any(character not in "0123456789ABCDEF" for character in item["file_sha256"]):
            raise RevitIntegrationError("Revit family-file hash is invalid.")
        if item.get("host_requirement") not in ALLOWED_HOST_REQUIREMENTS:
            raise RevitIntegrationError("Revit family-file host requirement is invalid.")
        connectors = item.get("connectors")
        types = item.get("types")
        if not isinstance(connectors, list) or len(connectors) > 16 or not isinstance(types, list) or len(types) > 32:
            raise RevitIntegrationError("Revit family-file detail bounds are invalid.")
        for connector in connectors:
            if not isinstance(connector, dict) or not isinstance(connector.get("is_primary"), bool):
                raise RevitIntegrationError("Revit family-file connector is invalid.")
            for field in ("domain", "system_classification", "shape"):
                if not isinstance(connector.get(field), str) or len(connector[field]) > 260:
                    raise RevitIntegrationError(f"Revit family-file connector field {field} is invalid.")
        for family_type in types:
            if not isinstance(family_type, dict) or not isinstance(family_type.get("name"), str):
                raise RevitIntegrationError("Revit family-file type is invalid.")
            if len(family_type["name"]) > 260:
                raise RevitIntegrationError("Revit family-file type name is invalid.")
            parameters = family_type.get("parameters")
            if not isinstance(parameters, list) or len(parameters) > 32:
                raise RevitIntegrationError("Revit family-file parameter bounds are invalid.")
            for parameter in parameters:
                if not isinstance(parameter, dict) or not isinstance(parameter.get("is_instance"), bool):
                    raise RevitIntegrationError("Revit family-file parameter is invalid.")
                for field in ("name", "storage_type", "formula", "value"):
                    if not isinstance(parameter.get(field), str) or len(parameter[field]) > 260:
                        raise RevitIntegrationError(f"Revit family-file parameter field {field} is invalid.")
    return RevitFamilyFileQuery(
        request_id=str(value.get("request_id", "")),
        document_title=str(value.get("document_title", "")),
        files=tuple(files),
    )
