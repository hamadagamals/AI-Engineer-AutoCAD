"""Human-reviewed, bounded plan-act-observe-verify developer loop."""

from __future__ import annotations

import json
import logging
import threading
import uuid
from dataclasses import asdict, dataclass, replace
from datetime import UTC, datetime
from enum import StrEnum
from pathlib import Path
from typing import Any, Callable, Mapping

from agent.exceptions import DeveloperAgentError, ProviderError, ResourceNotFoundError
from agent.permissions import ApprovalStatus, RiskLevel
from agent.provider import ProviderAdapter, ProviderCommand, ProviderPhase, ProviderRequest
from agent.state import StateStore
from agent.task_engine import TaskEngine, TaskRecord, TaskStatus


class DeveloperRunStatus(StrEnum):
    """Durable states for one developer-agent run."""

    PLANNING = "planning"
    AWAITING_PLAN_REVIEW = "awaiting_plan_review"
    READY = "ready"
    AWAITING_ACTION_REVIEW = "awaiting_action_review"
    AWAITING_COMMAND_APPROVAL = "awaiting_command_approval"
    SUCCEEDED = "succeeded"
    FAILED = "failed"
    CANCELLED = "cancelled"
    LIMIT_REACHED = "limit_reached"


class ProposedActionStatus(StrEnum):
    """Human and command review states for an exact proposed action."""

    PENDING_REVIEW = "pending_review"
    APPROVED = "approved"
    AWAITING_COMMAND_APPROVAL = "awaiting_command_approval"
    EXECUTED = "executed"
    DENIED = "denied"


_TERMINAL_RUN_STATUSES = frozenset(
    {
        DeveloperRunStatus.SUCCEEDED,
        DeveloperRunStatus.FAILED,
        DeveloperRunStatus.CANCELLED,
        DeveloperRunStatus.LIMIT_REACHED,
    }
)


@dataclass(frozen=True, slots=True)
class ProposedAction:
    """Exact provider-proposed command awaiting local human review."""

    action_id: str
    command_name: str
    payload: dict[str, Any]
    rationale: str
    risk: str
    status: ProposedActionStatus
    task_id: str | None = None
    approval_id: str | None = None

    @classmethod
    def from_dict(cls, value: Mapping[str, Any]) -> ProposedAction:
        required = ("action_id", "command_name", "rationale", "risk", "status")
        if any(not isinstance(value.get(key), str) or not value[key] for key in required):
            raise DeveloperAgentError("Persistent proposed-action strings must be non-empty.")
        if not isinstance(value.get("payload"), dict):
            raise DeveloperAgentError("Persistent proposed-action payload must be an object.")
        for key in ("task_id", "approval_id"):
            if value.get(key) is not None and not isinstance(value.get(key), str):
                raise DeveloperAgentError(f"Persistent proposed-action {key} is invalid.")
        try:
            return cls(
                action_id=value["action_id"],
                command_name=value["command_name"],
                payload=dict(value["payload"]),
                rationale=value["rationale"],
                risk=value["risk"],
                status=ProposedActionStatus(value["status"]),
                task_id=value.get("task_id"),
                approval_id=value.get("approval_id"),
            )
        except (KeyError, TypeError, ValueError) as exc:
            raise DeveloperAgentError(f"Invalid persistent proposed action: {exc}") from exc

    def to_dict(self) -> dict[str, Any]:
        value = asdict(self)
        value["status"] = self.status.value
        return value


