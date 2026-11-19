"""Fail-closed subprocess execution with explicit command policies."""

from __future__ import annotations

import logging
import math
import os
import subprocess
import threading
import time
from dataclasses import dataclass
from pathlib import Path, PurePath
from typing import Mapping, Sequence

import psutil

from agent.exceptions import (
    CommandDeniedError,
    ConfirmationRequiredError,
    FileManagerError,
    TerminalError,
)
from core.file_manager import FileManager


_DENIED_EXECUTABLES = frozenset(
    {"bash", "cmd", "cmd.exe", "powershell", "powershell.exe", "pwsh", "sh", "wsl"}
)
_DENIED_GIT_ACTIONS = frozenset(
    {"clean", "filter-branch", "push", "rebase", "reset", "restore"}
)
_SAFE_ENVIRONMENT_KEYS = frozenset(
    {
        "COMSPEC",
        "GIT_OPTIONAL_LOCKS",
        "HOMEDRIVE",
        "HOMEPATH",
        "LANG",
        "PATH",
        "PATHEXT",
        "SYSTEMDRIVE",
        "SYSTEMROOT",
        "TEMP",
        "TMP",
        "USERNAME",
        "USERPROFILE",
        "WINDIR",
    }
)


@dataclass(frozen=True, slots=True)
class CommandPolicy:
    """Explicit command prefixes and optional confirmation-gated prefixes."""

    allowed_prefixes: tuple[tuple[str, ...], ...]
    confirmation_prefixes: tuple[tuple[str, ...], ...] = ()

    def __post_init__(self) -> None:
        if not self.allowed_prefixes:
            raise ValueError("At least one allowed command prefix is required.")
        for prefix in self.allowed_prefixes + self.confirmation_prefixes:
            if not prefix or any(not isinstance(part, str) or not part for part in prefix):
                raise ValueError("Command prefixes must contain non-empty strings.")
            if _executable_name(prefix[0]) in _DENIED_EXECUTABLES:
                raise ValueError(f"Shell executable cannot be allowed: {prefix[0]}")

    def authorize(self, command: Sequence[str], *, confirmed: bool) -> None:
        """Raise an actionable exception unless *command* is explicitly allowed."""
        normalized = _normalize_command(command)
        executable = normalized[0]
        if executable in _DENIED_EXECUTABLES:
            raise CommandDeniedError(f"Shell executable is permanently denied: {command[0]}")
        if executable == "git" and len(normalized) > 1 and normalized[1] in _DENIED_GIT_ACTIONS:
            raise CommandDeniedError(f"Dangerous Git action is denied: {command[1]}")
        if executable == "python" and len(normalized) > 1 and normalized[1] in {"-", "-c"}:
            raise CommandDeniedError("Inline Python execution is denied by terminal policy.")

        allowed = tuple(_normalize_command(prefix) for prefix in self.allowed_prefixes)
        if not any(normalized[: len(prefix)] == prefix for prefix in allowed):
            raise CommandDeniedError("Command does not match an allowed policy prefix.")

        gated = tuple(_normalize_command(prefix) for prefix in self.confirmation_prefixes)
        if any(normalized[: len(prefix)] == prefix for prefix in gated) and not confirmed:
            raise ConfirmationRequiredError(
                "Explicit confirmation is required for this command policy prefix."
            )


@dataclass(frozen=True, slots=True)
class CommandResult:
    """Captured result of a bounded command execution."""

    command: tuple[str, ...]
    cwd: Path
    return_code: int
    stdout: str
    stderr: str
    duration_seconds: float
    timed_out: bool
    stdout_truncated: bool
    stderr_truncated: bool

    @property
    def succeeded(self) -> bool:
        """Return whether the process completed successfully within its timeout."""
        return not self.timed_out and self.return_code == 0


