"""Tests for sandboxed file operations."""

from __future__ import annotations

import logging
import os
import subprocess
from datetime import UTC, datetime
from pathlib import Path

import pytest

from agent.exceptions import (
    ConfirmationRequiredError,
    FileManagerError,
    FileSizeLimitError,
    PathPolicyError,
)
from core.file_manager import FileManager


@pytest.fixture
def workspace(tmp_path: Path) -> Path:
    root = tmp_path / "workspace"
    root.mkdir()
    return root


@pytest.fixture
def manager(tmp_path: Path, workspace: Path) -> FileManager:
    return FileManager(
        [workspace],
        tmp_path / "backup",
        clock=lambda: datetime(2026, 8, 5, 12, 0, tzinfo=UTC),
    )


def test_resolve_path_accepts_relative_path_inside_workspace(
    manager: FileManager, workspace: Path
) -> None:
    assert manager.resolve_path("folder/file.txt") == workspace / "folder" / "file.txt"


@pytest.mark.parametrize("path", [Path("..") / "escape.txt", Path("C:/outside.txt")])
def test_resolve_path_rejects_paths_outside_workspace(
    manager: FileManager, path: Path
) -> None:
    with pytest.raises(PathPolicyError, match="outside"):
        manager.resolve_path(path)


def test_relative_path_is_ambiguous_with_multiple_roots(tmp_path: Path) -> None:
    first = tmp_path / "first"
    second = tmp_path / "second"
    manager = FileManager([first, second], tmp_path / "backup")

    with pytest.raises(PathPolicyError, match="exactly one"):
        manager.resolve_path("file.txt")


def test_manager_requires_unique_workspace_roots(tmp_path: Path) -> None:
    workspace = tmp_path / "workspace"

    with pytest.raises(PathPolicyError, match="unique"):
        FileManager([workspace, workspace], tmp_path / "backup")


def test_manager_requires_at_least_one_workspace_root(tmp_path: Path) -> None:
    with pytest.raises(PathPolicyError, match="At least one"):
        FileManager([], tmp_path / "backup")


def test_backup_root_cannot_be_inside_workspace(workspace: Path) -> None:
    with pytest.raises(PathPolicyError, match="backup root"):
        FileManager([workspace], workspace / "backup")


def test_workspace_cannot_be_inside_backup_root(tmp_path: Path) -> None:
    backup = tmp_path / "storage"
    workspace = backup / "workspace"

    with pytest.raises(PathPolicyError, match="backup root"):
        FileManager([workspace], backup)


def test_symlink_escape_is_rejected(
    tmp_path: Path, manager: FileManager, workspace: Path
) -> None:
    outside = tmp_path / "outside"
    outside.mkdir()
    link = workspace / "escape"
    if os.name == "nt":
        result = subprocess.run(
            ["cmd.exe", "/d", "/c", "mklink", "/J", str(link), str(outside)],
            capture_output=True,
            text=True,
            timeout=10,
            check=False,
        )
        assert result.returncode == 0, result.stderr or result.stdout
    else:
        link.symlink_to(outside, target_is_directory=True)

    with pytest.raises(PathPolicyError, match="outside"):
        manager.resolve_path(link / "file.txt")


def test_write_and_read_utf8_text_atomically(
    manager: FileManager, workspace: Path
) -> None:
    result = manager.write_text("nested/message.txt", "مرحبًا", create_parents=True)

    assert result.action == "created"
    assert result.path == workspace / "nested" / "message.txt"
    assert result.backup_path is None
    assert result.bytes_written == len("مرحبًا".encode("utf-8"))
    assert manager.read_text(result.path) == "مرحبًا"
    assert not list(result.path.parent.glob("*.tmp"))


def test_write_does_not_create_parents_without_permission(
    manager: FileManager, workspace: Path
) -> None:
    with pytest.raises(FileManagerError, match="Parent directory"):
        manager.write_text("missing/file.txt", "content")

    assert not (workspace / "missing").exists()


def test_overwrite_requires_confirmation_and_creates_backup(
    manager: FileManager, workspace: Path
) -> None:
    target = workspace / "design.txt"
    target.write_text("original", encoding="utf-8")

    with pytest.raises(ConfirmationRequiredError, match="overwrite"):
        manager.write_text(target, "updated")

    result = manager.write_text(target, "updated", confirm_overwrite=True)

    assert target.read_text(encoding="utf-8") == "updated"
    assert result.action == "overwritten"
    assert result.backup_path is not None
    assert result.backup_path.read_text(encoding="utf-8") == "original"
    assert result.backup_path.is_relative_to(manager.backup_root)


