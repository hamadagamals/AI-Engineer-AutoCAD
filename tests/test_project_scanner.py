"""Tests for bounded project metadata scanning."""

from __future__ import annotations

import json
import logging
import os
import subprocess
from pathlib import Path

import pytest

from agent.exceptions import ProjectScannerError
from core.file_manager import FileManager
from core.project_scanner import ProjectScanner, ScanLimits


@pytest.fixture
def workspace(tmp_path: Path) -> Path:
    root = tmp_path / "workspace"
    root.mkdir()
    return root


@pytest.fixture
def scanner(tmp_path: Path, workspace: Path) -> ProjectScanner:
    manager = FileManager([workspace], tmp_path / "backup")
    return ProjectScanner(manager)


def test_scan_returns_deterministic_metadata_and_project_files(
    scanner: ProjectScanner, workspace: Path
) -> None:
    (workspace / "src").mkdir()
    (workspace / "src" / "main.py").write_text("print('ok')", encoding="utf-8")
    (workspace / "README.md").write_text("# Project", encoding="utf-8")
    (workspace / "image.png").write_bytes(b"\x89PNG")

    result = scanner.scan(workspace)

    assert [item.relative_path for item in result.files] == [
        "image.png",
        "README.md",
        "src/main.py",
    ]
    assert {item.relative_path: item.category for item in result.files} == {
        "image.png": "binary",
        "README.md": "text",
        "src/main.py": "text",
    }
    assert result.directories_scanned == 2
    assert dict(result.extension_counts) == {".md": 1, ".png": 1, ".py": 1}
    assert result.project_files == ("README.md",)
    assert result.total_bytes == sum(item.size_bytes for item in result.files)
    assert not result.truncated


def test_scan_result_is_json_serializable(scanner: ProjectScanner, workspace: Path) -> None:
    (workspace / "file.txt").write_text("content", encoding="utf-8")

    payload = scanner.scan(workspace).to_dict()

    encoded = json.dumps(payload)
    assert json.loads(encoded)["files"][0]["relative_path"] == "file.txt"


def test_scan_ignores_heavy_directories(scanner: ProjectScanner, workspace: Path) -> None:
    cache = workspace / "node_modules"
    cache.mkdir()
    (cache / "dependency.js").write_text("ignored", encoding="utf-8")

    result = scanner.scan(workspace)

    assert result.files == ()
    assert result.skipped[0].reason == "ignored_directory"
    assert result.skipped[0].relative_path == "node_modules"


def test_custom_empty_ignore_set_scans_default_ignored_name(
    tmp_path: Path, workspace: Path
) -> None:
    cache = workspace / "node_modules"
    cache.mkdir()
    (cache / "dependency.js").write_text("included", encoding="utf-8")
    manager = FileManager([workspace], tmp_path / "backup")
    scanner = ProjectScanner(manager, ignored_directories=frozenset())

    result = scanner.scan(workspace)

    assert [item.relative_path for item in result.files] == [
        "node_modules/dependency.js"
    ]


def test_scan_does_not_follow_windows_junction_escape(
    tmp_path: Path, scanner: ProjectScanner, workspace: Path
) -> None:
    outside = tmp_path / "outside"
    outside.mkdir()
    (outside / "secret.txt").write_text("secret", encoding="utf-8")
    link = workspace / "outside-link"
    if os.name == "nt":
        command = ["cmd.exe", "/d", "/c", "mklink", "/J", str(link), str(outside)]
        completed = subprocess.run(
            command, capture_output=True, text=True, timeout=10, check=False
        )
        assert completed.returncode == 0, completed.stderr or completed.stdout
    else:
        link.symlink_to(outside, target_is_directory=True)

    result = scanner.scan(workspace)

    assert result.files == ()
    assert result.skipped == (
        type(result.skipped[0])("outside-link", "link"),
    )


def test_scan_rejects_root_outside_workspace(
    tmp_path: Path, scanner: ProjectScanner
) -> None:
    outside = tmp_path / "outside"
    outside.mkdir()

    with pytest.raises(ProjectScannerError, match="workspace policy"):
        scanner.scan(outside)


def test_scan_rejects_file_as_root(scanner: ProjectScanner, workspace: Path) -> None:
    file_path = workspace / "file.txt"
    file_path.write_text("content", encoding="utf-8")

    with pytest.raises(ProjectScannerError, match="not a directory"):
        scanner.scan(file_path)


def test_max_depth_skips_deeper_directories(tmp_path: Path, workspace: Path) -> None:
    nested = workspace / "first" / "second"
    nested.mkdir(parents=True)
    (nested / "deep.txt").write_text("deep", encoding="utf-8")
    manager = FileManager([workspace], tmp_path / "backup")
    scanner = ProjectScanner(manager, limits=ScanLimits(max_depth=1))

    result = scanner.scan(workspace)

    assert result.files == ()
    assert any(item.reason == "max_depth" for item in result.skipped)
    assert not result.truncated