class TerminalManager:
    """Run approved argument-vector commands without invoking a shell."""

    def __init__(
        self,
        file_manager: FileManager,
        policy: CommandPolicy,
        *,
        default_timeout_seconds: float = 30.0,
        maximum_timeout_seconds: float = 300.0,
        max_output_bytes: int = 1_048_576,
        logger: logging.Logger | None = None,
    ) -> None:
        if not _is_positive_number(default_timeout_seconds) or not _is_positive_number(
            maximum_timeout_seconds
        ):
            raise ValueError("Command timeouts must be greater than zero.")
        if default_timeout_seconds > maximum_timeout_seconds:
            raise ValueError("Default timeout cannot exceed maximum timeout.")
        if (
            not isinstance(max_output_bytes, int)
            or isinstance(max_output_bytes, bool)
            or max_output_bytes <= 0
        ):
            raise ValueError("max_output_bytes must be greater than zero.")
        self._file_manager = file_manager
        self.policy = policy
        self.default_timeout_seconds = default_timeout_seconds
        self.maximum_timeout_seconds = maximum_timeout_seconds
        self.max_output_bytes = max_output_bytes
        self._logger = logger or logging.getLogger("ai_engineer.terminal_manager")

    def execute(
        self,
        command: Sequence[str],
        *,
        cwd: str | Path,
        timeout_seconds: float | None = None,
        environment: Mapping[str, str] | None = None,
        confirmed: bool = False,
    ) -> CommandResult:
        """Execute an approved command with bounded time, environment, and output."""
        argv = _validate_command(command)
        try:
            working_directory = self._file_manager.resolve_path(cwd)
        except FileManagerError as exc:
            self._logger.warning("command_cwd_denied", extra={"cwd": str(cwd)})
            raise CommandDeniedError(f"Working directory violates workspace policy: {cwd}") from exc
        if not working_directory.is_dir():
            raise TerminalError(f"Working directory is not a directory: {working_directory}")

        self.policy.authorize(argv, confirmed=confirmed)
        _validate_path_arguments(argv[1:], self._file_manager)
        timeout = self.default_timeout_seconds if timeout_seconds is None else timeout_seconds
        if not _is_positive_number(timeout) or timeout > self.maximum_timeout_seconds:
            raise CommandDeniedError(
                f"Timeout must be greater than zero and at most {self.maximum_timeout_seconds} seconds."
            )
        process_environment = _build_environment(environment)

        self._logger.info(
            "command_started",
            extra={
                "executable": _executable_name(argv[0]),
                "argument_count": len(argv) - 1,
                "cwd": str(working_directory),
                "timeout_seconds": timeout,
            },
        )
        started = time.monotonic()
        creation_flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
        try:
            process = subprocess.Popen(
                argv,
                cwd=working_directory,
                env=process_environment,
                stdin=subprocess.DEVNULL,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                shell=False,
                creationflags=creation_flags,
                start_new_session=os.name != "nt",
            )
        except OSError as exc:
            self._logger.error(
                "command_start_failed",
                extra={"executable": _executable_name(argv[0]), "error": str(exc)},
            )
            raise TerminalError(f"Cannot start approved command {argv[0]}: {exc}") from exc

        stdout_capture = _BoundedCapture(self.max_output_bytes)
        stderr_capture = _BoundedCapture(self.max_output_bytes)
        stdout_thread = threading.Thread(
            target=stdout_capture.drain, args=(process.stdout,), daemon=True
        )
        stderr_thread = threading.Thread(
            target=stderr_capture.drain, args=(process.stderr,), daemon=True
        )
        stdout_thread.start()
        stderr_thread.start()
        timed_out = False
        try:
            return_code = process.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            timed_out = True
            _terminate_process_tree(process.pid, self._logger)
            try:
                return_code = process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                return_code = process.wait(timeout=10)
        stdout_thread.join(timeout=10)
        stderr_thread.join(timeout=10)
        duration = time.monotonic() - started
        result = CommandResult(
            command=argv,
            cwd=working_directory,
            return_code=return_code,
            stdout=stdout_capture.text,
            stderr=stderr_capture.text,
            duration_seconds=duration,
            timed_out=timed_out,
            stdout_truncated=stdout_capture.truncated,
            stderr_truncated=stderr_capture.truncated,
        )
        self._logger.info(
            "command_completed",
            extra={
                "executable": _executable_name(argv[0]),
                "return_code": return_code,
                "duration_seconds": round(duration, 6),
                "timed_out": timed_out,
                "stdout_truncated": result.stdout_truncated,
                "stderr_truncated": result.stderr_truncated,
            },
        )
        return result


