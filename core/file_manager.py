"""Sandboxed UTF-8 text file operations with backups and atomic writes."""

from __future__ import annotations

import logging
import os
import shutil
import tempfile
import uuid
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path, PureWindowsPath
from typing import Callable, Sequence

from agent.exceptions import (
    ConfirmationRequiredError,
    FileManagerError,
    FileSizeLimitError,
    PathPolicyError,
)


@dataclass(frozen=True, slots=True)
class FileOperationResult:
    """Describe a completed mutating file operation."""

    action: str
    path: Path
    backup_path: Path | None = None
    bytes_written: int = 0


class FileManager:
    """Perform bounded text-file operations inside configured workspace roots.

    Relative paths are accepted only when exactly one workspace root is configured.
    Overwriting and deleting require an explicit confirmation flag and create a
    timestamped backup before changing the original file.
    """

    def __init__(
        self,
        workspace_roots: Sequence[Path],
        backup_root: Path,
        *,
        max_read_bytes: int = 1_048_576,
        logger: logging.Logger | None = None,
        clock: Callable[[], datetime] | None = None,
    ) -> None:
        if not workspace_roots:
            raise PathPolicyError("At least one workspace root must be configured.")
        if max_read_bytes <= 0:
            raise ValueError("max_read_bytes must be greater than zero.")

        roots = tuple(_canonicalize_root(root) for root in workspace_roots)
        if len(set(roots)) != len(roots):
            raise PathPolicyError("Workspace roots must be unique after resolution.")
        self._roots = roots
        self.backup_root = backup_root.expanduser().resolve(strict=False)
        if any(
            _is_within(self.backup_root, root) or _is_within(root, self.backup_root)
            for root in self._roots
        ):
            raise PathPolicyError(
                "The backup root and workspace roots must not contain one another."
            )

        self.backup_root.mkdir(parents=True, exist_ok=True)
        self.max_read_bytes = max_read_bytes
        self._logger = logger or logging.getLogger("ai_engineer.file_manager")
        self._clock = clock or (lambda: datetime.now(UTC))

    @property
    def workspace_roots(self) -> tuple[Path, ...]:
        """Return the canonical configured workspace roots."""
        return self._roots

    def resolve_path(self, path: str | Path) -> Path:
        """Resolve *path* and enforce canonical workspace containment."""
        requested = Path(path).expanduser()
        raw_value = str(path)
        foreign_windows_absolute = os.name != "nt" and PureWindowsPath(raw_value).is_absolute()
        if foreign_windows_absolute:
            self._logger.warning(
                "path_access_denied",
                extra={"requested_path": raw_value, "reason": "foreign_absolute_path"},
            )
            raise PathPolicyError(f"Path is outside configured workspace roots: {path}")
        if requested.is_absolute():
            candidate = requested.resolve(strict=False)
        else:
            if len(self._roots) != 1:
                raise PathPolicyError(
                    "Relative paths require exactly one configured workspace root."
                )
            candidate = (self._roots[0] / requested).resolve(strict=False)

        if not any(_is_within(candidate, root) for root in self._roots):
            self._logger.warning(
                "path_access_denied",
                extra={"requested_path": str(path), "resolved_path": str(candidate)},
            )
            raise PathPolicyError(f"Path is outside configured workspace roots: {path}")
        return candidate

    def read_text(
        self, path: str | Path, *, maximum_bytes: int | None = None
    ) -> str:
        """Read bounded UTF-8 text, optionally with a call-specific limit up to 2 MiB."""
        limit = self.max_read_bytes if maximum_bytes is None else maximum_bytes
        if (
            not isinstance(limit, int) or isinstance(limit, bool)
            or not 1 <= limit <= 2_097_152
        ):
            raise ValueError("maximum_bytes must be from 1 through 2,097,152.")
        target = self.resolve_path(path)
        self._require_regular_file(target)
        try:
            size = target.stat().st_size
            if size > limit:
                self._logger.warning(
                    "file_read_limit_exceeded",
                    extra={"path": str(target), "bytes": size, "limit": limit},
                )
                raise FileSizeLimitError(
                    f"File size {size} exceeds the {limit}-byte read limit: {target}"
                )
            content = target.read_text(encoding="utf-8")
        except FileSizeLimitError:
            raise
        except UnicodeDecodeError as exc:
            self._logger.error("file_read_failed", extra={"path": str(target), "error": str(exc)})
            raise FileManagerError(f"File is not valid UTF-8 text: {target}") from exc
        except OSError as exc:
            self._logger.error("file_read_failed", extra={"path": str(target), "error": str(exc)})
            raise FileManagerError(f"Cannot read file {target}: {exc}") from exc

        if len(content.encode("utf-8")) > limit:
            raise FileSizeLimitError(f"File exceeded the read limit while being read: {target}")
        self._logger.info("file_read", extra={"path": str(target), "bytes": size})
        return content

    def read_bytes(
        self, path: str | Path, *, maximum_bytes: int | None = None
    ) -> bytes:
        """Read exact bounded bytes inside the workspace without newline normalization."""
        limit = self.max_read_bytes if maximum_bytes is None else maximum_bytes
        if (
            not isinstance(limit, int) or isinstance(limit, bool)
            or not 1 <= limit <= 2_097_152
        ):
            raise ValueError("maximum_bytes must be from 1 through 2,097,152.")
        target = self.resolve_path(path)
        self._require_regular_file(target)
        try:
            size = target.stat().st_size
            if size > limit:
                raise FileSizeLimitError(
                    f"File size {size} exceeds the {limit}-byte read limit: {target}"
                )
            content = target.read_bytes()
        except FileSizeLimitError:
            raise
        except OSError as exc:
            self._logger.error("file_read_failed", extra={"path": str(target), "error": str(exc)})
            raise FileManagerError(f"Cannot read file {target}: {exc}") from exc
        if len(content) > limit:
            raise FileSizeLimitError(f"File exceeded the read limit while being read: {target}")
        self._logger.info("file_read", extra={"path": str(target), "bytes": size})
        return content

    def write_text(
        self,
        path: str | Path,
        content: str,
        *,
        confirm_overwrite: bool = False,
        create_parents: bool = False,
    ) -> FileOperationResult:
        """Atomically create or overwrite a UTF-8 text file.

        Existing files require ``confirm_overwrite=True`` and are backed up first.
        Parent directories are created only when ``create_parents=True``.
        """
        if not isinstance(content, str):
            raise TypeError("content must be a string.")
        target = self.resolve_path(path)
        self._reject_workspace_root(target)

        if target.exists() and not target.is_file():
            raise FileManagerError(f"Target is not a regular file: {target}")
        if target.exists() and not confirm_overwrite:
            self._logger.warning(
                "file_overwrite_confirmation_required", extra={"path": str(target)}
            )
            raise ConfirmationRequiredError(
                f"Explicit confirmation is required to overwrite: {target}"
            )

        if not target.parent.exists():
            if not create_parents:
                raise FileManagerError(f"Parent directory does not exist: {target.parent}")
            try:
                target.parent.mkdir(parents=True)
            except OSError as exc:
                self._logger.error(
                    "parent_directory_creation_failed",
                    extra={"path": str(target.parent), "error": str(exc)},
                )
                raise FileManagerError(
                    f"Cannot create parent directory {target.parent}: {exc}"
                ) from exc

        validated_target = self.resolve_path(target)
        if validated_target != target:
            raise PathPolicyError(f"Target path changed during validation: {target}")

        backup_path = self._create_backup(target) if target.exists() else None
        data = content.encode("utf-8")
        temporary_path: Path | None = None
        try:
            descriptor, name = tempfile.mkstemp(
                dir=target.parent, prefix=f".{target.name}.", suffix=".tmp"
            )
            temporary_path = Path(name)
            with os.fdopen(descriptor, "wb") as handle:
                handle.write(data)
                handle.flush()
                os.fsync(handle.fileno())
            if self.resolve_path(target) != target:
                raise PathPolicyError(f"Target path changed before replacement: {target}")
            os.replace(temporary_path, target)
        except (OSError, PathPolicyError) as exc:
            if isinstance(exc, PathPolicyError):
                raise
            self._logger.error(
                "file_write_failed", extra={"path": str(target), "error": str(exc)}
            )
            raise FileManagerError(f"Cannot atomically write file {target}: {exc}") from exc
        finally:
            if temporary_path is not None and temporary_path.exists():
                try:
                    temporary_path.unlink()
                except OSError as cleanup_error:
                    self._logger.error(
                        "temporary_file_cleanup_failed",
                        extra={"path": str(temporary_path), "error": str(cleanup_error)},
                    )

        action = "overwritten" if backup_path is not None else "created"
        self._logger.info(
            "file_written",
            extra={
                "path": str(target),
                "action": action,
                "bytes": len(data),
                "backup_path": str(backup_path) if backup_path else None,
            },
        )
        return FileOperationResult(action, target, backup_path, len(data))

    def delete_file(self, path: str | Path, *, confirmed: bool = False) -> FileOperationResult:
        """Back up and delete a regular file after explicit confirmation."""
        target = self.resolve_path(path)
        self._reject_workspace_root(target)
        self._require_regular_file(target)
        if not confirmed:
            self._logger.warning(
                "file_delete_confirmation_required", extra={"path": str(target)}
            )
            raise ConfirmationRequiredError(
                f"Explicit confirmation is required to delete: {target}"
            )

        backup_path = self._create_backup(target)
        try:
            if self.resolve_path(target) != target:
                raise PathPolicyError(f"Target path changed before deletion: {target}")
            target.unlink()
        except PathPolicyError:
            raise
        except OSError as exc:
            self._logger.error(
                "file_delete_failed", extra={"path": str(target), "error": str(exc)}
            )
            raise FileManagerError(f"Cannot delete file {target}: {exc}") from exc
        self._logger.info(
            "file_deleted",
            extra={"path": str(target), "backup_path": str(backup_path)},
        )
        return FileOperationResult("deleted", target, backup_path)

    def _create_backup(self, target: Path) -> Path:
        root_index, relative_path = self._locate_root(target)
        timestamp = self._timestamp_for_path()
        backup_path = (
            self.backup_root
            / timestamp
            / f"workspace-{root_index + 1}"
            / relative_path
        )
        backup_path.parent.mkdir(parents=True, exist_ok=True)
        if backup_path.exists():
            backup_path = backup_path.with_name(
                f"{backup_path.name}.{uuid.uuid4().hex}"
            )
        temporary_path: Path | None = None
        try:
            descriptor, name = tempfile.mkstemp(
                dir=backup_path.parent,
                prefix=f".{backup_path.name}.",
                suffix=".tmp",
            )
            os.close(descriptor)
            temporary_path = Path(name)
            shutil.copy2(target, temporary_path)
            os.replace(temporary_path, backup_path)
        except OSError as exc:
            self._logger.error(
                "file_backup_failed",
                extra={
                    "path": str(target),
                    "backup_path": str(backup_path),
                    "error": str(exc),
                },
            )
            raise FileManagerError(f"Cannot back up {target} to {backup_path}: {exc}") from exc
        finally:
            if temporary_path is not None and temporary_path.exists():
                try:
                    temporary_path.unlink()
                except OSError as cleanup_error:
                    self._logger.error(
                        "backup_temporary_file_cleanup_failed",
                        extra={"path": str(temporary_path), "error": str(cleanup_error)},
                    )
        self._logger.info(
            "file_backed_up",
            extra={"path": str(target), "backup_path": str(backup_path)},
        )
        return backup_path

    def _locate_root(self, target: Path) -> tuple[int, Path]:
        for index, root in enumerate(self._roots):
            if _is_within(target, root):
                return index, target.relative_to(root)
        raise PathPolicyError(f"Path is outside configured workspace roots: {target}")

    def _require_regular_file(self, target: Path) -> None:
        if not target.exists():
            self._logger.warning("file_not_found", extra={"path": str(target)})
            raise FileManagerError(f"File does not exist: {target}")
        if not target.is_file():
            self._logger.warning("file_type_denied", extra={"path": str(target)})
            raise FileManagerError(f"Path is not a regular file: {target}")

    def _reject_workspace_root(self, target: Path) -> None:
        if target in self._roots:
            raise PathPolicyError(f"Operation cannot target a workspace root: {target}")

    def _timestamp_for_path(self) -> str:
        now = self._clock()
        if now.tzinfo is None:
            raise FileManagerError("Backup clock must return a timezone-aware datetime.")
        return now.astimezone(UTC).strftime("%Y%m%dT%H%M%S.%fZ")


def _canonicalize_root(root: Path) -> Path:
    candidate = root.expanduser()
    try:
        candidate.mkdir(parents=True, exist_ok=True)
        resolved = candidate.resolve(strict=True)
    except OSError as exc:
        raise PathPolicyError(f"Cannot prepare workspace root {root}: {exc}") from exc
    if not resolved.is_dir():
        raise PathPolicyError(f"Workspace root is not a directory: {resolved}")
    return resolved


def _is_within(candidate: Path, root: Path) -> bool:
    return candidate == root or root in candidate.parents
