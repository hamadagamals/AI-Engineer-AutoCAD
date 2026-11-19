"""Bounded process inspection and confirmation-gated graceful termination."""

from __future__ import annotations

import logging
import math
import os
from dataclasses import asdict, dataclass
from datetime import UTC, datetime
from typing import Any

import psutil

from agent.exceptions import ConfirmationRequiredError, ProcessManagerError


@dataclass(frozen=True, slots=True)
class ProcessInfo:
    """Non-secret metadata for one local process."""

    pid: int
    name: str
    status: str
    created_at: str | None
    memory_rss_bytes: int | None

    def to_dict(self) -> dict[str, Any]:
        """Return a JSON-serializable representation."""
        return asdict(self)


@dataclass(frozen=True, slots=True)
class ProcessListResult:
    """Bounded process listing with explicit truncation state."""

    processes: tuple[ProcessInfo, ...]
    skipped_count: int
    truncated: bool


@dataclass(frozen=True, slots=True)
class TerminationResult:
    """Result of a graceful process termination request."""

    pid: int
    terminated: bool
    timed_out: bool


class ProcessManager:
    """Inspect processes and gracefully terminate an exact confirmed process instance."""

    def __init__(
        self,
        *,
        max_processes: int = 500,
        logger: logging.Logger | None = None,
    ) -> None:
        if not isinstance(max_processes, int) or isinstance(max_processes, bool) or max_processes <= 0:
            raise ValueError("max_processes must be a positive integer.")
        self.max_processes = max_processes
        self._logger = logger or logging.getLogger("ai_engineer.process_manager")

    def list_processes(self) -> ProcessListResult:
        """Return PID-sorted process metadata without command lines or usernames."""
        processes: list[ProcessInfo] = []
        skipped_count = 0
        truncated = False
        attributes = ["pid", "name", "status", "create_time", "memory_info"]
        for process in psutil.process_iter(attributes):
            if len(processes) >= self.max_processes:
                truncated = True
                break
            try:
                info = process.info
                created = info.get("create_time")
                memory = info.get("memory_info")
                processes.append(
                    ProcessInfo(
                        pid=int(info["pid"]),
                        name=str(info.get("name") or ""),
                        status=str(info.get("status") or "unknown"),
                        created_at=(
                            datetime.fromtimestamp(float(created), tz=UTC).isoformat()
                            if created is not None
                            else None
                        ),
                        memory_rss_bytes=(int(memory.rss) if memory is not None else None),
                    )
                )
            except (KeyError, TypeError, ValueError, psutil.Error):
                skipped_count += 1
        processes.sort(key=lambda item: item.pid)
        result = ProcessListResult(tuple(processes), skipped_count, truncated)
        self._logger.info(
            "processes_listed",
            extra={
                "count": len(result.processes),
                "skipped": skipped_count,
                "truncated": truncated,
            },
        )
        return result

    def terminate(
        self,
        pid: int,
        *,
        expected_create_time: float,
        confirmed: bool = False,
        timeout_seconds: float = 5.0,
    ) -> TerminationResult:
        """Gracefully terminate one exact process instance after confirmation."""
        if not isinstance(pid, int) or isinstance(pid, bool) or pid <= 4:
            raise ProcessManagerError("System and invalid process identifiers are protected.")
        if pid == os.getpid():
            raise ProcessManagerError("ProcessManager cannot terminate its own process.")
        if not confirmed:
            self._logger.warning("process_termination_confirmation_required", extra={"pid": pid})
            raise ConfirmationRequiredError(
                f"Explicit confirmation is required to terminate process {pid}."
            )
        if (
            not isinstance(expected_create_time, (int, float))
            or isinstance(expected_create_time, bool)
            or not math.isfinite(expected_create_time)
            or expected_create_time <= 0
        ):
            raise ProcessManagerError("Expected process creation time must be a positive number.")
        if (
            not isinstance(timeout_seconds, (int, float))
            or isinstance(timeout_seconds, bool)
            or not 0 < timeout_seconds <= 30
        ):
            raise ProcessManagerError("Termination timeout must be greater than zero and at most 30 seconds.")

        try:
            process = psutil.Process(pid)
            actual_create_time = process.create_time()
            if abs(actual_create_time - expected_create_time) > 0.001:
                raise ProcessManagerError(
                    f"Process {pid} no longer matches the expected process instance."
                )
            process.terminate()
            try:
                process.wait(timeout=timeout_seconds)
            except psutil.TimeoutExpired:
                self._logger.warning("process_termination_timed_out", extra={"pid": pid})
                return TerminationResult(pid, terminated=False, timed_out=True)
        except psutil.NoSuchProcess as exc:
            raise ProcessManagerError(f"Process does not exist: {pid}") from exc
        except psutil.AccessDenied as exc:
            self._logger.error("process_termination_denied", extra={"pid": pid})
            raise ProcessManagerError(f"Access denied while terminating process {pid}.") from exc
        except psutil.Error as exc:
            self._logger.error(
                "process_termination_failed", extra={"pid": pid, "error": str(exc)}
            )
            raise ProcessManagerError(f"Cannot terminate process {pid}: {exc}") from exc
        self._logger.info("process_terminated", extra={"pid": pid})
        return TerminationResult(pid, terminated=True, timed_out=False)