@dataclass(frozen=True, slots=True)
class DeveloperRun:
    """Durable, bounded state for one reviewed developer goal."""

    run_id: str
    provider_name: str
    goal: str
    status: DeveloperRunStatus
    plan_summary: str | None
    plan_steps: tuple[str, ...]
    action_count: int
    maximum_actions: int
    provider_calls: int
    maximum_provider_calls: int
    created_at: str
    updated_at: str
    pending_action: ProposedAction | None = None
    last_task_id: str | None = None
    last_verification_accepted: bool | None = None
    last_verification_reason: str | None = None
    completion_summary: str | None = None
    error: str | None = None
    recent_history: tuple[dict[str, Any], ...] = ()

    @classmethod
    def from_dict(cls, value: Mapping[str, Any]) -> DeveloperRun:
        required = ("run_id", "provider_name", "goal", "status", "created_at", "updated_at")
        if any(not isinstance(value.get(key), str) or not value[key] for key in required):
            raise DeveloperAgentError("Persistent developer-run strings must be non-empty.")
        integer_fields = (
            "action_count",
            "maximum_actions",
            "provider_calls",
            "maximum_provider_calls",
        )
        if any(
            not isinstance(value.get(key), int) or isinstance(value.get(key), bool)
            for key in integer_fields
        ):
            raise DeveloperAgentError("Persistent developer-run counters must be integers.")
        steps = value.get("plan_steps")
        if not isinstance(steps, list) or any(not isinstance(item, str) for item in steps):
            raise DeveloperAgentError("Persistent developer plan steps must be strings.")
        optional_strings = (
            "plan_summary",
            "last_task_id",
            "last_verification_reason",
            "completion_summary",
            "error",
        )
        if any(
            value.get(key) is not None and not isinstance(value.get(key), str)
            for key in optional_strings
        ):
            raise DeveloperAgentError("Persistent optional developer-run strings are invalid.")
        accepted = value.get("last_verification_accepted")
        if accepted is not None and not isinstance(accepted, bool):
            raise DeveloperAgentError("Persistent verification decision must be boolean or null.")
        pending = value.get("pending_action")
        if pending is not None and not isinstance(pending, dict):
            raise DeveloperAgentError("Persistent pending action must be an object or null.")
        history = value.get("recent_history", [])
        if (
            not isinstance(history, list)
            or len(history) > 10
            or any(not isinstance(item, dict) for item in history)
        ):
            raise DeveloperAgentError("Persistent developer history is invalid.")
        try:
            run = cls(
                run_id=value["run_id"],
                provider_name=value["provider_name"],
                goal=value["goal"],
                status=DeveloperRunStatus(value["status"]),
                plan_summary=value.get("plan_summary"),
                plan_steps=tuple(steps),
                action_count=value["action_count"],
                maximum_actions=value["maximum_actions"],
                provider_calls=value["provider_calls"],
                maximum_provider_calls=value["maximum_provider_calls"],
                created_at=value["created_at"],
                updated_at=value["updated_at"],
                pending_action=ProposedAction.from_dict(pending) if pending else None,
                last_task_id=value.get("last_task_id"),
                last_verification_accepted=accepted,
                last_verification_reason=value.get("last_verification_reason"),
                completion_summary=value.get("completion_summary"),
                error=value.get("error"),
                recent_history=tuple(dict(item) for item in history),
            )
        except (KeyError, TypeError, ValueError) as exc:
            raise DeveloperAgentError(f"Invalid persistent developer run: {exc}") from exc
        if not 1 <= run.maximum_actions <= 10 or not 0 <= run.action_count <= run.maximum_actions:
            raise DeveloperAgentError("Persistent developer action bounds are invalid.")
        if not 3 <= run.maximum_provider_calls <= 32 or not (
            0 <= run.provider_calls <= run.maximum_provider_calls
        ):
            raise DeveloperAgentError("Persistent provider-call bounds are invalid.")
        _validate_persistent_run_invariants(run)
        return run

    def to_dict(self) -> dict[str, Any]:
        value = asdict(self)
        value["status"] = self.status.value
        value["plan_steps"] = list(self.plan_steps)
        value["pending_action"] = self.pending_action.to_dict() if self.pending_action else None
        value["recent_history"] = [dict(item) for item in self.recent_history]
        return value


@dataclass(frozen=True, slots=True)
class DeveloperEvent:
    event_id: str
    run_id: str
    event: str
    timestamp: str
    status: str
    command_name: str | None = None

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


