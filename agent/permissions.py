"""Durable permission and human-approval policy."""

from __future__ import annotations

import threading
import uuid
from dataclasses import asdict, dataclass
from datetime import UTC, datetime, timedelta
from enum import IntEnum, StrEnum
from pathlib import Path
from typing import Any, Callable

from agent.exceptions import (
    ApprovalDeniedError,
    ApprovalError,
    PermissionDeniedError,
    ResourceNotFoundError,
)
from agent.state import StateStore


class RiskLevel(IntEnum):
    """Ordered risk levels used to trigger human approval."""

    LOW = 1
    MODERATE = 2
    HIGH = 3
    DESTRUCTIVE = 4


class ApprovalStatus(StrEnum):
    """Approval state constants persisted as strings."""

    PENDING = "pending"
    APPROVED = "approved"
    DENIED = "denied"
    CONSUMED = "consumed"
    EXPIRED = "expired"


@dataclass(frozen=True, slots=True)
class ApprovalRecord:
    """A one-use approval tied to one task and command."""

    approval_id: str
    task_id: str
    command_name: str
    status: str
    requested_at: str
    expires_at: str
    decided_at: str | None = None
    consumed_at: str | None = None

    @classmethod
    def from_dict(cls, value: dict[str, Any]) -> ApprovalRecord:
        """Validate and construct an approval from persistent JSON data."""
        required_strings = (
            "approval_id",
            "task_id",
            "command_name",
            "status",
            "requested_at",
            "expires_at",
        )
        if any(not isinstance(value.get(key), str) or not value[key] for key in required_strings):
            raise ApprovalError("Persistent approval string fields must be non-empty.")
        if any(
            value.get(key) is not None and not isinstance(value.get(key), str)
            for key in ("decided_at", "consumed_at")
        ):
            raise ApprovalError("Optional approval timestamps must be strings or null.")
        try:
            record = cls(
                approval_id=value["approval_id"],
                task_id=value["task_id"],
                command_name=value["command_name"],
                status=value["status"],
                requested_at=value["requested_at"],
                expires_at=value["expires_at"],
                decided_at=value.get("decided_at"),
                consumed_at=value.get("consumed_at"),
            )
        except (KeyError, TypeError, ValueError) as exc:
            raise ApprovalError(f"Invalid persistent approval record: {exc}") from exc
        if record.status not in {
            ApprovalStatus.PENDING,
            ApprovalStatus.APPROVED,
            ApprovalStatus.DENIED,
            ApprovalStatus.CONSUMED,
            ApprovalStatus.EXPIRED,
        }:
            raise ApprovalError(f"Invalid approval status: {record.status}")
        if not record.approval_id or not record.task_id or not record.command_name:
            raise ApprovalError("Approval identifiers and command name must be non-empty.")
        _parse_time(record.requested_at)
        _parse_time(record.expires_at)
        return record

    def to_dict(self) -> dict[str, Any]:
        """Return a JSON-compatible record."""
        return asdict(self)


@dataclass(frozen=True, slots=True)
class PermissionPolicy:
    """Capabilities granted to the command engine and approval risk threshold."""

    allowed_permissions: frozenset[str]
    approval_threshold: RiskLevel = RiskLevel.HIGH

    def authorize(self, permission: str) -> None:
        """Require an exact permission or its explicit wildcard namespace."""
        namespace = permission.split(".", 1)[0] + ".*"
        if permission not in self.allowed_permissions and namespace not in self.allowed_permissions:
            raise PermissionDeniedError(f"Permission is not granted: {permission}")

    def requires_approval(self, risk: RiskLevel) -> bool:
        """Return whether *risk* meets the configured approval threshold."""
        return risk >= self.approval_threshold


