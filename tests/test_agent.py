"""Tests for permissions, typed commands, approvals, and durable tasks."""

from __future__ import annotations

import json
import threading
import time
from datetime import UTC, datetime, timedelta
from pathlib import Path
from typing import Any, Mapping

import pytest

from agent.command_engine import CommandDefinition, CommandEngine, CommandRegistry
from agent.exceptions import (
    ApprovalError,
    CommandExecutionError,
    CommandRegistryError,
    PermissionDeniedError,
    ResourceNotFoundError,
    RetryableCommandError,
    TaskEngineError,
)
from agent.permissions import ApprovalManager, ApprovalStatus, PermissionPolicy, RiskLevel
from agent.task_engine import TaskEngine, TaskStatus


def validate_value(payload: Mapping[str, Any]) -> dict[str, Any]:
    value = payload["value"]
    if not isinstance(value, int) or isinstance(value, bool):
        raise ValueError("value must be an integer")
    return {"value": value}


def build_engines(
    tmp_path: Path,
    handler: Any,
    *,
    risk: RiskLevel = RiskLevel.LOW,
    allowed_permissions: frozenset[str] = frozenset({"demo.run"}),
    max_payload_bytes: int = 1024,
    max_result_bytes: int = 1024,
) -> tuple[CommandEngine, TaskEngine, ApprovalManager, CommandRegistry]:
    registry = CommandRegistry()
    registry.register(
        CommandDefinition(
            name="demo.execute",
            description="Execute a deterministic test command.",
            permission="demo.run",
            risk=risk,
            validator=validate_value,
            handler=handler,
        )
    )
    approvals = ApprovalManager(tmp_path / "approvals.json")
    command_engine = CommandEngine(
        registry,
        PermissionPolicy(allowed_permissions),
        approvals,
        max_payload_bytes=max_payload_bytes,
        max_result_bytes=max_result_bytes,
    )
    tasks = TaskEngine(command_engine, tmp_path / "history.json")
    return command_engine, tasks, approvals, registry


def test_registry_rejects_duplicate_and_unknown_commands(tmp_path: Path) -> None:
    definition = CommandDefinition(
        "demo.execute",
        "Description",
        "demo.run",
        RiskLevel.LOW,
        validate_value,
        lambda payload, context: payload,
    )
    registry = CommandRegistry()
    registry.register(definition)

    with pytest.raises(CommandRegistryError, match="already registered"):
        registry.register(definition)
    with pytest.raises(ResourceNotFoundError, match="not registered"):
        registry.get("missing")


def test_submission_requires_permission(tmp_path: Path) -> None:
    _, tasks, _, _ = build_engines(
        tmp_path,
        lambda payload, context: payload,
        allowed_permissions=frozenset(),
    )

    with pytest.raises(PermissionDeniedError, match="demo.run"):
        tasks.submit("demo.execute", {"value": 1})


def test_namespace_permission_wildcard_is_explicitly_supported(tmp_path: Path) -> None:
    _, tasks, _, _ = build_engines(
        tmp_path,
        lambda payload, context: payload,
        allowed_permissions=frozenset({"demo.*"}),
    )

    assert tasks.run(tasks.submit("demo.execute", {"value": 2}).task_id).status is TaskStatus.SUCCEEDED


def test_payload_validator_and_size_limit_run_before_queueing(tmp_path: Path) -> None:
    _, tasks, _, _ = build_engines(
        tmp_path, lambda payload, context: payload, max_payload_bytes=30
    )

    with pytest.raises(CommandExecutionError, match="value must be"):
        tasks.submit("demo.execute", {"value": "wrong"})
    with pytest.raises(CommandExecutionError, match="byte limit"):
        tasks.submit("demo.execute", {"value": 1, "padding": "x" * 100})
    assert tasks.list_tasks() == ()