class DeveloperAgent:
    """Orchestrate reviewed provider proposals through the existing task engine."""

    def __init__(
        self,
        provider: ProviderAdapter,
        tasks: TaskEngine,
        history_path: Path,
        *,
        clock: Callable[[], datetime] | None = None,
        maximum_runs: int = 500,
        maximum_events: int = 5_000,
        maximum_goal_bytes: int = 16_384,
        logger: logging.Logger | None = None,
    ) -> None:
        if not isinstance(provider, ProviderAdapter):
            raise DeveloperAgentError("Developer provider must be a ProviderAdapter.")
        if any(
            not isinstance(item, int) or isinstance(item, bool) or item <= 0
            for item in (maximum_runs, maximum_events, maximum_goal_bytes)
        ):
            raise DeveloperAgentError("Developer-agent limits must be positive integers.")
        self.provider = provider
        self.tasks = tasks
        self._store = StateStore(history_path)
        self._clock = clock or (lambda: datetime.now(UTC))
        self.maximum_runs = maximum_runs
        self.maximum_events = maximum_events
        self.maximum_goal_bytes = maximum_goal_bytes
        self._logger = logger or logging.getLogger("ai_engineer.developer_agent")
        self._lock = threading.RLock()
        self._provider_calls_in_progress: set[str] = set()
        self._ensure_document()

    def create_run(self, goal: str, *, maximum_actions: int = 5) -> DeveloperRun:
        """Create a run and request a bounded plan that always awaits human review."""
        clean_goal = self._validate_goal(goal)
        if (
            not isinstance(maximum_actions, int)
            or isinstance(maximum_actions, bool)
            or not 1 <= maximum_actions <= 10
        ):
            raise DeveloperAgentError("maximum_actions must be an integer from 1 through 10.")
        now = self._now()
        run = DeveloperRun(
            run_id=uuid.uuid4().hex,
            provider_name=self.provider.name,
            goal=clean_goal,
            status=DeveloperRunStatus.PLANNING,
            plan_summary=None,
            plan_steps=(),
            action_count=0,
            maximum_actions=maximum_actions,
            provider_calls=0,
            maximum_provider_calls=min(32, maximum_actions * 2 + 2),
            created_at=now,
            updated_at=now,
        )
        with self._lock:
            self._insert(run, "run_created")
            response, called = self._generate(run, ProviderPhase.PLAN)
            if response is None:
                return called
            try:
                summary, steps = _parse_plan(response)
                explicit_scope = self._explicit_goal_command_scope(called.goal)
                if explicit_scope is not None:
                    outside_scope = tuple(step for step in steps if step not in explicit_scope)
                    if outside_scope:
                        raise ProviderError(
                            "Provider plan exceeded the explicit user tool scope: "
                            + ", ".join(outside_scope)
                        )
            except ProviderError as exc:
                return self._fail(called, exc)
            planned = replace(
                called,
                status=DeveloperRunStatus.AWAITING_PLAN_REVIEW,
                plan_summary=summary,
                plan_steps=steps,
                error=None,
            )
            return self._replace(planned, "plan_proposed")

    def review_plan(self, run_id: str, *, approved: bool) -> DeveloperRun:
        """Record the human decision for the provider's complete plan."""
        _require_boolean(approved, "approved")
        with self._lock:
            run = self.get(run_id)
            if run.status is not DeveloperRunStatus.AWAITING_PLAN_REVIEW:
                raise DeveloperAgentError("Plan review is not available in the current run state.")
            if not approved:
                return self._replace(
                    replace(run, status=DeveloperRunStatus.CANCELLED), "plan_denied"
                )
            return self._replace(replace(run, status=DeveloperRunStatus.READY), "plan_approved")

    def advance(self, run_id: str) -> DeveloperRun:
        """Advance one bounded provider phase or resume one approved command."""
        with self._lock:
            run = self.get(run_id)
            if run_id in self._provider_calls_in_progress:
                raise DeveloperAgentError(
                    "A provider call is already in progress for this developer run."
                )
            if run.status is DeveloperRunStatus.AWAITING_COMMAND_APPROVAL:
                return self._resume_approved_command(run)
            if run.status is not DeveloperRunStatus.READY:
                raise DeveloperAgentError("Run cannot advance from its current state.")
            current = run
            policy_feedback: dict[str, Any] | None = None
            # A local model can occasionally get stuck proposing an already successful action.
            # Recover internally a small number of times instead of surfacing/re-running the
            # deterministic duplicate or consuming the whole action budget. Provider
            # verification is advisory and cannot make an unchanged successful command useful.
            for _recovery_attempt in range(3):
                response, called = self._generate(
                    current, ProviderPhase.ACT, observation=policy_feedback
                )
                if response is None:
                    return called
                try:
                    decision = _parse_action(response)
                    if decision["kind"] == "complete":
                        if called.action_count == 0 or called.last_verification_accepted is not True:
                            raise ProviderError(
                                "Provider cannot complete before an action has succeeded and been verified."
                            )
                        completed = replace(
                            called,
                            status=DeveloperRunStatus.SUCCEEDED,
                            completion_summary=decision["summary"],
                            pending_action=None,
                            error=None,
                        )
                        return self._replace(completed, "run_completed")
                    if called.action_count >= called.maximum_actions:
                        return self._limit(called, "The maximum action count was reached.")
                    command_name = decision["command_name"]
                    allowed_commands = {
                        item.name for item in self._command_catalog(called, ProviderPhase.ACT)
                    }
                    if command_name not in allowed_commands:
                        raise ProviderError(
                            "Provider action exceeded the reviewed/user command scope: "
                            + command_name
                        )
                    payload = self.tasks.command_engine.validate_submission(
                        command_name, decision["payload"]
                    )
                    no_progress_reason = _no_progress_reason(
                        called.recent_history,
                        command_name,
                        payload,
                        potentially_mutating_commands={
                            item.name
                            for item in self.tasks.command_engine.registry.list_definitions()
                            if item.risk > RiskLevel.LOW
                        },
                    )
                    if no_progress_reason is not None:
                        current = self._replace(
                            replace(
                                called,
                                status=DeveloperRunStatus.READY,
                                pending_action=None,
                                error=None,
                            ),
                            "no_progress_action_blocked",
                            command_name=command_name,
                        )
                        policy_feedback = {
                            "kind": "policy_feedback",
                            "blocked_command_name": command_name,
                            "blocked_payload": payload,
                            "reason": no_progress_reason,
                            "required_next_step": (
                                "Do not repeat the blocked command and payload. Choose a different "
                                "available command, or use the same command with a different concrete "
                                "payload supported by prior evidence. Complete only if the user goal "
                                "is materially satisfied by verified evidence."
                            ),
                        }
                        continue
                    definition = self.tasks.command_engine.registry.get(command_name)
                    proposed = ProposedAction(
                        action_id=uuid.uuid4().hex,
                        command_name=command_name,
                        payload=payload,
                        rationale=decision["rationale"],
                        risk=definition.risk.name.lower(),
                        status=ProposedActionStatus.PENDING_REVIEW,
                    )
                except Exception as exc:
                    if isinstance(exc, ProviderError):
                        error = exc
                    else:
                        error = ProviderError(f"Provider proposed an invalid command: {exc}")
                    return self._fail(called, error)
                proposed_run = replace(
                    called,
                    status=DeveloperRunStatus.AWAITING_ACTION_REVIEW,
                    pending_action=proposed,
                    error=None,
                )
                return self._replace(
                    proposed_run, "action_proposed", command_name=proposed.command_name
                )
            return self._fail(
                current,
                ProviderError(
                    "Provider repeatedly proposed no-progress actions after successful execution."
                ),
            )

    def review_action(self, run_id: str, *, approved: bool) -> DeveloperRun:
        """Review the exact command and payload, then execute only when approved."""
        _require_boolean(approved, "approved")
        with self._lock:
            run = self.get(run_id)
            action = run.pending_action
            if run.status is not DeveloperRunStatus.AWAITING_ACTION_REVIEW or action is None:
                raise DeveloperAgentError(
                    "Action review is not available in the current run state."
                )
            if not approved:
                denied = replace(action, status=ProposedActionStatus.DENIED)
                return self._replace(
                    replace(run, status=DeveloperRunStatus.CANCELLED, pending_action=denied),
                    "action_denied",
                    command_name=action.command_name,
                )
            approved_action = replace(action, status=ProposedActionStatus.APPROVED)
            task = self.tasks.submit(action.command_name, action.payload)
            task = self.tasks.run(task.task_id)
            approved_action = replace(approved_action, task_id=task.task_id)
            if task.status is TaskStatus.AWAITING_APPROVAL:
                awaiting = replace(
                    approved_action,
                    status=ProposedActionStatus.AWAITING_COMMAND_APPROVAL,
                    approval_id=task.approval_id,
                )
                return self._replace(
                    replace(
                        run,
                        status=DeveloperRunStatus.AWAITING_COMMAND_APPROVAL,
                        pending_action=awaiting,
                        last_task_id=task.task_id,
                    ),
                    "command_approval_requested",
                    command_name=action.command_name,
                )
            return self._observe_and_verify(run, approved_action, task)

    def cancel(self, run_id: str) -> DeveloperRun:
        """Cancel a non-terminal run and any queued or awaiting task."""
        with self._lock:
            run = self.get(run_id)
            if run.status in _TERMINAL_RUN_STATUSES:
                raise DeveloperAgentError("Developer run is already terminal.")
            action = run.pending_action
            if action and action.task_id:
                task = self.tasks.get(action.task_id)
                if task.status not in {
                    TaskStatus.SUCCEEDED,
                    TaskStatus.FAILED,
                    TaskStatus.CANCELLED,
                }:
                    self.tasks.cancel(task.task_id)
            return self._replace(replace(run, status=DeveloperRunStatus.CANCELLED), "run_cancelled")

    def get(self, run_id: str) -> DeveloperRun:
        """Return one durable developer run."""
        with self._lock:
            for item in self._load()["runs"]:
                run = DeveloperRun.from_dict(item)
                if run.run_id == run_id:
                    return run
        raise ResourceNotFoundError(f"Developer run does not exist: {run_id}")

    def list_runs(self, *, limit: int = 100) -> tuple[DeveloperRun, ...]:
        """Return newest durable runs within a strict limit."""
        if not isinstance(limit, int) or isinstance(limit, bool) or not 1 <= limit <= 500:
            raise DeveloperAgentError("Developer run list limit must be from 1 through 500.")
        with self._lock:
            runs = [DeveloperRun.from_dict(item) for item in self._load()["runs"]]
            return tuple(reversed(runs[-limit:]))

    def _resume_approved_command(self, run: DeveloperRun) -> DeveloperRun:
        action = run.pending_action
        if action is None or action.task_id is None or action.approval_id is None:
            return self._fail(run, DeveloperAgentError("Command approval state is incomplete."))
        approval = self.tasks.command_engine.approvals.get(action.approval_id)
        if approval.status == ApprovalStatus.PENDING:
            return run
        task = self.tasks.run(action.task_id, approval_id=action.approval_id)
        if task.status is TaskStatus.CANCELLED:
            denied = replace(action, status=ProposedActionStatus.DENIED)
            return self._replace(
                replace(run, status=DeveloperRunStatus.CANCELLED, pending_action=denied),
                "command_approval_denied",
                command_name=action.command_name,
            )
        return self._observe_and_verify(run, action, task)

    def _observe_and_verify(
        self, run: DeveloperRun, action: ProposedAction, task: TaskRecord
    ) -> DeveloperRun:
        executed = replace(action, status=ProposedActionStatus.EXECUTED)
        history_entry = _history_entry(action, task)
        observed = replace(
            run,
            status=DeveloperRunStatus.READY,
            pending_action=executed,
            last_task_id=task.task_id,
            action_count=run.action_count + 1,
            last_verification_accepted=None,
            last_verification_reason=None,
            recent_history=_append_history(run.recent_history, history_entry),
        )
        observed = self._replace(observed, "action_observed", command_name=action.command_name)
        observation = {
            "command_name": task.command_name,
            "task_id": task.task_id,
            "status": task.status.value,
            "result": task.result,
            "error": task.error,
        }
        response, called = self._generate(observed, ProviderPhase.VERIFY, observation=observation)
        if response is None:
            return called
        try:
            accepted, reason = _parse_verification(response)
            if accepted and task.status is not TaskStatus.SUCCEEDED:
                raise ProviderError("Provider cannot accept verification for an unsuccessful task.")
        except ProviderError as exc:
            return self._fail(called, exc)
        verified = replace(
            called,
            status=DeveloperRunStatus.READY,
            last_verification_accepted=accepted,
            last_verification_reason=reason,
            error=None,
            recent_history=_mark_last_history_verified(
                called.recent_history, accepted=accepted, reason=reason
            ),
        )
        return self._replace(verified, "verification_completed", command_name=action.command_name)

    def _generate(
        self,
        run: DeveloperRun,
        phase: ProviderPhase,
        *,
        observation: dict[str, Any] | None = None,
    ) -> tuple[str | None, DeveloperRun]:
        if run.provider_calls >= run.maximum_provider_calls:
            return None, self._limit(run, "The maximum provider-call count was reached.")
        recent_history = run.recent_history[-3:]
        if phase is ProviderPhase.VERIFY:
            # VERIFY evaluates only the exact current observation. Prior history competes with
            # bounded local-model context and can make the model evaluate an older scan/read or
            # null placeholder instead of the authoritative task result. The finalized entry is
            # restored to recent_history for the next ACT request after verification completes.
            recent_history = ()
        verify_goal = "Verify only the single command result in observation."
        request = ProviderRequest(
            run_id=run.run_id,
            phase=phase,
            # A VERIFY call is deliberately isolated from the broader goal and plan. Small
            # local models otherwise compare a successful file.read observation with a terse
            # project.scan plan step instead of evaluating the command that actually ran.
            goal=verify_goal if phase is ProviderPhase.VERIFY else run.goal,
            plan_summary=None if phase is ProviderPhase.VERIFY else run.plan_summary,
            plan_steps=() if phase is ProviderPhase.VERIFY else run.plan_steps,
            action_number=(
                run.action_count
                if phase is ProviderPhase.VERIFY
                else run.action_count + 1
            ),
            maximum_actions=run.maximum_actions,
            available_commands=self._command_catalog(run, phase),
            observation=observation,
            recent_history=recent_history,
        )
        called = self._replace(
            replace(run, provider_calls=run.provider_calls + 1),
            "provider_called",
        )
        self._provider_calls_in_progress.add(run.run_id)
        response: str | None = None
        provider_error: ProviderError | None = None
        # Provider inference can take minutes on a small local GPU. Reads and cancellation must
        # remain responsive while it runs. The in-progress guard above prevents a second advance,
        # and the durable terminal-state check below discards any response that arrives after a
        # cancellation.
        self._lock.release()
        try:
            response = self.provider.generate(request)
        except ProviderError as exc:
            provider_error = exc
        finally:
            self._lock.acquire()
            self._provider_calls_in_progress.discard(run.run_id)
        latest = self.get(run.run_id)
        if latest.status in _TERMINAL_RUN_STATUSES:
            return None, latest
        if provider_error is not None:
            return None, self._fail(latest, provider_error)
        return response, latest

    def _command_catalog(
        self, run: DeveloperRun, phase: ProviderPhase
    ) -> tuple[ProviderCommand, ...]:
        definitions = self.tasks.command_engine.registry.list_definitions()
        explicit_scope = self._explicit_goal_command_scope(run.goal)
        if explicit_scope is not None:
            # An explicit ONLY-tools list is the user's hard capability boundary for the run.
            # Keep every tool in that boundary available during ACT even if a smaller local
            # model omitted one from its terse PLAN; exact actions are still validated/reviewed.
            allowed = set(explicit_scope)
            definitions = tuple(item for item in definitions if item.name in allowed)
        elif phase is not ProviderPhase.PLAN and run.plan_steps:
            registered = {item.name for item in definitions}
            reviewed = {step for step in run.plan_steps if step in registered}
            if reviewed:
                definitions = tuple(item for item in definitions if item.name in reviewed)
        return tuple(
            ProviderCommand(
                name=item.name,
                description=item.description,
                permission=item.permission,
                risk=item.risk.name.lower(),
                payload_contract=item.payload_contract,
            )
            for item in definitions
        )

    def _explicit_goal_command_scope(self, goal: str) -> tuple[str, ...] | None:
        """Return an exact command allowlist when the user explicitly says to use only listed tools.

        This is intentionally conservative: it activates only for an explicit ONLY-tools phrase
        and reads command names from the immediately following bullet/list block (or the same line).
        The provider then never sees commands outside that scope during PLAN or ACT.
        """
        definitions = self.tasks.command_engine.registry.list_definitions()
        registered = tuple(item.name for item in definitions)
        lines = goal.splitlines()
        trigger_index: int | None = None
        for index, line in enumerate(lines):
            folded = line.casefold()
            if (
                "use only these tools" in folded
                or "using only these tools" in folded
                or "use only the following tools" in folded
                or "using only the following tools" in folded
                or "only these tools" in folded
            ):
                trigger_index = index
                break
        if trigger_index is None:
            return None

        allowed: list[str] = []
        for index in range(trigger_index, len(lines)):
            raw = lines[index]
            stripped = raw.strip()
            if index > trigger_index and not stripped:
                if allowed:
                    break
                continue
            is_list_line = stripped.startswith(("-", "*", "•"))
            if index > trigger_index and allowed and not is_list_line:
                break
            if index > trigger_index and not is_list_line and not allowed:
                continue
            folded = stripped.casefold()
            for name in registered:
                if name.casefold() in folded and name not in allowed:
                    allowed.append(name)
        return tuple(allowed)

    def _validate_goal(self, goal: str) -> str:
        if not isinstance(goal, str) or not goal.strip():
            raise DeveloperAgentError("Developer goal must be a non-empty string.")
        if goal != goal.strip() or "\x00" in goal:
            raise DeveloperAgentError("Developer goal must be trimmed and contain no null bytes.")
        try:
            goal_size = len(goal.encode("utf-8"))
        except UnicodeEncodeError as exc:
            raise DeveloperAgentError("Developer goal must contain valid UTF-8 text.") from exc
        if goal_size > self.maximum_goal_bytes:
            raise DeveloperAgentError("Developer goal exceeds its byte limit.")
        return goal

    def _fail(self, run: DeveloperRun, error: Exception) -> DeveloperRun:
        safe_error = str(error)[:1_000] or type(error).__name__
        self._logger.error(
            "developer_run_failed",
            extra={"run_id": run.run_id, "error_type": type(error).__name__},
        )
        return self._replace(
            replace(run, status=DeveloperRunStatus.FAILED, error=safe_error), "run_failed"
        )

    def _limit(self, run: DeveloperRun, message: str) -> DeveloperRun:
        return self._replace(
            replace(run, status=DeveloperRunStatus.LIMIT_REACHED, error=message),
            "run_limit_reached",
        )

    def _insert(self, run: DeveloperRun, event: str) -> DeveloperRun:
        run = DeveloperRun.from_dict(run.to_dict())
        document = self._load()
        runs = [DeveloperRun.from_dict(item) for item in document["runs"]]
        runs.append(run)
        document["runs"] = [item.to_dict() for item in runs[-self.maximum_runs :]]
        self._append_event(document, run, event)
        self._store.save(document)
        self._logger.info(event, extra={"run_id": run.run_id})
        return run

    def _replace(
        self,
        run: DeveloperRun,
        event: str,
        *,
        command_name: str | None = None,
    ) -> DeveloperRun:
        updated = DeveloperRun.from_dict(
            replace(run, updated_at=self._now()).to_dict()
        )
        document = self._load()
        runs = [DeveloperRun.from_dict(item) for item in document["runs"]]
        for index, existing in enumerate(runs):
            if existing.run_id == updated.run_id:
                runs[index] = updated
                break
        else:
            raise DeveloperAgentError(f"Developer run disappeared: {updated.run_id}")
        document["runs"] = [item.to_dict() for item in runs]
        self._append_event(document, updated, event, command_name=command_name)
        self._store.save(document)
        self._logger.info(
            event,
            extra={"run_id": updated.run_id, "command": command_name},
        )
        return updated

    def _append_event(
        self,
        document: dict[str, Any],
        run: DeveloperRun,
        event: str,
        *,
        command_name: str | None = None,
    ) -> None:
        events = [DeveloperEvent(**item) for item in document["events"]]
        events.append(
            DeveloperEvent(
                event_id=uuid.uuid4().hex,
                run_id=run.run_id,
                event=event,
                timestamp=self._now(),
                status=run.status.value,
                command_name=command_name,
            )
        )
        document["events"] = [item.to_dict() for item in events[-self.maximum_events :]]

    def _ensure_document(self) -> None:
        if not self._store.path.exists():
            self._store.save({"schema_version": 1, "runs": [], "events": []})
        self._load()

    def _load(self) -> dict[str, Any]:
        document = self._store.load()
        if document.get("schema_version") != 1:
            raise DeveloperAgentError("Developer history must use schema_version 1.")
        if not isinstance(document.get("runs"), list) or not isinstance(
            document.get("events"), list
        ):
            raise DeveloperAgentError("Developer history must contain run and event lists.")
        return document

    def _now(self) -> str:
        value = self._clock()
        if value.tzinfo is None:
            raise DeveloperAgentError("Developer-agent clock must return timezone-aware values.")
        return value.astimezone(UTC).isoformat()


