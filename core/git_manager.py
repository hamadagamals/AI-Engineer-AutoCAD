"""Read-only Git inspection constrained to a configured workspace."""

from __future__ import annotations

import logging
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any

from agent.exceptions import (
    CommandDeniedError,
    FileManagerError,
    GitManagerError,
    TerminalError,
)
from core.file_manager import FileManager
from core.terminal_manager import CommandPolicy, CommandResult, TerminalManager


@dataclass(frozen=True, slots=True)
class GitStatusEntry:
    """One porcelain status entry."""

    index_status: str
    worktree_status: str
    path: str


@dataclass(frozen=True, slots=True)
class GitStatus:
    """Current branch and bounded working-tree status."""

    repository_root: Path
    branch: str | None
    entries: tuple[GitStatusEntry, ...]

    @property
    def clean(self) -> bool:
        """Return whether Git reported no changes."""
        return not self.entries


@dataclass(frozen=True, slots=True)
class GitCommit:
    """Non-sensitive commit metadata."""

    commit_hash: str
    author_name: str
    committed_at: str
    subject: str

    def to_dict(self) -> dict[str, Any]:
        """Return a JSON-compatible representation."""
        return asdict(self)


class GitManager:
    """Inspect a Git working tree without performing mutations or invoking hooks."""

    def __init__(
        self,
        file_manager: FileManager,
        repository_path: str | Path,
        *,
        timeout_seconds: float = 15.0,
        max_output_bytes: int = 2_097_152,
        logger: logging.Logger | None = None,
    ) -> None:
        self._file_manager = file_manager
        self._logger = logger or logging.getLogger("ai_engineer.git_manager")
        try:
            self.repository_path = file_manager.resolve_path(repository_path)
        except (FileManagerError, OSError) as exc:
            raise GitManagerError(
                f"Repository path violates workspace policy: {repository_path}"
            ) from exc
        if not self.repository_path.is_dir():
            raise GitManagerError(f"Repository path is not a directory: {self.repository_path}")

        policy = CommandPolicy(
            (
                ("git", "rev-parse", "--show-toplevel"),
                ("git", "rev-parse", "--absolute-git-dir"),
                ("git", "branch", "--show-current"),
                ("git", "status", "--porcelain=v1"),
                ("git", "diff", "--no-ext-diff", "--no-textconv"),
                ("git", "log"),
            )
        )
        self._terminal = TerminalManager(
            file_manager,
            policy,
            default_timeout_seconds=timeout_seconds,
            maximum_timeout_seconds=max(timeout_seconds, 15.0),
            max_output_bytes=max_output_bytes,
            logger=self._logger,
        )
        self.repository_root, self.git_directory = self._validate_repository()

    def status(self) -> GitStatus:
        """Return branch and porcelain status entries."""
        branch_result = self._run(["git", "branch", "--show-current"])
        status_result = self._run(["git", "status", "--porcelain=v1", "--untracked-files=all"])
        entries = tuple(_parse_status_line(line) for line in status_result.stdout.splitlines())
        branch = branch_result.stdout.strip() or None
        self._logger.info(
            "git_status_read",
            extra={"repository": str(self.repository_root), "entries": len(entries)},
        )
        return GitStatus(self.repository_root, branch, entries)

    def diff(self, *, cached: bool = False, path: str | Path | None = None) -> str:
        """Return a bounded patch with external diff and text conversion disabled."""
        command = ["git", "diff", "--no-ext-diff", "--no-textconv"]
        if cached:
            command.append("--cached")
        command.append("--")
        if path is not None:
            candidate = Path(path)
            if candidate.is_absolute():
                try:
                    resolved = self._file_manager.resolve_path(candidate)
                except (FileManagerError, OSError) as exc:
                    raise GitManagerError(f"Diff path violates workspace policy: {path}") from exc
                try:
                    candidate = resolved.relative_to(self.repository_root)
                except ValueError as exc:
                    raise GitManagerError(f"Diff path is outside repository root: {path}") from exc
            if ".." in candidate.parts:
                raise GitManagerError(f"Parent traversal is denied in diff path: {path}")
            command.append(candidate.as_posix())
        result = self._run(command)
        self._logger.info(
            "git_diff_read",
            extra={"repository": str(self.repository_root), "cached": cached},
        )
        return result.stdout

    def recent_commits(self, *, limit: int = 10) -> tuple[GitCommit, ...]:
        """Return bounded commit metadata without email addresses or commit bodies."""
        if not isinstance(limit, int) or isinstance(limit, bool) or not 1 <= limit <= 100:
            raise GitManagerError("Commit limit must be an integer from 1 through 100.")
        record_format = "%H%x1f%an%x1f%aI%x1f%s%x1e"
        result = self._run(
            ["git", "log", f"-n{limit}", f"--pretty=format:{record_format}", "--no-decorate"]
        )
        commits: list[GitCommit] = []
        for record in result.stdout.split("\x1e"):
            record = record.strip("\r\n")
            if not record:
                continue
            fields = record.split("\x1f")
            if len(fields) != 4:
                raise GitManagerError("Git returned malformed commit metadata.")
            commits.append(GitCommit(*fields))
        self._logger.info(
            "git_log_read",
            extra={"repository": str(self.repository_root), "commits": len(commits)},
        )
        return tuple(commits)

    def _validate_repository(self) -> tuple[Path, Path]:
        try:
            root_result = self._run(["git", "rev-parse", "--show-toplevel"])
            git_dir_result = self._run(["git", "rev-parse", "--absolute-git-dir"])
            repository_root = self._file_manager.resolve_path(root_result.stdout.strip())
            git_directory = self._file_manager.resolve_path(git_dir_result.stdout.strip())
        except (CommandDeniedError, FileManagerError, GitManagerError, TerminalError, ValueError) as exc:
            raise GitManagerError(
                f"Path is not a safe Git working tree: {self.repository_path}"
            ) from exc
        if not repository_root.is_dir() or not git_directory.is_dir():
            raise GitManagerError("Git working tree or metadata directory does not exist.")
        return repository_root, git_directory

    def _run(self, command: list[str]) -> CommandResult:
        try:
            result = self._terminal.execute(
                command,
                cwd=self.repository_path,
                environment={"GIT_OPTIONAL_LOCKS": "0"},
            )
        except (CommandDeniedError, TerminalError) as exc:
            raise GitManagerError(f"Git inspection command failed to start: {command[1]}") from exc
        if result.timed_out:
            raise GitManagerError(f"Git inspection command timed out: {command[1]}")
        if result.stdout_truncated or result.stderr_truncated:
            raise GitManagerError(f"Git inspection output exceeded its limit: {command[1]}")
        if result.return_code != 0:
            message = result.stderr.strip() or "unknown Git error"
            raise GitManagerError(f"Git inspection failed ({command[1]}): {message}")
        return result


def _parse_status_line(line: str) -> GitStatusEntry:
    if len(line) < 3:
        raise GitManagerError("Git returned a malformed status entry.")
    return GitStatusEntry(line[0], line[1], line[3:])
