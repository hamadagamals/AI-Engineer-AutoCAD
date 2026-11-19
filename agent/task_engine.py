"""Durable synchronous task execution with approvals, retries, and cancellation."""

from __future__ import annotations

import logging
import threading
import uuid
from dataclasses import asdict, dataclass, replace
from datetime import UTC, datetime
from enum import StrEnum
from pathlib import Path
from typing import Any, Callable, Mapping

from agent.command_engine import CommandEngine
from agent.exceptions import (
    ApprovalDeniedError,
    ApprovalError,
    ApprovalRequiredError,
    CommandExecutionError,
    RetryableCommandError,
    ResourceNotFoundError,
    TaskCancelledError,
    TaskEngineError,
)
from agent.state import StateStore


class TaskStatus(StrEnum):
    """Persisted task lifecycle states."""

    QUEUED = "queued"
    AWAITING_APPROVAL = "awaiting_approval"
    RUNNING = "running"
    SUCCEEDED = "succeeded"
    FAILED = "failed"
    CANCELLED = "cancelled"


_TERMINAL_STATUSES = frozenset(
    {TaskStatus.SUCCEEDED, TaskStatus.FAILED, TaskStatus.CANCELLED}
)


@dataclass(frozen=True, slots=True)
class TaskRecord:
    """Durable task state and bounded result or error data."""

    task_id: str
    command_name: str
    payload: dict[str, Any]
    status: TaskStatus
    attempts: int
    max_attempts: int
    created_at: str
    updated_at: str
    approval_id: str | None = None
    result: Any = None
    error: str | None = None

    @classmethod
    def from_dict(cls, value: dict[str, Any]) -> TaskRecord:
        """Validate a persistent task record."""
        if any(
            not isinstance(value.get(key), str) or not value[key]
            for key in ("task_id", "command_name", "created_at", "updated_at")
        ):
            raise TaskEngineError("Persistent task string fields must be non-empty.")
        if not isinstance(value.get("payload"), dict):
            raise TaskEngineError("Persistent task payload must be an object.")
        if any(
            not isinstance(value.get(key), int) or isinstance(value.get(key), bool)
            for key in ("attempts", "max_attempts")
        ):
            raise TaskEngineError("Persistent task attempt values must be integers.")
        if value.get("approval_id") is not None and not isinstance(
            value.get("approval_id"), str
        ):
            raise TaskEngineError("Persistent approval identifier must be a string or null.")
        if value.get("error") is not None and not isinstance(value.get("error"), str):
            raise TaskEngineError("Persistent task error must be a string or null.")
        try:
            record = cls(
                task_id=value["task_id"],
                command_name=value["command_name"],
                payload=value["payload"],
                status=TaskStatus(value["status"]),
                attempts=value["attempts"],
                max_attempts=value["max_attempts"],
                created_at=value["created_at"],
                updated_at=value["updated_at"],
                approval_id=value.get("approval_id"),
                result=value.get("result"),
                error=value.get("error"),
            )
        except (KeyError, TypeError, ValueError) as exc:
            raise TaskEngineError(f"Invalid persistent task record: {exc}") from exc
        if not record.task_id or not record.command_name:
            raise TaskEngineError("Task identifiers and command name must be non-empty.")
        if record.attempts < 0 or not 1 <= record.max_attempts <= 3:
            raise TaskEngineError("Persistent task attempt values are invalid.")
        if record.attempts > record.max_attempts:
            raise TaskEngineError("Persistent task attempts exceed the configured maximum.")
        return record

    def to_dict(self) -> dict[str, Any]:
        """Return a JSON-compatible task record."""
        value = asdict(self)
        value["status"] = self.status.value
        return value


@dataclass(frozen=True, slots=True)
class TaskEvent:
    """Append-only audit transition stored with task history."""

    event_id: str
    task_id: str
    event: str
    timestamp: str
    from_status: str | None
    to_status: str | None

    def to_dict(self) -> dict[str, Any]:
        """Return a JSON-compatible event."""
        return asdict(self)