def _action_signature(command_name: str, payload: Mapping[str, Any]) -> str:
    """Return a stable signature for bounded loop/no-progress detection."""
    try:
        payload_text = json.dumps(
            dict(payload), ensure_ascii=False, sort_keys=True, separators=(",", ":")
        )
    except (TypeError, ValueError, RecursionError):
        payload_text = repr(dict(payload))
    return command_name + "\x1f" + payload_text


def _no_progress_reason(
    history: tuple[dict[str, Any], ...],
    command_name: str,
    payload: Mapping[str, Any],
    *,
    potentially_mutating_commands: set[str] | None = None,
) -> str | None:
    """Block unchanged successful actions and short cycles before execution.

    An exact action can run again only after an intervening command whose registered risk says it
    may have changed state. This preserves edit -> test -> edit workflows without treating a
    read-only file read as evidence that an earlier project scan has new inputs. A provider's
    rejected verification never authorizes re-running an unchanged deterministic command.
    """
    mutating = potentially_mutating_commands or set()
    successful: list[str] = []
    successful_items: list[dict[str, Any]] = []
    for item in history:
        if (
            item.get("task_status") == TaskStatus.SUCCEEDED.value
            and isinstance(item.get("command_name"), str)
            and isinstance(item.get("payload"), dict)
        ):
            signature = _action_signature(item["command_name"], item["payload"])
            successful.append(signature)
            successful_items.append(item)
    if not successful:
        return None
    proposed = _action_signature(command_name, payload)
    previous_index = next(
        (index for index in range(len(successful) - 1, -1, -1) if successful[index] == proposed),
        None,
    )
    if previous_index is not None and not any(
        item.get("command_name") in mutating
        for item in successful_items[previous_index + 1 :]
    ):
        return (
            "The exact command and payload already succeeded, and no intervening "
            "state-changing command could have changed its inputs."
        )

    # Detect short A/B or A/B/C cycles when every member already succeeded.
    candidate = successful[-6:] + [proposed]
    for period in (2, 3):
        if len(candidate) >= period * 2 and candidate[-period:] == candidate[-2 * period : -period]:
            return f"The proposal would repeat a successful no-progress action cycle of length {period}."
    return None


