"""Bounded, deterministic project-tree metadata scanning."""

from __future__ import annotations

import logging
import os
from dataclasses import dataclass
from itertools import islice
from pathlib import Path
from typing import Any

from agent.exceptions import FileManagerError, ProjectScannerError
from core.file_manager import FileManager


DEFAULT_IGNORED_DIRECTORIES = frozenset(
    {
        ".git",
        ".ai-engineer",
        ".hg",
        ".mypy_cache",
        ".pytest_cache",
        ".ruff_cache",
        ".svn",
        ".tox",
        ".venv",
        "__pycache__",
        "backup",
        "logs",
        "node_modules",
        "venv",
    }
)

_BINARY_SUFFIXES = frozenset(
    {
        ".7z",
        ".bmp",
        ".class",
        ".dll",
        ".doc",
        ".docx",
        ".dwg",
        ".exe",
        ".gif",
        ".ico",
        ".jpeg",
        ".jpg",
        ".pdf",
        ".png",
        ".pyc",
        ".pyd",
        ".rar",
        ".so",
        ".webp",
        ".xls",
        ".xlsx",
        ".zip",
    }
)

_TEXT_SUFFIXES = frozenset(
    {
        ".bat",
        ".c",
        ".cfg",
        ".cmd",
        ".cpp",
        ".cs",
        ".css",
        ".csv",
        ".dxf",
        ".h",
        ".html",
        ".ini",
        ".java",
        ".js",
        ".json",
        ".lsp",
        ".md",
        ".ps1",
        ".py",
        ".rst",
        ".sql",
        ".toml",
        ".ts",
        ".txt",
        ".xml",
        ".yaml",
        ".yml",
    }
)

_PROJECT_FILENAMES = frozenset(
    {
        ".editorconfig",
        ".gitignore",
        "agents.md",
        "cargo.toml",
        "dockerfile",
        "go.mod",
        "makefile",
        "package.json",
        "poetry.lock",
        "pyproject.toml",
        "readme.md",
        "requirements.txt",
        "setup.cfg",
        "setup.py",
    }
)

_PROJECT_SUFFIXES = frozenset({".csproj", ".sln", ".vcxproj"})


@dataclass(frozen=True, slots=True)
class ScanLimits:
    """Resource and traversal limits for one project scan."""

    max_files: int = 10_000
    max_directories: int = 2_000
    max_entries_per_directory: int = 10_000
    max_depth: int = 20
    max_total_bytes: int = 1_073_741_824

    def __post_init__(self) -> None:
        values = {
            "max_files": self.max_files,
            "max_directories": self.max_directories,
            "max_entries_per_directory": self.max_entries_per_directory,
            "max_total_bytes": self.max_total_bytes,
        }
        for name, value in values.items():
            if not isinstance(value, int) or isinstance(value, bool) or value <= 0:
                raise ValueError(f"{name} must be greater than zero.")
        if (
            not isinstance(self.max_depth, int)
            or isinstance(self.max_depth, bool)
            or self.max_depth < 0
        ):
            raise ValueError("max_depth must be zero or greater.")


@dataclass(frozen=True, slots=True)
class ScannedFile:
    """Metadata collected for one regular file without reading its contents."""

    relative_path: str
    size_bytes: int
    suffix: str
    category: str

    def to_dict(self) -> dict[str, str | int]:
        """Return a JSON-compatible representation."""
        return {
            "relative_path": self.relative_path,
            "size_bytes": self.size_bytes,
            "suffix": self.suffix,
            "category": self.category,
        }


@dataclass(frozen=True, slots=True)
class SkippedPath:
    """Describe a path intentionally or unavoidably omitted from a scan."""

    relative_path: str
    reason: str

    def to_dict(self) -> dict[str, str]:
        """Return a JSON-compatible representation."""
        return {"relative_path": self.relative_path, "reason": self.reason}


