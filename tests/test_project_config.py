"""Tests for safe project configuration loading and validation."""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from agent.exceptions import ProjectConfigError
from core.project_config import (
    ProjectConfig,
    load_project_config,
    validate_config_paths,
)


def _create_config_file(path: Path, content: dict) -> Path:
    """Helper to write a config file for a test."""
    config_file = path / "ai-engineer.json"
    config_file.write_text(json.dumps(content), encoding="utf-8")
    return config_file


def test_load_valid_config_is_successful(tmp_path: Path) -> None:
    """Tests that a valid configuration file is loaded and parsed correctly."""
    workspace_dir = tmp_path / "workspace"
    workspace_dir.mkdir()
    config_content = {
        "schema_version": 1,
        "project_id": "project:test-123",
        "project_name": "Test Project",
        "workspace_root": str(workspace_dir),
    }
    config_file = _create_config_file(tmp_path, config_content)

    config = load_project_config(config_file)

    assert isinstance(config, ProjectConfig)
    assert config.project_id == "project:test-123"
    assert config.project_name == "Test Project"
    assert config.workspace_root == workspace_dir.resolve()


def test_load_config_missing_file_raises_error(tmp_path: Path) -> None:
    """Tests that a ProjectConfigError is raised for a missing config file."""
    with pytest.raises(ProjectConfigError, match="does not exist"):
        load_project_config(tmp_path / "non-existent-config.json")


def test_load_invalid_json_raises_error(tmp_path: Path) -> None:
    """Tests that a ProjectConfigError is raised for malformed JSON."""
    config_file = tmp_path / "ai-engineer.json"
    config_file.write_text("{ 'invalid_json': True, }", encoding="utf-8")

    with pytest.raises(ProjectConfigError, match="is not valid JSON"):
        load_project_config(config_file)


@pytest.mark.parametrize(
    ("field_to_remove", "error_match"),
    [
        ("schema_version", "schema_version"),
        ("project_id", "project_id"),
        ("project_name", "project_name"),
        ("workspace_root", "workspace_root"),
    ],
)
def test_load_config_with_missing_required_field_raises_error(
    tmp_path: Path, field_to_remove: str, error_match: str
) -> None:
    """Tests that missing required fields in the config cause a validation error."""
    workspace_dir = tmp_path / "workspace"
    workspace_dir.mkdir()
    config_content = {
        "schema_version": 1,
        "project_id": "project:test-123",
        "project_name": "Test Project",
        "workspace_root": str(workspace_dir),
    }
    del config_content[field_to_remove]
    config_file = _create_config_file(tmp_path, config_content)

    with pytest.raises(ProjectConfigError, match=f"'{error_match}' is a required property"):
        load_project_config(config_file)


def test_validate_paths_rejects_non_existent_workspace(tmp_path: Path) -> None:
    """Tests that path validation fails if the workspace_root does not exist."""
    config = ProjectConfig(
        schema_version=1,
        project_id="project:test-123",
        project_name="Test Project",
        workspace_root=tmp_path / "non_existent_workspace",
    )
    with pytest.raises(ProjectConfigError, match="Workspace root directory does not exist"):
        validate_config_paths(config)


def test_load_config_with_unsupported_schema_version_raises_error(tmp_path: Path) -> None:
    """Tests that an unsupported schema version is rejected."""
    config_file = _create_config_file(tmp_path, {"schema_version": 99})
    with pytest.raises(ProjectConfigError, match="Unsupported schema version"):
        load_project_config(config_file)


def test_rejects_relative_or_filesystem_root(tmp_path: Path) -> None:
    with pytest.raises(ProjectConfigError, match="absolute path"):
        validate_config_paths(ProjectConfig(1, "project:test", "Test", Path("workspace")))
    with pytest.raises(ProjectConfigError, match="filesystem root"):
        validate_config_paths(ProjectConfig(1, "project:test", "Test", Path(tmp_path.anchor)))


def test_rejects_duplicate_keys_and_unexpected_fields(tmp_path: Path) -> None:
    config_file = tmp_path / "ai-engineer.json"
    config_file.write_text('{"schema_version": 1, "schema_version": 1}', encoding="utf-8")
    with pytest.raises(ProjectConfigError, match="Duplicate"):
        load_project_config(config_file)
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    config_file = _create_config_file(tmp_path, {
        "schema_version": 1, "project_id": "project:test", "project_name": "Test",
        "workspace_root": str(workspace), "api_key": "must-not-be-here",
    })
    with pytest.raises(ProjectConfigError, match="unsupported properties"):
        load_project_config(config_file)


def test_rejects_large_config_and_linked_workspace(tmp_path: Path) -> None:
    config_file = tmp_path / "ai-engineer.json"
    config_file.write_text(" " * 65_537, encoding="utf-8")
    with pytest.raises(ProjectConfigError, match="65536-byte limit"):
        load_project_config(config_file)
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    linked = tmp_path / "linked"
    try:
        linked.symlink_to(workspace, target_is_directory=True)
    except OSError:
        pytest.skip("This Windows profile cannot create directory symlinks.")
    with pytest.raises(ProjectConfigError, match="symlink or junction"):
        validate_config_paths(ProjectConfig(1, "project:test", "Test", linked))
