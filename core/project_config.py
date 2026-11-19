"""Strict, read-only loading of an explicitly scoped engineering project."""

from __future__ import annotations

import json
import re
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from agent.exceptions import ProjectConfigError

_MAX_CONFIG_BYTES = 65_536
_PROJECT_ID = re.compile(r"project:[A-Za-z0-9][A-Za-z0-9._-]{0,127}\Z")
_REQUIRED_FIELDS = ("schema_version", "project_id", "project_name", "workspace_root")


@dataclass(frozen=True, slots=True)
class ProjectConfig:
    """Project identity and one workspace boundary; no file access is granted here."""

    schema_version: int
    project_id: str
    project_name: str
    workspace_root: Path


def load_project_config(path: Path) -> ProjectConfig:
    """Read a small UTF-8 JSON object and validate its explicit workspace root."""
    config_path = Path(path)
    if not config_path.is_file():
        raise ProjectConfigError(f"Project configuration file does not exist: {config_path}")
    if _is_link(config_path):
        raise ProjectConfigError(f"Project configuration file must not be a link: {config_path}")
    try:
        if config_path.stat().st_size > _MAX_CONFIG_BYTES:
            raise ProjectConfigError("Project configuration exceeds the 65536-byte limit.")
        document = json.loads(config_path.read_text(encoding="utf-8"), object_pairs_hook=_unique_keys)
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise ProjectConfigError(f"Project configuration is not valid JSON: {config_path}: {exc}") from exc
    if not isinstance(document, dict):
        raise ProjectConfigError("Project configuration root must be a JSON object.")
    for field in _REQUIRED_FIELDS:
        if field not in document:
            raise ProjectConfigError(f"'{field}' is a required property.")
        if field == "schema_version" and (type(document[field]) is not int or document[field] != 1):
            raise ProjectConfigError("Unsupported schema version; expected 1.")
    if set(document) != set(_REQUIRED_FIELDS):
        raise ProjectConfigError("Project configuration has unsupported properties.")
    root_value = document["workspace_root"]
    if not isinstance(root_value, str) or not root_value.strip():
        raise ProjectConfigError("workspace_root must be a non-empty absolute path.")
    config = ProjectConfig(
        schema_version=document["schema_version"],
        project_id=document["project_id"],
        project_name=document["project_name"],
        workspace_root=Path(root_value),
    )
    return validate_config_paths(config)


def validate_config_paths(config: ProjectConfig) -> ProjectConfig:
    """Reject missing, linked, relative, or broad workspace roots without creating them."""
    if config.schema_version != 1:
        raise ProjectConfigError("Unsupported schema version; expected 1.")
    if not isinstance(config.project_id, str) or not _PROJECT_ID.fullmatch(config.project_id):
        raise ProjectConfigError("project_id must be a bounded 'project:' identifier.")
    if (
        not isinstance(config.project_name, str)
        or not config.project_name.strip()
        or len(config.project_name) > 200
        or any(ord(char) < 32 for char in config.project_name)
    ):
        raise ProjectConfigError("project_name must be printable text of at most 200 characters.")
    root = config.workspace_root
    if not isinstance(root, Path) or not root.is_absolute() or ".." in root.parts:
        raise ProjectConfigError("workspace_root must be an absolute path without traversal.")
    if root.parent == root:
        raise ProjectConfigError("workspace_root must not be a filesystem root.")
    if not root.exists() or not root.is_dir():
        raise ProjectConfigError(f"Workspace root directory does not exist: {root}")
    if any(_is_link(part) for part in (root, *root.parents)):
        raise ProjectConfigError("workspace_root must not contain a symlink or junction.")
    return ProjectConfig(1, config.project_id, config.project_name.strip(), root.resolve(strict=True))


def _is_link(path: Path) -> bool:
    return path.is_symlink() or path.is_junction()


def _unique_keys(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise ProjectConfigError(f"Duplicate project configuration property: {key}")
        result[key] = value
    return result