def test_successful_task_is_persisted_with_transition_events(tmp_path: Path) -> None:
    _, tasks, _, _ = build_engines(
        tmp_path, lambda payload, context: {"answer": payload["value"] * 2}
    )

    submitted = tasks.submit("demo.execute", {"value": 3})
    completed = tasks.run(submitted.task_id)

    assert completed.status is TaskStatus.SUCCEEDED
    assert completed.attempts == 1
    assert completed.result == {"answer": 6}
    document = json.loads((tmp_path / "history.json").read_text(encoding="utf-8"))
    assert [event["event"] for event in document["events"]] == [
        "submitted",
        "started",
        "succeeded",
    ]
    reloaded = TaskEngine(tasks.command_engine, tmp_path / "history.json")
    assert reloaded.get(submitted.task_id).result == {"answer": 6}


def test_high_risk_task_requires_durable_one_use_approval(tmp_path: Path) -> None:
    _, tasks, approvals, _ = build_engines(
        tmp_path,
        lambda payload, context: {"done": payload["value"]},
        risk=RiskLevel.HIGH,
    )
    submitted = tasks.submit("demo.execute", {"value": 4})

    awaiting = tasks.run(submitted.task_id)

    assert awaiting.status is TaskStatus.AWAITING_APPROVAL
    assert awaiting.attempts == 0
    assert awaiting.approval_id is not None
    approvals.decide(awaiting.approval_id, approved=True)
    completed = tasks.run(awaiting.task_id, approval_id=awaiting.approval_id)
    assert completed.status is TaskStatus.SUCCEEDED
    assert approvals.get(awaiting.approval_id).status == ApprovalStatus.CONSUMED
    with pytest.raises(TaskEngineError, match="terminal status"):
        tasks.run(completed.task_id, approval_id=awaiting.approval_id)


def test_denied_approval_cancels_task(tmp_path: Path) -> None:
    _, tasks, approvals, _ = build_engines(
        tmp_path,
        lambda payload, context: payload,
        risk=RiskLevel.DESTRUCTIVE,
    )
    awaiting = tasks.run(tasks.submit("demo.execute", {"value": 1}).task_id)
    assert awaiting.approval_id is not None
    approvals.decide(awaiting.approval_id, approved=False)

    denied = tasks.run(awaiting.task_id, approval_id=awaiting.approval_id)

    assert denied.status is TaskStatus.CANCELLED
    assert "denied" in (denied.error or "")


def test_approval_cannot_be_used_for_another_task(tmp_path: Path) -> None:
    _, tasks, approvals, _ = build_engines(
        tmp_path,
        lambda payload, context: payload,
        risk=RiskLevel.HIGH,
    )
    first = tasks.run(tasks.submit("demo.execute", {"value": 1}).task_id)
    second = tasks.run(tasks.submit("demo.execute", {"value": 2}).task_id)
    assert first.approval_id is not None
    approvals.decide(first.approval_id, approved=True)

    failed = tasks.run(second.task_id, approval_id=first.approval_id)

    assert failed.status is TaskStatus.FAILED
    assert "does not match" in (failed.error or "")


def test_approval_expiration_is_enforced(tmp_path: Path) -> None:
    now = [datetime(2026, 8, 5, 12, 0, tzinfo=UTC)]
    approvals = ApprovalManager(tmp_path / "approvals.json", clock=lambda: now[0])
    record = approvals.request("task", "demo.execute", ttl_seconds=1)
    now[0] += timedelta(seconds=2)

    with pytest.raises(ApprovalError, match="expired"):
        approvals.decide(record.approval_id, approved=True)


def test_retryable_failure_retries_only_within_bound(tmp_path: Path) -> None:
    calls = 0

    def handler(payload: dict[str, Any], context: Any) -> dict[str, int]:
        nonlocal calls
        calls += 1
        if calls < 3:
            raise RetryableCommandError("temporary failure")
        return {"calls": calls}

    _, tasks, _, _ = build_engines(tmp_path, handler)
    task = tasks.submit("demo.execute", {"value": 1}, max_attempts=3)

    completed = tasks.run(task.task_id)

    assert completed.status is TaskStatus.SUCCEEDED
    assert completed.attempts == 3
    assert completed.result == {"calls": 3}