@dataclass(frozen=True, slots=True)
class ProjectScanResult:
    """Immutable summary of a bounded project scan."""

    root: Path
    files: tuple[ScannedFile, ...]
    skipped: tuple[SkippedPath, ...]
    directories_scanned: int
    total_bytes: int
    extension_counts: tuple[tuple[str, int], ...]
    project_files: tuple[str, ...]
    truncated: bool
    truncation_reason: str | None

    def to_dict(self) -> dict[str, Any]:
        """Return the result in a JSON-serializable structure."""
        return {
            "root": str(self.root),
            "files": [item.to_dict() for item in self.files],
            "skipped": [item.to_dict() for item in self.skipped],
            "directories_scanned": self.directories_scanned,
            "total_bytes": self.total_bytes,
            "extension_counts": dict(self.extension_counts),
            "project_files": list(self.project_files),
            "truncated": self.truncated,
            "truncation_reason": self.truncation_reason,
        }


class ProjectScanner:
    """Scan project metadata without following links or reading file contents."""

    def __init__(
        self,
        file_manager: FileManager,
        *,
        limits: ScanLimits | None = None,
        ignored_directories: frozenset[str] | None = None,
        logger: logging.Logger | None = None,
    ) -> None:
        self._file_manager = file_manager
        self.limits = limits or ScanLimits()
        ignored = (
            DEFAULT_IGNORED_DIRECTORIES
            if ignored_directories is None
            else ignored_directories
        )
        self.ignored_directories = frozenset(name.casefold() for name in ignored)
        self._logger = logger or logging.getLogger("ai_engineer.project_scanner")

    def scan(self, root: str | Path) -> ProjectScanResult:
        """Return bounded, deterministic metadata for a project directory."""
        try:
            scan_root = self._file_manager.resolve_path(root)
        except (FileManagerError, OSError) as exc:
            self._logger.warning(
                "project_scan_denied", extra={"root": str(root), "error": str(exc)}
            )
            raise ProjectScannerError(f"Project root violates workspace policy: {root}") from exc
        if not scan_root.exists():
            raise ProjectScannerError(f"Project root does not exist: {scan_root}")
        if not scan_root.is_dir():
            raise ProjectScannerError(f"Project root is not a directory: {scan_root}")

        self._logger.info("project_scan_started", extra={"root": str(scan_root)})
        files: list[ScannedFile] = []
        skipped: list[SkippedPath] = []
        extension_counts: dict[str, int] = {}
        project_files: set[str] = set()
        total_bytes = 0
        directories_scanned = 0
        truncation_reason: str | None = None
        stack: list[tuple[Path, int]] = [(scan_root, 0)]

        while stack and truncation_reason is None:
            directory, depth = stack.pop()
            if not self._directory_is_safe(directory, scan_root):
                skipped.append(self._skipped(directory, scan_root, "path_escape"))
                continue
            if directories_scanned >= self.limits.max_directories:
                skipped.append(self._skipped(directory, scan_root, "max_directories"))
                truncation_reason = "max_directories"
                break
            directories_scanned += 1

            entry_limit_hit = False
            try:
                with os.scandir(directory) as iterator:
                    entries = list(
                        islice(iterator, self.limits.max_entries_per_directory + 1)
                    )
                if len(entries) > self.limits.max_entries_per_directory:
                    entries.pop()
                    entry_limit_hit = True
                entries.sort(key=lambda item: item.name.casefold())
            except OSError as exc:
                skipped.append(self._skipped(directory, scan_root, _os_error_reason(exc)))
                self._logger.warning(
                    "project_scan_directory_skipped",
                    extra={"path": str(directory), "error": str(exc)},
                )
                continue

            child_directories: list[Path] = []
            for entry in entries:
                entry_path = Path(entry.path)
                relative_path = entry_path.relative_to(scan_root).as_posix()
                try:
                    if _is_link_or_junction(entry_path, entry):
                        skipped.append(SkippedPath(relative_path, "link"))
                        continue
                    if entry.is_dir(follow_symlinks=False):
                        if entry.name.casefold() in self.ignored_directories:
                            skipped.append(SkippedPath(relative_path, "ignored_directory"))
                        elif depth >= self.limits.max_depth:
                            skipped.append(SkippedPath(relative_path, "max_depth"))
                        else:
                            child_directories.append(entry_path)
                        continue
                    if not entry.is_file(follow_symlinks=False):
                        skipped.append(SkippedPath(relative_path, "not_regular_file"))
                        continue
                    stat_result = entry.stat(follow_symlinks=False)
                except OSError as exc:
                    skipped.append(SkippedPath(relative_path, _os_error_reason(exc)))
                    self._logger.warning(
                        "project_scan_entry_skipped",
                        extra={"path": str(entry_path), "error": str(exc)},
                    )
                    continue

                if len(files) >= self.limits.max_files:
                    skipped.append(SkippedPath(relative_path, "max_files"))
                    truncation_reason = "max_files"
                    break
                if total_bytes + stat_result.st_size > self.limits.max_total_bytes:
                    skipped.append(SkippedPath(relative_path, "max_total_bytes"))
                    truncation_reason = "max_total_bytes"
                    break

                suffix = entry_path.suffix.casefold()
                files.append(
                    ScannedFile(
                        relative_path=relative_path,
                        size_bytes=stat_result.st_size,
                        suffix=suffix,
                        category=_file_category(suffix),
                    )
                )
                total_bytes += stat_result.st_size
                extension_key = suffix or "[none]"
                extension_counts[extension_key] = extension_counts.get(extension_key, 0) + 1
                if _is_project_file(entry.name, suffix):
                    project_files.add(relative_path)

            stack.extend((child, depth + 1) for child in reversed(child_directories))
            if entry_limit_hit and truncation_reason is None:
                skipped.append(
                    self._skipped(directory, scan_root, "max_entries_per_directory")
                )
                truncation_reason = "max_entries_per_directory"

        result = ProjectScanResult(
            root=scan_root,
            files=tuple(sorted(files, key=lambda item: item.relative_path.casefold())),
            skipped=tuple(
                sorted(skipped, key=lambda item: (item.relative_path.casefold(), item.reason))
            ),
            directories_scanned=directories_scanned,
            total_bytes=total_bytes,
            extension_counts=tuple(sorted(extension_counts.items())),
            project_files=tuple(sorted(project_files, key=str.casefold)),
            truncated=truncation_reason is not None,
            truncation_reason=truncation_reason,
        )
        self._logger.info(
            "project_scan_completed",
            extra={
                "root": str(scan_root),
                "files": len(result.files),
                "directories": result.directories_scanned,
                "total_bytes": result.total_bytes,
                "skipped": len(result.skipped),
                "truncated": result.truncated,
                "truncation_reason": result.truncation_reason,
            },
        )
        return result

    def _directory_is_safe(self, directory: Path, scan_root: Path) -> bool:
        try:
            resolved = self._file_manager.resolve_path(directory)
        except (FileManagerError, OSError):
            return False
        return resolved == scan_root or scan_root in resolved.parents

    @staticmethod
    def _skipped(path: Path, root: Path, reason: str) -> SkippedPath:
        try:
            relative_path = path.relative_to(root).as_posix()
        except ValueError:
            relative_path = str(path)
        return SkippedPath(relative_path or ".", reason)


def _is_link_or_junction(path: Path, entry: os.DirEntry[str]) -> bool:
    if entry.is_symlink():
        return True
    is_junction = getattr(path, "is_junction", None)
    return bool(is_junction is not None and is_junction())


def _file_category(suffix: str) -> str:
    if suffix in _BINARY_SUFFIXES:
        return "binary"
    if suffix in _TEXT_SUFFIXES:
        return "text"
    return "unknown"


def _is_project_file(name: str, suffix: str) -> bool:
    return name.casefold() in _PROJECT_FILENAMES or suffix in _PROJECT_SUFFIXES


def _os_error_reason(error: OSError) -> str:
    if isinstance(error, PermissionError):
        return "permission_denied"
    return "os_error"