class TaskEngine:
    """Execute registered commands synchronously with durable bounded state."""

    def __init__(
        self,
        command_engine: CommandEngine,
        history_path: Path,
        *,
        clock: Callable[[], datetime] | None = None,
        maximum_tasks: int = 2_000,
        maximum_events: int = 10_000,
        logger: logging.Logger | None = None,
    ) -> None:
        if (
            not isinstance(maximum_tasks, int)
            or isinstance(maximum_tasks, bool)
            or maximum_tasks <= 0
            or not isinstance(maximum_events, int)
            or isinstance(maximum_events, bool)
            or maximum_events <= 0
        ):
            raise ValueError("Task and event history limits must be greater than zero.")
        self.command_engine = command_engine
        self._store = StateStore(history_path)
        self._clock = clock or (lambda: datetime.now(UTC))
        self.maximum_tasks = maximum_tasks
        self.maximum_events = maximum_events
        self._logger = logger or logging.getLogger("ai_engineer.task_engine")
        self._lock = threading.RLock()
        self._cancellations: dict[str, threading.Event] = {}
        self._ensure_document()

    def submit(
        self,
        command_name: str,
        payload: Mapping[str, Any],
        *,
        max_attempts: int = 1,
    ) -> TaskRecord:
        """Validate and durably queue one task."""
        if not isinstance(max_attempts, int) or isinstance(max_attempts, bool) or not 1 <= max_attempts <= 3:
            raise TaskEngineError("max_attempts must be an integer from 1 through 3.")
        validated = self.command_engine.validate_submission(command_name, payload)
        with self._lock:
            now = self._now()
            task = TaskRecord(
                task_id=uuid.uuid4().hex,
                command_name=command_name,
                payload=validated,
                status=TaskStatus.QUEUED,
                attempts=0,
                max_attempts=max_attempts,
                created_at=now,
                updated_at=now,
            )
            document = self._load()
            tasks = [TaskRecord.from_dict(item) for item in document["tasks"]]
            tasks.append(task)
            document["tasks"] = [item.to_dict() for item in tasks[-self.maximum_tasks :]]
            self._append_event(document, task.task_id, "submitted", None, task.status)
            self._store.save(document)
            self._cancellations[task.task_id] = threading.Event()
            self._logger.info(
                "task_submitted", extra={"task_id": task.task_id, "command": command_name}
            )
            return task

    def run(self, task_id: str, *, approval_id: str | None = None) -> TaskRecord:
        """Run a queued task to a terminal or awaiting-approval state."""
        while True:
            with self._lock:
                task = self.get(task_id)
                if task.status in _TERMINAL_STATUSES:
                    raise TaskEngineError(
                        f"Cannot run task in terminal status {task.status.value}: {task_id}"
                    )
                if task.status not in {TaskStatus.QUEUED, TaskStatus.AWAITING_APPROVAL}:
                    raise TaskEngineError(
                        f"Task is already being executed: {task_id}"
                    )
                cancellation = self._cancellations.setdefault(task_id, threading.Event())
                if cancellation.is_set():
                    return self._transition(task, TaskStatus.CANCELLED, event="cancelled")
                running = self._transition(
                    replace(task, attempts=task.attempts + 1, error=None),
                    TaskStatus.RUNNING,
                    event="started",
                )

            try:
                result = self.command_engine.execute(
                    running.command_name,
                    running.payload,
                    task_id=task_id,
                    cancellation_event=cancellation,
                    approval_id=approval_id,
                )
            except ApprovalRequiredError as exc:
                with self._lock:
                    awaiting = replace(
                        running,
                        attempts=running.attempts - 1,
                        approval_id=exc.approval_id,
                    )
                    return self._transition(
                        awaiting,
                        TaskStatus.AWAITING_APPROVAL,
                        event="approval_requested",
                    )
            except ApprovalDeniedError as exc:
                with self._lock:
                    return self._transition(
                        replace(running, error=_bounded_error(exc)),
                        TaskStatus.CANCELLED,
                        event="approval_denied",
                    )
            except ApprovalError as exc:
                with self._lock:
                    return self._transition(
                        replace(running, error=_bounded_error(exc)),
                        TaskStatus.FAILED,
                        event="approval_failed",
                    )
            except TaskCancelledError as exc:
                with self._lock:
                    return self._transition(
                        replace(running, error=_bounded_error(exc)),
                        TaskStatus.CANCELLED,
                        event="cancelled",
                    )
            except RetryableCommandError as exc:
                with self._lock:
                    current = self.get(task_id)
                    if cancellation.is_set():
                        return self._transition(
                            replace(current, error=_bounded_error(exc)),
                            TaskStatus.CANCELLED,
                            event="cancelled",
                        )
                    if current.attempts >= current.max_attempts:
                        return self._transition(
                            replace(current, error=_bounded_error(exc)),
                            TaskStatus.FAILED,
                            event="retries_exhausted",
                        )
                    self._transition(
                        replace(current, error=_bounded_error(exc)),
                        TaskStatus.QUEUED,
                        event="retry_scheduled",
                    )
                approval_id = None
                continue
            except CommandExecutionError as exc:
                with self._lock:
                    return self._transition(
                        replace(running, error=_bounded_error(exc)),
                        TaskStatus.FAILED,
                        event="failed",
                    )
            except Exception as exc:
                self._logger.error(
                    "task_handler_failed_unexpectedly",
                    extra={"task_id": task_id, "error_type": type(exc).__name__},
                )
                with self._lock:
                    safe_error = f"Unexpected {type(exc).__name__} while executing command."
                    return self._transition(
                        replace(running, error=safe_error),
                        TaskStatus.FAILED,
                        event="failed",
                    )
            else:
                with self._lock:
                    return self._transition(
                        replace(running, result=result, error=None),
                        TaskStatus.SUCCEEDED,
                        event="succeeded",
                    )

    def cancel(self, task_id: str) -> TaskRecord:
        """Cancel queued tasks immediately or signal a running task cooperatively."""
        with self._lock:
            task = self.get(task_id)
            if task.status in _TERMINAL_STATUSES:
                raise TaskEngineError(f"Task is already terminal: {task_id}")
            event = self._cancellations.setdefault(task_id, threading.Event())
            event.set()
            if task.status in {TaskStatus.QUEUED, TaskStatus.AWAITING_APPROVAL}:
                return self._transition(task, TaskStatus.CANCELLED, event="cancelled")
            document = self._load()
            self._append_event(
                document, task_id, "cancellation_requested", task.status, task.status
            )
            self._store.save(document)
            self._logger.info("task_cancellation_requested", extra={"task_id": task_id})
            return task

    def get(self, task_id: str) -> TaskRecord:
        """Return one durable task by identifier."""
        with self._lock:
            for item in self._load()["tasks"]:
                task = TaskRecord.from_dict(item)
                if task.task_id == task_id:
                    return task
        raise ResourceNotFoundError(f"Task does not exist: {task_id}")

    def list_tasks(self, *, limit: int = 100) -> tuple[TaskRecord, ...]:
        """Return the newest bounded task records."""
        if not isinstance(limit, int) or isinstance(limit, bool) or not 1 <= limit <= 500:
            raise TaskEngineError("Task list limit must be an integer from 1 through 500.")
        with self._lock:
            tasks = [TaskRecord.from_dict(item) for item in self._load()["tasks"]]
            return tuple(reversed(tasks[-limit:]))

    def _transition(
        self,
        task: TaskRecord,
        status: TaskStatus,
        *,
        event: str,
    ) -> TaskRecord:
        previous = task.status
        _validate_transition(previous, status)
        updated = replace(task, status=status, updated_at=self._now())
        document = self._load()
        tasks = [TaskRecord.from_dict(item) for item in document["tasks"]]
        for index, existing in enumerate(tasks):
            if existing.task_id == updated.task_id:
                tasks[index] = updated
                break
        else:
            raise TaskEngineError(f"Task disappeared from history: {updated.task_id}")
        document["tasks"] = [item.to_dict() for item in tasks]
        self._append_event(document, updated.task_id, event, previous, status)
        self._store.save(document)
        self._logger.info(
            "task_transitioned",
            extra={
                "task_id": updated.task_id,
                "from_status": previous.value,
                "to_status": status.value,
                "event": event,
            },
        )
        return updated

    def _append_event(
        self,
        document: dict[str, Any],
        task_id: str,
        event: str,
        from_status: TaskStatus | None,
        to_status: TaskStatus | None,
    ) -> None:
        events = [TaskEvent(**item) for item in document["events"]]
        events.append(
            TaskEvent(
                event_id=uuid.uuid4().hex,
                task_id=task_id,
                event=event,
                timestamp=self._now(),
                from_status=from_status.value if from_status else None,
                to_status=to_status.value if to_status else None,
            )
        )
        document["events"] = [item.to_dict() for item in events[-self.maximum_events :]]

    def _ensure_document(self) -> None:
        if not self._store.path.exists():
            self._store.save({"schema_version": 1, "tasks": [], "events": []})
        self._load()

    def _load(self) -> dict[str, Any]:
        document = self._store.load()
        if document.get("schema_version") != 1:
            raise TaskEngineError("Task history must use schema_version 1.")
        if not isinstance(document.get("tasks"), list) or not isinstance(
            document.get("events"), list
        ):
            raise TaskEngineError("Task history must contain task and event lists.")
        return document

    def _now(self) -> str:
        now = self._clock()
        if now.tzinfo is None:
            raise TaskEngineError("Task clock must return timezone-aware values.")
        return now.astimezone(UTC).isoformat()


def _validate_transition(previous: TaskStatus, target: TaskStatus) -> None:
    allowed = {
        TaskStatus.QUEUED: {TaskStatus.RUNNING, TaskStatus.CANCELLED},
        TaskStatus.AWAITING_APPROVAL: {TaskStatus.RUNNING, TaskStatus.CANCELLED},
        TaskStatus.RUNNING: {
            TaskStatus.QUEUED,
            TaskStatus.AWAITING_APPROVAL,
            TaskStatus.SUCCEEDED,
            TaskStatus.FAILED,
            TaskStatus.CANCELLED,
        },
    }
    if target not in allowed.get(previous, set()):
        raise TaskEngineError(
            f"Invalid task transition: {previous.value} -> {target.value}"
        )


def _bounded_error(error: Exception) -> str:
    message = str(error)
    return message[:1_000] if message else type(error).__name__