def test_repeated_overwrites_never_replace_an_existing_backup(
    manager: FileManager, workspace: Path
) -> None:
    target = workspace / "revisions.txt"
    target.write_text("version one", encoding="utf-8")

    first = manager.write_text(target, "version two", confirm_overwrite=True)
    second = manager.write_text(target, "version three", confirm_overwrite=True)

    assert first.backup_path is not None
    assert second.backup_path is not None
    assert first.backup_path != second.backup_path
    assert first.backup_path.read_text(encoding="utf-8") == "version one"
    assert second.backup_path.read_text(encoding="utf-8") == "version two"


def test_backup_failure_prevents_overwrite(
    monkeypatch: pytest.MonkeyPatch, manager: FileManager, workspace: Path
) -> None:
    target = workspace / "protected.txt"
    target.write_text("original", encoding="utf-8")

    def fail_copy(source: Path, destination: Path) -> None:
        raise OSError(f"simulated backup failure: {source} -> {destination}")

    monkeypatch.setattr("core.file_manager.shutil.copy2", fail_copy)

    with pytest.raises(FileManagerError, match="Cannot back up"):
        manager.write_text(target, "replacement", confirm_overwrite=True)

    assert target.read_text(encoding="utf-8") == "original"
    assert not list(manager.backup_root.rglob("*.tmp"))


def test_delete_requires_confirmation_and_creates_backup(
    manager: FileManager, workspace: Path
) -> None:
    target = workspace / "drawing.txt"
    target.write_text("recoverable", encoding="utf-8")

    with pytest.raises(ConfirmationRequiredError, match="delete"):
        manager.delete_file(target)

    result = manager.delete_file(target, confirmed=True)

    assert not target.exists()
    assert result.backup_path is not None
    assert result.backup_path.read_text(encoding="utf-8") == "recoverable"


def test_read_rejects_file_over_size_limit(tmp_path: Path, workspace: Path) -> None:
    target = workspace / "large.txt"
    target.write_text("12345", encoding="utf-8")
    manager = FileManager([workspace], tmp_path / "backup", max_read_bytes=4)

    with pytest.raises(FileSizeLimitError, match="read limit"):
        manager.read_text(target)


def test_read_allows_bounded_call_specific_limit(tmp_path: Path, workspace: Path) -> None:
    target = workspace / "saved-official-page.html"
    target.write_text("12345", encoding="utf-8")
    manager = FileManager([workspace], tmp_path / "backup", max_read_bytes=4)

    assert manager.read_text(target, maximum_bytes=5) == "12345"
    with pytest.raises(ValueError, match="2,097,152"):
        manager.read_text(target, maximum_bytes=2_097_153)


def test_read_bytes_preserves_exact_newlines_and_enforces_limit(
    tmp_path: Path, workspace: Path,
) -> None:
    target = workspace / "official.html"
    target.write_bytes(b"first\r\nsecond\r\n")
    manager = FileManager([workspace], tmp_path / "backup", max_read_bytes=4)

    assert manager.read_bytes(target, maximum_bytes=32) == b"first\r\nsecond\r\n"
    with pytest.raises(FileSizeLimitError, match="read limit"):
        manager.read_bytes(target)


def test_read_rejects_non_utf8_file(manager: FileManager, workspace: Path) -> None:
    target = workspace / "binary.dat"
    target.write_bytes(b"\xff\xfe")

    with pytest.raises(FileManagerError, match="UTF-8"):
        manager.read_text(target)


def test_directory_cannot_be_read_or_deleted(
    manager: FileManager, workspace: Path
) -> None:
    directory = workspace / "folder"
    directory.mkdir()

    with pytest.raises(FileManagerError, match="regular file"):
        manager.read_text(directory)
    with pytest.raises(FileManagerError, match="regular file"):
        manager.delete_file(directory, confirmed=True)


def test_workspace_root_cannot_be_a_mutation_target(
    manager: FileManager, workspace: Path
) -> None:
    with pytest.raises(PathPolicyError, match="workspace root"):
        manager.write_text(workspace, "content", confirm_overwrite=True)


def test_mutations_emit_audit_log_records(
    tmp_path: Path, workspace: Path
) -> None:
    records: list[logging.LogRecord] = []

    class CaptureHandler(logging.Handler):
        def emit(self, record: logging.LogRecord) -> None:
            records.append(record)

    logger = logging.getLogger(f"test.file_manager.{os.getpid()}")
    logger.handlers = [CaptureHandler()]
    logger.setLevel(logging.INFO)
    logger.propagate = False
    manager = FileManager([workspace], tmp_path / "backup", logger=logger)

    manager.write_text("audit.txt", "content")
    manager.read_text("audit.txt")
    manager.delete_file("audit.txt", confirmed=True)

    assert [record.getMessage() for record in records] == [
        "file_written",
        "file_read",
        "file_backed_up",
        "file_deleted",
    ]