def _history_entry(action: ProposedAction, task: TaskRecord) -> dict[str, Any]:
    return {
        "command_name": action.command_name,
        "payload": dict(action.payload),
        "task_status": task.status.value,
        "result_preview": _bounded_json_preview(task.result),
        "error": task.error,
        "verification_accepted": None,
        "verification_reason": None,
    }


def _append_history(
    history: tuple[dict[str, Any], ...], item: dict[str, Any]
) -> tuple[dict[str, Any], ...]:
    return tuple((*history, item)[-10:])


def _mark_last_history_verified(
    history: tuple[dict[str, Any], ...], *, accepted: bool, reason: str
) -> tuple[dict[str, Any], ...]:
    if not history:
        return history
    items = [dict(item) for item in history]
    items[-1]["verification_accepted"] = accepted
    items[-1]["verification_reason"] = reason
    return tuple(items)


def _bounded_json_preview(value: Any, maximum_bytes: int = 4096) -> str:
    try:
        text = json.dumps(value, ensure_ascii=False, separators=(",", ":"), default=str)
    except (TypeError, ValueError, RecursionError):
        text = json.dumps({"unavailable": True}, separators=(",", ":"))
    encoded = text.encode("utf-8")
    if len(encoded) <= maximum_bytes:
        return text
    clipped = encoded[: maximum_bytes - 32]
    while clipped:
        try:
            prefix = clipped.decode("utf-8")
            break
        except UnicodeDecodeError:
            clipped = clipped[:-1]
    else:
        prefix = ""
    return prefix + "...[truncated]"