def test_unexpected_failure_is_not_retried_or_leaked(tmp_path: Path) -> None:
    calls = 0

    def handler(payload: dict[str, Any], context: Any) -> None:
        nonlocal calls
        calls += 1
        raise RuntimeError("sensitive internal detail")

    _, tasks, _, _ = build_engines(tmp_path, handler)
    failed = tasks.run(
        tasks.submit("demo.execute", {"value": 1}, max_attempts=3).task_id
    )

    assert failed.status is TaskStatus.FAILED
    assert failed.attempts == 1
    assert calls == 1
    assert "sensitive internal detail" not in (failed.error or "")


def test_non_json_and_oversized_results_fail_task(tmp_path: Path) -> None:
    _, non_json_tasks, _, _ = build_engines(
        tmp_path / "non-json", lambda payload, context: object()
    )
    non_json = non_json_tasks.run(
        non_json_tasks.submit("demo.execute", {"value": 1}).task_id
    )
    assert non_json.status is TaskStatus.FAILED
    assert "non-JSON" in (non_json.error or "")

    _, large_tasks, _, _ = build_engines(
        tmp_path / "large",
        lambda payload, context: "x" * 100,
        max_result_bytes=20,
    )
    large = large_tasks.run(large_tasks.submit("demo.execute", {"value": 1}).task_id)
    assert large.status is TaskStatus.FAILED
    assert "byte limit" in (large.error or "")


def test_queued_task_can_be_cancelled_without_execution(tmp_path: Path) -> None:
    calls = 0

    def handler(payload: dict[str, Any], context: Any) -> None:
        nonlocal calls
        calls += 1

    _, tasks, _, _ = build_engines(tmp_path, handler)
    submitted = tasks.submit("demo.execute", {"value": 1})

    cancelled = tasks.cancel(submitted.task_id)

    assert cancelled.status is TaskStatus.CANCELLED
    assert calls == 0


def test_running_task_supports_cooperative_cancellation(tmp_path: Path) -> None:
    started = threading.Event()

    def handler(payload: dict[str, Any], context: Any) -> None:
        started.set()
        while True:
            context.raise_if_cancelled()
            time.sleep(0.01)

    _, tasks, _, _ = build_engines(tmp_path, handler)
    submitted = tasks.submit("demo.execute", {"value": 1})
    holder: list[Any] = []
    worker = threading.Thread(target=lambda: holder.append(tasks.run(submitted.task_id)))
    worker.start()
    assert started.wait(timeout=2)

    tasks.cancel(submitted.task_id)
    worker.join(timeout=2)

    assert not worker.is_alive()
    assert holder[0].status is TaskStatus.CANCELLED


@pytest.mark.parametrize("attempts", [0, 4, True])
def test_retry_count_is_bounded(tmp_path: Path, attempts: int) -> None:
    _, tasks, _, _ = build_engines(tmp_path, lambda payload, context: payload)

    with pytest.raises(TaskEngineError, match="max_attempts"):
        tasks.submit("demo.execute", {"value": 1}, max_attempts=attempts)


def test_corrupt_persistent_attempt_type_is_rejected(tmp_path: Path) -> None:
    _, tasks, _, _ = build_engines(tmp_path, lambda payload, context: payload)
    submitted = tasks.submit("demo.execute", {"value": 1})
    history_path = tmp_path / "history.json"
    document = json.loads(history_path.read_text(encoding="utf-8"))
    document["tasks"][0]["attempts"] = True
    history_path.write_text(json.dumps(document), encoding="utf-8")

    with pytest.raises(TaskEngineError, match="must be integers"):
        tasks.get(submitted.task_id)