class ApprovalManager:
    """Persist, decide, expire, and atomically consume one-use approvals."""

    def __init__(
        self,
        path: Path,
        *,
        clock: Callable[[], datetime] | None = None,
        maximum_records: int = 2_000,
    ) -> None:
        if (
            not isinstance(maximum_records, int)
            or isinstance(maximum_records, bool)
            or maximum_records <= 0
        ):
            raise ValueError("maximum_records must be greater than zero.")
        self._store = StateStore(path)
        self._clock = clock or (lambda: datetime.now(UTC))
        self.maximum_records = maximum_records
        self._lock = threading.RLock()
        self._ensure_document()

    def request(
        self, task_id: str, command_name: str, *, ttl_seconds: int = 900
    ) -> ApprovalRecord:
        """Create or return the existing pending approval for a task command."""
        if (
            not isinstance(ttl_seconds, int)
            or isinstance(ttl_seconds, bool)
            or not 1 <= ttl_seconds <= 86_400
        ):
            raise ApprovalError("Approval lifetime must be from 1 through 86400 seconds.")
        if not task_id or not command_name:
            raise ApprovalError("Approval task and command identifiers must be non-empty.")
        with self._lock:
            document = self._load()
            now = self._now()
            records = [ApprovalRecord.from_dict(item) for item in document["approvals"]]
            for record in records:
                if (
                    record.task_id == task_id
                    and record.command_name == command_name
                    and record.status == ApprovalStatus.PENDING
                    and _parse_time(record.expires_at) > now
                ):
                    return record
            record = ApprovalRecord(
                approval_id=uuid.uuid4().hex,
                task_id=task_id,
                command_name=command_name,
                status=ApprovalStatus.PENDING,
                requested_at=now.isoformat(),
                expires_at=(now + timedelta(seconds=ttl_seconds)).isoformat(),
            )
            records.append(record)
            document["approvals"] = [
                item.to_dict() for item in records[-self.maximum_records :]
            ]
            self._store.save(document)
            return record

    def decide(self, approval_id: str, *, approved: bool) -> ApprovalRecord:
        """Approve or deny a pending request."""
        with self._lock:
            document = self._load()
            records = [ApprovalRecord.from_dict(item) for item in document["approvals"]]
            index, record = _find_approval(records, approval_id)
            if record.status != ApprovalStatus.PENDING:
                raise ApprovalError(f"Approval is not pending: {approval_id}")
            now = self._now()
            if _parse_time(record.expires_at) <= now:
                expired = _replace_approval(record, status=ApprovalStatus.EXPIRED)
                records[index] = expired
                self._save_records(document, records)
                raise ApprovalError(f"Approval has expired: {approval_id}")
            updated = _replace_approval(
                record,
                status=ApprovalStatus.APPROVED if approved else ApprovalStatus.DENIED,
                decided_at=now.isoformat(),
            )
            records[index] = updated
            self._save_records(document, records)
            return updated

    def consume(self, approval_id: str, task_id: str, command_name: str) -> ApprovalRecord:
        """Validate and consume an approved request exactly once."""
        with self._lock:
            document = self._load()
            records = [ApprovalRecord.from_dict(item) for item in document["approvals"]]
            index, record = _find_approval(records, approval_id)
            if record.task_id != task_id or record.command_name != command_name:
                raise ApprovalError("Approval does not match this task and command.")
            if record.status == ApprovalStatus.DENIED:
                raise ApprovalDeniedError(f"Approval was denied: {approval_id}")
            if record.status != ApprovalStatus.APPROVED:
                raise ApprovalError(f"Approval is not approved and usable: {approval_id}")
            now = self._now()
            if _parse_time(record.expires_at) <= now:
                records[index] = _replace_approval(record, status=ApprovalStatus.EXPIRED)
                self._save_records(document, records)
                raise ApprovalError(f"Approval has expired: {approval_id}")
            consumed = _replace_approval(
                record, status=ApprovalStatus.CONSUMED, consumed_at=now.isoformat()
            )
            records[index] = consumed
            self._save_records(document, records)
            return consumed

    def get(self, approval_id: str) -> ApprovalRecord:
        """Return one approval record."""
        with self._lock:
            records = [
                ApprovalRecord.from_dict(item) for item in self._load()["approvals"]
            ]
            return _find_approval(records, approval_id)[1]

    def _ensure_document(self) -> None:
        if not self._store.path.exists():
            self._store.save({"schema_version": 1, "approvals": []})
        self._load()

    def _load(self) -> dict[str, Any]:
        document = self._store.load()
        if document.get("schema_version") != 1 or not isinstance(
            document.get("approvals"), list
        ):
            raise ApprovalError("Approval store must use schema_version 1 and an approvals list.")
        return document

    def _save_records(
        self, document: dict[str, Any], records: list[ApprovalRecord]
    ) -> None:
        document["approvals"] = [item.to_dict() for item in records]
        self._store.save(document)

    def _now(self) -> datetime:
        now = self._clock()
        if now.tzinfo is None:
            raise ApprovalError("Approval clock must return timezone-aware values.")
        return now.astimezone(UTC)


def _find_approval(
    records: list[ApprovalRecord], approval_id: str
) -> tuple[int, ApprovalRecord]:
    for index, record in enumerate(records):
        if record.approval_id == approval_id:
            return index, record
    raise ResourceNotFoundError(f"Approval does not exist: {approval_id}")


def _replace_approval(record: ApprovalRecord, **changes: Any) -> ApprovalRecord:
    values = record.to_dict()
    values.update(changes)
    return ApprovalRecord.from_dict(values)


def _parse_time(value: str) -> datetime:
    try:
        parsed = datetime.fromisoformat(value)
    except ValueError as exc:
        raise ApprovalError(f"Invalid approval timestamp: {value}") from exc
    if parsed.tzinfo is None:
        raise ApprovalError(f"Approval timestamp lacks timezone: {value}")
    return parsed.astimezone(UTC)