def _load_provider_object(raw: str) -> dict[str, Any]:
    try:
        value = json.loads(
            raw,
            parse_constant=_raise_invalid_constant,
            object_pairs_hook=_reject_duplicate_keys,
        )
    except (json.JSONDecodeError, RecursionError, ValueError) as exc:
        raise ProviderError("Provider response must be strict JSON.") from exc
    if not isinstance(value, dict):
        raise ProviderError("Provider response root must be an object.")
    return value


def _parse_plan(raw: str) -> tuple[str, tuple[str, ...]]:
    value = _load_provider_object(raw)
    if set(value) != {"summary", "steps"}:
        raise ProviderError("Plan response must contain exactly summary and steps.")
    summary = _bounded_text(value["summary"], "plan summary", 4_000)
    steps = value["steps"]
    if not isinstance(steps, list) or not 1 <= len(steps) <= 20:
        raise ProviderError("Plan must contain from 1 through 20 steps.")
    parsed = tuple(_bounded_text(item, "plan step", 1_000) for item in steps)
    return summary, parsed


def _parse_action(raw: str) -> dict[str, Any]:
    value = _load_provider_object(raw)
    kind = value.get("kind")
    if kind == "complete":
        if set(value) != {"kind", "summary"}:
            raise ProviderError("Completion response must contain exactly kind and summary.")
        return {
            "kind": kind,
            "summary": _bounded_text(
                value["summary"], "completion summary", 4_000
            ),
        }
    if kind != "command" or set(value) != {"kind", "command_name", "payload", "rationale"}:
        raise ProviderError(
            "Action response must be an exact command proposal or completion object."
        )
    command_name = _bounded_text(value["command_name"], "command name", 128)
    rationale = _bounded_text(value["rationale"], "action rationale", 2_000)
    if not isinstance(value["payload"], dict):
        raise ProviderError("Proposed command payload must be an object.")
    return {
        "kind": kind,
        "command_name": command_name,
        "payload": value["payload"],
        "rationale": rationale,
    }