class _BoundedCapture:
    def __init__(self, limit: int) -> None:
        self._limit = limit
        self._data = bytearray()
        self.truncated = False

    def drain(self, stream: object) -> None:
        reader = getattr(stream, "read", None)
        if reader is None:
            return
        while True:
            chunk = reader(8192)
            if not chunk:
                break
            remaining = self._limit - len(self._data)
            if remaining > 0:
                self._data.extend(chunk[:remaining])
            if len(chunk) > remaining:
                self.truncated = True

    @property
    def text(self) -> str:
        return bytes(self._data).decode("utf-8", errors="replace")


def _validate_command(command: Sequence[str]) -> tuple[str, ...]:
    if isinstance(command, (str, bytes)) or not command:
        raise CommandDeniedError("Command must be a non-empty sequence of arguments.")
    if len(command) > 128:
        raise CommandDeniedError("Command exceeds the 128-argument limit.")
    argv = tuple(command)
    if any(not isinstance(part, str) or not part or "\x00" in part for part in argv):
        raise CommandDeniedError("Every command argument must be a non-empty string without NUL bytes.")
    if sum(len(part) for part in argv) > 32_768:
        raise CommandDeniedError("Command exceeds the 32,768-character limit.")
    return argv


def _normalize_command(command: Sequence[str]) -> tuple[str, ...]:
    return (_executable_name(command[0]), *(part.casefold() for part in command[1:]))


def _executable_name(value: str) -> str:
    name = Path(value).name.casefold()
    return name[:-4] if name.endswith(".exe") else name


def _validate_path_arguments(arguments: Sequence[str], file_manager: FileManager) -> None:
    for argument in arguments:
        candidate_value = argument.split("=", 1)[-1] if "=" in argument else argument
        candidate = PurePath(candidate_value)
        if ".." in candidate.parts:
            raise CommandDeniedError(f"Parent traversal is denied in command argument: {argument}")
        if Path(candidate_value).is_absolute():
            try:
                file_manager.resolve_path(candidate_value)
            except FileManagerError as exc:
                raise CommandDeniedError(
                    f"Absolute command argument is outside workspace roots: {argument}"
                ) from exc


def _build_environment(overrides: Mapping[str, str] | None) -> dict[str, str]:
    environment = {
        key.upper(): value
        for key, value in os.environ.items()
        if key.upper() in _SAFE_ENVIRONMENT_KEYS
    }
    if overrides:
        for key, value in overrides.items():
            if not isinstance(key, str):
                raise CommandDeniedError("Environment variable names must be strings.")
            if key.upper() not in _SAFE_ENVIRONMENT_KEYS:
                raise CommandDeniedError(f"Environment variable is not allowed: {key}")
            if not isinstance(value, str) or "\x00" in value:
                raise CommandDeniedError(f"Invalid environment value for: {key}")
            normalized_key = key.upper()
            if normalized_key == "GIT_OPTIONAL_LOCKS" and value != "0":
                raise CommandDeniedError("GIT_OPTIONAL_LOCKS may only be set to 0.")
            environment[normalized_key] = value
    return environment


def _is_positive_number(value: object) -> bool:
    return (
        isinstance(value, (int, float))
        and not isinstance(value, bool)
        and math.isfinite(value)
        and value > 0
    )


def _terminate_process_tree(process_id: int, logger: logging.Logger) -> None:
    try:
        parent = psutil.Process(process_id)
        processes = parent.children(recursive=True)
        processes.append(parent)
        for process in processes:
            try:
                process.terminate()
            except psutil.NoSuchProcess:
                continue
        _, alive = psutil.wait_procs(processes, timeout=3)
        for process in alive:
            try:
                process.kill()
            except psutil.NoSuchProcess:
                continue
    except psutil.NoSuchProcess:
        return
    except psutil.Error as exc:
        logger.error("process_tree_termination_failed", extra={"pid": process_id, "error": str(exc)})
