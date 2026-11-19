"""Read-only, non-secret local system information."""

from __future__ import annotations

import logging
import platform
from dataclasses import asdict, dataclass
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

import psutil

from agent.exceptions import SystemInfoError
from core.file_manager import FileManager


@dataclass(frozen=True, slots=True)
class DiskSnapshot:
    """Disk usage for one configured workspace root."""

    path: str
    total_bytes: int
    used_bytes: int
    free_bytes: int
    percent_used: float


@dataclass(frozen=True, slots=True)
class SystemSnapshot:
    """Bounded system facts that intentionally omit identity and environment data."""

    operating_system: str
    release: str
    machine: str
    python_version: str
    logical_cpu_count: int | None
    physical_cpu_count: int | None
    memory_total_bytes: int
    memory_available_bytes: int
    memory_percent_used: float
    booted_at: str
    disks: tuple[DiskSnapshot, ...]

    def to_dict(self) -> dict[str, Any]:
        """Return a JSON-serializable snapshot."""
        return asdict(self)


class SystemInfo:
    """Collect read-only system information scoped to configured workspace disks."""

    def __init__(
        self, file_manager: FileManager, *, logger: logging.Logger | None = None
    ) -> None:
        self._file_manager = file_manager
        self._logger = logger or logging.getLogger("ai_engineer.system_info")

    def snapshot(self) -> SystemSnapshot:
        """Collect one bounded system snapshot without secrets or user identity."""
        try:
            memory = psutil.virtual_memory()
            disks = tuple(self._disk_snapshot(root) for root in self._file_manager.workspace_roots)
            snapshot = SystemSnapshot(
                operating_system=platform.system(),
                release=platform.release(),
                machine=platform.machine(),
                python_version=platform.python_version(),
                logical_cpu_count=psutil.cpu_count(logical=True),
                physical_cpu_count=psutil.cpu_count(logical=False),
                memory_total_bytes=memory.total,
                memory_available_bytes=memory.available,
                memory_percent_used=memory.percent,
                booted_at=datetime.fromtimestamp(psutil.boot_time(), tz=UTC).isoformat(),
                disks=disks,
            )
        except (OSError, ValueError, psutil.Error) as exc:
            self._logger.error("system_snapshot_failed", extra={"error": str(exc)})
            raise SystemInfoError(f"Cannot collect local system information: {exc}") from exc
        self._logger.info(
            "system_snapshot_collected",
            extra={"workspace_disks": len(snapshot.disks)},
        )
        return snapshot

    @staticmethod
    def _disk_snapshot(path: Path) -> DiskSnapshot:
        usage = psutil.disk_usage(str(path))
        return DiskSnapshot(
            path=str(path),
            total_bytes=usage.total,
            used_bytes=usage.used,
            free_bytes=usage.free,
            percent_used=usage.percent,
        )