def _parse_verification(raw: str) -> tuple[bool, str]:
    value = _load_provider_object(raw)
    if set(value) != {"accepted", "reason"} or not isinstance(value["accepted"], bool):
        raise ProviderError("Verification response must contain exact boolean accepted and reason.")
    return value["accepted"], _bounded_text(value["reason"], "verification reason", 2_000)


def _bounded_text(value: Any, label: str, maximum: int) -> str:
    if not isinstance(value, str) or not value.strip() or value != value.strip():
        raise ProviderError(f"Provider {label} must be a trimmed non-empty string.")
    try:
        size = len(value.encode("utf-8"))
    except UnicodeEncodeError as exc:
        raise ProviderError(f"Provider {label} must contain valid UTF-8 text.") from exc
    if "\x00" in value or size > maximum:
        raise ProviderError(f"Provider {label} exceeds its safe content limit.")
    return value


def _raise_invalid_constant(constant: str) -> None:
    raise ValueError(f"Invalid JSON constant: {constant}")


def _reject_duplicate_keys(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    value: dict[str, Any] = {}
    for key, item in pairs:
        if key in value:
            raise ValueError(f"Duplicate JSON key: {key}")
        value[key] = item
    return value


def _require_boolean(value: bool, name: str) -> None:
    if not isinstance(value, bool):
        raise DeveloperAgentError(f"{name} must be a boolean.")


def _validate_persistent_run_invariants(run: DeveloperRun) -> None:
    if len(run.recent_history) > 10:
        raise DeveloperAgentError("Persistent developer history exceeds its bound.")
    for item in run.recent_history:
        if set(item) != {
            "command_name",
            "payload",
            "task_status",
            "result_preview",
            "error",
            "verification_accepted",
            "verification_reason",
        }:
            raise DeveloperAgentError("Persistent developer history fields are invalid.")
        if (
            not isinstance(item["command_name"], str)
            or not item["command_name"]
            or not isinstance(item["payload"], dict)
            or not isinstance(item["task_status"], str)
            or not isinstance(item["result_preview"], str)
            or len(item["result_preview"].encode("utf-8")) > 4096
            or (item["error"] is not None and not isinstance(item["error"], str))
            or (
                item["verification_accepted"] is not None
                and not isinstance(item["verification_accepted"], bool)
            )
            or (
                item["verification_reason"] is not None
                and not isinstance(item["verification_reason"], str)
            )
        ):
            raise DeveloperAgentError("Persistent developer history content is invalid.")
    planned_statuses = {
        DeveloperRunStatus.AWAITING_PLAN_REVIEW,
        DeveloperRunStatus.READY,
        DeveloperRunStatus.AWAITING_ACTION_REVIEW,
        DeveloperRunStatus.AWAITING_COMMAND_APPROVAL,
        DeveloperRunStatus.SUCCEEDED,
        DeveloperRunStatus.LIMIT_REACHED,
    }
    if run.status in planned_statuses and (
        run.plan_summary is None or not run.plan_steps
    ):
        raise DeveloperAgentError("Persistent reviewed run is missing its plan.")
    if run.status is DeveloperRunStatus.AWAITING_ACTION_REVIEW and (
        run.pending_action is None
        or run.pending_action.status is not ProposedActionStatus.PENDING_REVIEW
    ):
        raise DeveloperAgentError("Persistent action-review state is incomplete.")
    if run.status is DeveloperRunStatus.AWAITING_COMMAND_APPROVAL and (
        run.pending_action is None
        or run.pending_action.status
        is not ProposedActionStatus.AWAITING_COMMAND_APPROVAL
        or run.pending_action.task_id is None
        or run.pending_action.approval_id is None
    ):
        raise DeveloperAgentError("Persistent command-approval state is incomplete.")
    if run.action_count > 0 and run.last_task_id is None:
        raise DeveloperAgentError("Persistent observed run is missing its task identifier.")
    if run.status is DeveloperRunStatus.SUCCEEDED and (
        run.action_count == 0
        or run.last_verification_accepted is not True
        or run.completion_summary is None
    ):
        raise DeveloperAgentError("Persistent completed run lacks verified evidence.")