def test_max_files_stops_scan_with_reason(tmp_path: Path, workspace: Path) -> None:
    for name in ("a.txt", "b.txt", "c.txt"):
        (workspace / name).write_text(name, encoding="utf-8")
    manager = FileManager([workspace], tmp_path / "backup")
    scanner = ProjectScanner(manager, limits=ScanLimits(max_files=2))

    result = scanner.scan(workspace)

    assert len(result.files) == 2
    assert result.truncated
    assert result.truncation_reason == "max_files"
    assert result.skipped[-1].reason == "max_files"


def test_max_total_bytes_stops_before_exceeding_limit(
    tmp_path: Path, workspace: Path
) -> None:
    (workspace / "a.txt").write_text("1234", encoding="utf-8")
    (workspace / "b.txt").write_text("5678", encoding="utf-8")
    manager = FileManager([workspace], tmp_path / "backup")
    scanner = ProjectScanner(manager, limits=ScanLimits(max_total_bytes=6))

    result = scanner.scan(workspace)

    assert result.total_bytes == 4
    assert result.truncated
    assert result.truncation_reason == "max_total_bytes"


def test_max_directories_stops_deep_tree(tmp_path: Path, workspace: Path) -> None:
    (workspace / "a" / "nested").mkdir(parents=True)
    (workspace / "b").mkdir()
    manager = FileManager([workspace], tmp_path / "backup")
    scanner = ProjectScanner(manager, limits=ScanLimits(max_directories=2))

    result = scanner.scan(workspace)

    assert result.directories_scanned == 2
    assert result.truncated
    assert result.truncation_reason == "max_directories"


def test_max_entries_per_directory_bounds_enumeration(
    tmp_path: Path, workspace: Path
) -> None:
    for name in ("a.txt", "b.txt", "c.txt"):
        (workspace / name).write_text(name, encoding="utf-8")
    manager = FileManager([workspace], tmp_path / "backup")
    scanner = ProjectScanner(
        manager, limits=ScanLimits(max_entries_per_directory=2)
    )

    result = scanner.scan(workspace)

    assert len(result.files) == 2
    assert result.truncated
    assert result.truncation_reason == "max_entries_per_directory"


def test_permission_error_is_reported_without_failing_entire_scan(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
    workspace: Path,
) -> None:
    denied = workspace / "denied"
    denied.mkdir()
    (workspace / "visible.txt").write_text("visible", encoding="utf-8")
    real_scandir = os.scandir

    def controlled_scandir(path: str | bytes | os.PathLike[str]) -> os.ScandirIterator[str]:
        if Path(path) == denied:
            raise PermissionError("simulated denial")
        return real_scandir(path)

    monkeypatch.setattr("core.project_scanner.os.scandir", controlled_scandir)
    manager = FileManager([workspace], tmp_path / "backup")

    result = ProjectScanner(manager).scan(workspace)

    assert [item.relative_path for item in result.files] == ["visible.txt"]
    assert any(
        item.relative_path == "denied" and item.reason == "permission_denied"
        for item in result.skipped
    )
    assert not result.truncated


@pytest.mark.parametrize(
    "arguments, message",
    [
        ({"max_files": 0}, "max_files"),
        ({"max_directories": 0}, "max_directories"),
        ({"max_entries_per_directory": 0}, "max_entries_per_directory"),
        ({"max_depth": -1}, "max_depth"),
        ({"max_total_bytes": 0}, "max_total_bytes"),
        ({"max_files": True}, "max_files"),
        ({"max_files": 1.5}, "max_files"),
    ],
)
def test_scan_limits_reject_invalid_values(
    arguments: dict[str, object], message: str
) -> None:
    with pytest.raises(ValueError, match=message):
        ScanLimits(**arguments)  # type: ignore[arg-type]


def test_scan_emits_start_and_completion_audit_events(
    tmp_path: Path, workspace: Path
) -> None:
    records: list[logging.LogRecord] = []

    class CaptureHandler(logging.Handler):
        def emit(self, record: logging.LogRecord) -> None:
            records.append(record)

    logger = logging.getLogger(f"test.project_scanner.{os.getpid()}")
    logger.handlers = [CaptureHandler()]
    logger.setLevel(logging.INFO)
    logger.propagate = False
    manager = FileManager([workspace], tmp_path / "backup")
    scanner = ProjectScanner(manager, logger=logger)

    scanner.scan(workspace)

    assert [record.getMessage() for record in records] == [
        "project_scan_started",
        "project_scan_completed",
    ]
