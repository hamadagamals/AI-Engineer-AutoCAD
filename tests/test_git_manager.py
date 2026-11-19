"""Tests for read-only, workspace-confined Git inspection."""

from __future__ import annotations

import subprocess
from pathlib import Path

import pytest

from agent.exceptions import GitManagerError
from core.file_manager import FileManager
from core.git_manager import GitManager


def run_git(repository: Path, *arguments: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        ["git", *arguments],
        cwd=repository,
        text=True,
        capture_output=True,
        timeout=10,
        check=True,
    )


@pytest.fixture
def repository(tmp_path: Path) -> Path:
    root = tmp_path / "workspace" / "project"
    root.mkdir(parents=True)
    run_git(root, "init")
    run_git(root, "config", "user.name", "Test User")
    run_git(root, "config", "user.email", "test@example.invalid")
    (root / "tracked.txt").write_text("original\n", encoding="utf-8")
    run_git(root, "add", "tracked.txt")
    run_git(root, "commit", "-m", "Initial commit")
    return root


def make_manager(tmp_path: Path, repository: Path, **kwargs: object) -> GitManager:
    workspace = tmp_path / "workspace"
    file_manager = FileManager([workspace], tmp_path / "backup")
    return GitManager(file_manager, repository, **kwargs)


def test_status_reports_branch_and_changes(tmp_path: Path, repository: Path) -> None:
    manager = make_manager(tmp_path, repository)
    (repository / "tracked.txt").write_text("changed\n", encoding="utf-8")
    (repository / "new.txt").write_text("new\n", encoding="utf-8")

    status = manager.status()

    assert status.branch in {"master", "main"}
    assert not status.clean
    assert {entry.path for entry in status.entries} == {"new.txt", "tracked.txt"}


def test_diff_disables_external_tools_and_can_filter_path(
    tmp_path: Path, repository: Path
) -> None:
    manager = make_manager(tmp_path, repository)
    (repository / "tracked.txt").write_text("changed\n", encoding="utf-8")

    patch = manager.diff(path="tracked.txt")

    assert "-original" in patch
    assert "+changed" in patch


def test_diff_rejects_path_outside_repository(tmp_path: Path, repository: Path) -> None:
    manager = make_manager(tmp_path, repository)
    outside_repository = tmp_path / "workspace" / "other.txt"
    outside_repository.write_text("outside", encoding="utf-8")

    with pytest.raises(GitManagerError, match="outside repository"):
        manager.diff(path=outside_repository)


def test_recent_commits_omit_email_and_body(tmp_path: Path, repository: Path) -> None:
    manager = make_manager(tmp_path, repository)

    commits = manager.recent_commits(limit=1)

    assert len(commits) == 1
    assert commits[0].subject == "Initial commit"
    assert commits[0].author_name == "Test User"
    assert "@" not in str(commits[0].to_dict())


@pytest.mark.parametrize("limit", [0, 101, True])
def test_recent_commits_reject_invalid_limit(
    tmp_path: Path, repository: Path, limit: int
) -> None:
    manager = make_manager(tmp_path, repository)

    with pytest.raises(GitManagerError, match="Commit limit"):
        manager.recent_commits(limit=limit)


def test_non_repository_is_rejected(tmp_path: Path) -> None:
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    file_manager = FileManager([workspace], tmp_path / "backup")

    with pytest.raises(GitManagerError, match="safe Git working tree"):
        GitManager(file_manager, workspace)


def test_repository_outside_workspace_is_rejected(tmp_path: Path) -> None:
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    outside = tmp_path / "outside"
    outside.mkdir()
    file_manager = FileManager([workspace], tmp_path / "backup")

    with pytest.raises(GitManagerError, match="workspace policy"):
        GitManager(file_manager, outside)


def test_output_limit_fails_closed(tmp_path: Path, repository: Path) -> None:
    manager = make_manager(tmp_path, repository, max_output_bytes=256)
    for index in range(20):
        (repository / f"untracked-{index:02d}.txt").write_text("content", encoding="utf-8")

    with pytest.raises(GitManagerError, match="output exceeded"):
        manager.status()
