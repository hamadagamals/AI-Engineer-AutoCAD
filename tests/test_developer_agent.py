"""Tests for the reviewed, bounded developer-agent state machine."""

from __future__ import annotations

import json
import threading
from collections import deque
from pathlib import Path
from typing import Any, Mapping

import pytest

from agent.command_engine import CommandDefinition, CommandEngine, CommandRegistry
from agent.developer_agent import DeveloperAgent, DeveloperRunStatus
from agent.exceptions import DeveloperAgentError, ProviderError
from agent.permissions import ApprovalManager, PermissionPolicy, RiskLevel
from agent.provider import ProviderAdapter, ProviderPhase, ProviderRequest
from agent.task_engine import TaskEngine
from integrations.provider_contracts import developer_instructions


class ScriptedGenerator:
    """Record requests and return explicit JSON responses in test order."""

    def __init__(self, responses: list[str]) -> None:
        self.responses = deque(responses)
        self.requests: list[ProviderRequest] = []

    def __call__(self, request: ProviderRequest) -> str:
        self.requests.append(request)
        if not self.responses:
            raise RuntimeError("test provider response queue exhausted")
        return self.responses.popleft()


def _validate_value(payload: Mapping[str, Any]) -> dict[str, Any]:
    if set(payload) != {"value"}:
        raise ValueError("payload requires only value")
    value = payload["value"]
    if not isinstance(value, int) or isinstance(value, bool):
        raise ValueError("value must be an integer")
    return {"value": value}


def _build_agent(
    tmp_path: Path,
    responses: list[str],
    *,
    high_risk: bool = False,
    provider: ProviderAdapter | None = None,
) -> tuple[DeveloperAgent, ScriptedGenerator, ApprovalManager, TaskEngine]:
    registry = CommandRegistry()
    registry.register(
        CommandDefinition(
            "demo.execute",
            "Execute one deterministic operation.",
            "demo.run",
            RiskLevel.HIGH if high_risk else RiskLevel.LOW,
            _validate_value,
            lambda payload, context: {"answer": payload["value"] * 2},
            payload_contract='{"value":"<integer>"}',
        )
    )
    approvals = ApprovalManager(tmp_path / "approvals.json")
    command_engine = CommandEngine(
        registry, PermissionPolicy(frozenset({"demo.run"})), approvals
    )
    tasks = TaskEngine(command_engine, tmp_path / "tasks.json")
    scripted = ScriptedGenerator(responses)
    selected_provider = provider or ProviderAdapter("test-provider", scripted)
    agent = DeveloperAgent(selected_provider, tasks, tmp_path / "developer.json")
    return agent, scripted, approvals, tasks


def _plan() -> str:
    return json.dumps(
        {
            "summary": "Inspect and verify safely.",
            "steps": ["Run the bounded command."],
        }
    )


def _action(value: int = 3) -> str:
    return json.dumps(
        {
            "kind": "command",
            "command_name": "demo.execute",
            "payload": {"value": value},
            "rationale": "Collect a deterministic observation.",
        }
    )


def _verification(accepted: bool = True) -> str:
    return json.dumps(
        {"accepted": accepted, "reason": "The observed result matches the expected value."}
    )


def _completion() -> str:
    return json.dumps({"kind": "complete", "summary": "The goal was verified."})


def test_provider_adapter_validates_identity_output_and_redacts_callback() -> None:
    adapter = ProviderAdapter("safe-provider", lambda request: "{}")

    def invalid_output(request: ProviderRequest) -> Any:
        return 1

    assert adapter.name == "safe-provider"
    assert "lambda" not in repr(adapter)
    with pytest.raises(ProviderError, match="name"):
        ProviderAdapter("Unsafe Name", lambda request: "{}")
    with pytest.raises(ProviderError, match="JSON string"):
        ProviderAdapter("bad-output", invalid_output).generate(
            ProviderRequest("run", ProviderPhase.PLAN, "goal", None, (), 1, 1, ())
        )
    with pytest.raises(ProviderError, match="UTF-8"):
        ProviderAdapter("bad-unicode", lambda request: "\ud800").generate(
            ProviderRequest("run", ProviderPhase.PLAN, "goal", None, (), 1, 1, ())
        )


def test_act_contract_forbids_exact_policy_blocked_action() -> None:
    request = ProviderRequest(
        run_id="run",
        phase=ProviderPhase.ACT,
        goal="Inspect safely.",
        plan_summary="Inspect.",
        plan_steps=("file.read",),
        action_number=2,
        maximum_actions=10,
        available_commands=(),
        observation={
            "kind": "policy_feedback",
            "blocked_command_name": "file.read",
            "blocked_payload": {"path": "agent/command_engine.py"},
        },
    )

    instructions = developer_instructions(request)

    assert (
        "exact blocked_command_name plus blocked_payload combination is forbidden"
        in instructions
    )
    assert "keep the command but change its payload to a different concrete" in instructions


def test_verify_contract_separates_command_success_from_goal_completion() -> None:
    request = ProviderRequest(
        run_id="run",
        phase=ProviderPhase.VERIFY,
        goal="Report the complete architecture.",
        plan_summary="Inspect files.",
        plan_steps=("project.scan", "file.read"),
        action_number=2,
        maximum_actions=10,
        available_commands=(),
        observation={
            "command_name": "file.read",
            "status": "succeeded",
            "result": {"path": "agent/app.py", "content": "bounded"},
        },
    )

    instructions = developer_instructions(request)

    assert "Do not require one observation to satisfy the overall user goal" in instructions
    assert "succeeded file.read with a path and content verifies the read" in instructions


def test_low_risk_run_requires_plan_and_exact_action_review(tmp_path: Path) -> None:
    agent, scripted, _, tasks = _build_agent(
        tmp_path, [_plan(), _action(), _verification(), _completion()]
    )

    planned = agent.create_run("Perform a bounded demonstration.", maximum_actions=1)
    assert planned.status is DeveloperRunStatus.AWAITING_PLAN_REVIEW
    assert planned.plan_steps == ("Run the bounded command.",)
    assert tasks.list_tasks() == ()

    ready = agent.review_plan(planned.run_id, approved=True)
    proposed = agent.advance(ready.run_id)
    assert proposed.status is DeveloperRunStatus.AWAITING_ACTION_REVIEW
    assert proposed.pending_action is not None
    assert proposed.pending_action.payload == {"value": 3}
    assert tasks.list_tasks() == ()

    verified = agent.review_action(proposed.run_id, approved=True)
    assert verified.status is DeveloperRunStatus.READY
    assert verified.action_count == 1
    assert verified.last_verification_accepted is True
    assert tasks.list_tasks()[0].result == {"answer": 6}

    completed = agent.advance(verified.run_id)
    assert completed.status is DeveloperRunStatus.SUCCEEDED
    assert completed.completion_summary == "The goal was verified."
    assert [request.phase for request in scripted.requests] == [
        ProviderPhase.PLAN,
        ProviderPhase.ACT,
        ProviderPhase.VERIFY,
        ProviderPhase.ACT,
    ]
    assert scripted.requests[2].observation == {
        "command_name": "demo.execute",
        "task_id": completed.last_task_id,
        "status": "succeeded",
        "result": {"answer": 6},
        "error": None,
    }
    assert scripted.requests[2].recent_history == ()
    assert scripted.requests[2].action_number == 1
    assert scripted.requests[2].goal == "Verify only the single command result in observation."
    assert scripted.requests[2].plan_summary is None
    assert scripted.requests[2].plan_steps == ()
    assert scripted.requests[3].recent_history[-1]["command_name"] == "demo.execute"
    assert '"answer":6' in scripted.requests[3].recent_history[-1]["result_preview"]
    assert scripted.requests[3].recent_history[-1]["verification_accepted"] is True

    reloaded = DeveloperAgent(
        agent.provider, tasks, tmp_path / "developer.json"
    ).get(completed.run_id)
    assert reloaded.status is DeveloperRunStatus.SUCCEEDED
    document = json.loads((tmp_path / "developer.json").read_text(encoding="utf-8"))
    assert "Perform a bounded demonstration." not in json.dumps(document["events"])
    assert {event["event"] for event in document["events"]} >= {
        "plan_proposed",
        "action_proposed",
        "action_observed",
        "verification_completed",
        "run_completed",
    }


def test_cancel_remains_responsive_during_provider_call_and_discards_response(
    tmp_path: Path,
) -> None:
    started = threading.Event()
    release = threading.Event()
    responses = deque([_plan(), _action()])

    def blocking_provider(request: ProviderRequest) -> str:
        response = responses.popleft()
        if request.phase is ProviderPhase.ACT:
            started.set()
            assert release.wait(timeout=5)
        return response

    agent, _, _, tasks = _build_agent(
        tmp_path,
        [],
        provider=ProviderAdapter("blocking-provider", blocking_provider),
    )
    planned = agent.create_run("Allow cancellation during local inference.")
    agent.review_plan(planned.run_id, approved=True)
    advanced: list[Any] = []
    worker = threading.Thread(target=lambda: advanced.append(agent.advance(planned.run_id)))
    worker.start()
    assert started.wait(timeout=5)
    assert agent.get(planned.run_id).status is DeveloperRunStatus.READY
    with pytest.raises(DeveloperAgentError, match="already in progress"):
        agent.advance(planned.run_id)

    cancelled = agent.cancel(planned.run_id)
    release.set()
    worker.join(timeout=5)

    assert not worker.is_alive()
    assert cancelled.status is DeveloperRunStatus.CANCELLED
    assert advanced[0].status is DeveloperRunStatus.CANCELLED
    assert agent.get(planned.run_id).status is DeveloperRunStatus.CANCELLED
    assert tasks.list_tasks() == ()


def test_plan_and_action_denials_cancel_without_execution(tmp_path: Path) -> None:
    plan_agent, _, _, plan_tasks = _build_agent(tmp_path / "plan", [_plan()])
    planned = plan_agent.create_run("Review this plan.")
    denied_plan = plan_agent.review_plan(planned.run_id, approved=False)
    assert denied_plan.status is DeveloperRunStatus.CANCELLED
    assert plan_tasks.list_tasks() == ()

    action_agent, _, _, action_tasks = _build_agent(
        tmp_path / "action", [_plan(), _action()]
    )
    planned = action_agent.create_run("Review this action.")
    action_agent.review_plan(planned.run_id, approved=True)
    proposed = action_agent.advance(planned.run_id)
    denied_action = action_agent.review_action(proposed.run_id, approved=False)
    assert denied_action.status is DeveloperRunStatus.CANCELLED
    assert action_tasks.list_tasks() == ()


def test_high_risk_action_retains_separate_durable_approval(tmp_path: Path) -> None:
    agent, scripted, approvals, tasks = _build_agent(
        tmp_path,
        [_plan(), _action(4), _verification(), _completion()],
        high_risk=True,
    )
    run = agent.create_run("Run a reviewed high-risk operation.", maximum_actions=1)
    agent.review_plan(run.run_id, approved=True)
    proposed = agent.advance(run.run_id)
    awaiting = agent.review_action(proposed.run_id, approved=True)

    assert awaiting.status is DeveloperRunStatus.AWAITING_COMMAND_APPROVAL
    assert awaiting.pending_action is not None
    assert awaiting.pending_action.approval_id is not None
    assert len(scripted.requests) == 2
    assert agent.advance(run.run_id).status is DeveloperRunStatus.AWAITING_COMMAND_APPROVAL
    assert len(scripted.requests) == 2

    approvals.decide(awaiting.pending_action.approval_id, approved=True)
    verified = agent.advance(run.run_id)
    assert verified.status is DeveloperRunStatus.READY
    assert verified.last_verification_accepted is True
    assert tasks.get(verified.last_task_id or "").result == {"answer": 8}
    assert agent.advance(run.run_id).status is DeveloperRunStatus.SUCCEEDED


def test_denied_command_approval_cancels_run(tmp_path: Path) -> None:
    agent, _, approvals, _ = _build_agent(
        tmp_path, [_plan(), _action()], high_risk=True
    )
    run = agent.create_run("Do not bypass approval.")
    agent.review_plan(run.run_id, approved=True)
    proposed = agent.advance(run.run_id)
    awaiting = agent.review_action(proposed.run_id, approved=True)
    assert awaiting.pending_action is not None
    assert awaiting.pending_action.approval_id is not None
    approvals.decide(awaiting.pending_action.approval_id, approved=False)

    cancelled = agent.advance(run.run_id)

    assert cancelled.status is DeveloperRunStatus.CANCELLED


@pytest.mark.parametrize(
    "response",
    [
        "not-json",
        json.dumps({"summary": "Missing steps"}),
        json.dumps({"summary": "Plan", "steps": [], "extra": True}),
        json.dumps({"summary": "Plan", "steps": [" okay "]}),
        '{"summary":"one","summary":"two","steps":["step"]}',
    ],
)
def test_invalid_plan_fails_closed_and_persists_safe_error(
    tmp_path: Path, response: str
) -> None:
    agent, _, _, tasks = _build_agent(tmp_path, [response])

    failed = agent.create_run("Reject malformed provider output.")

    assert failed.status is DeveloperRunStatus.FAILED
    assert tasks.list_tasks() == ()
    assert failed.error is not None
    assert response not in failed.error


def test_provider_exception_does_not_persist_sensitive_detail(tmp_path: Path) -> None:
    def explode(request: ProviderRequest) -> str:
        raise RuntimeError("secret-provider-key")

    registry = CommandRegistry()
    approvals = ApprovalManager(tmp_path / "approvals.json")
    tasks = TaskEngine(
        CommandEngine(registry, PermissionPolicy(frozenset()), approvals),
        tmp_path / "tasks.json",
    )
    agent = DeveloperAgent(
        ProviderAdapter("failing-provider", explode), tasks, tmp_path / "developer.json"
    )

    failed = agent.create_run("Handle provider failure safely.")

    assert failed.status is DeveloperRunStatus.FAILED
    assert "RuntimeError" in (failed.error or "")
    assert "secret-provider-key" not in (failed.error or "")
    assert "secret-provider-key" not in (tmp_path / "developer.json").read_text(encoding="utf-8")


def test_unknown_command_and_premature_completion_fail_closed(tmp_path: Path) -> None:
    unknown = json.dumps(
        {
            "kind": "command",
            "command_name": "terminal.unrestricted",
            "payload": {},
            "rationale": "Attempt an unavailable command.",
        }
    )
    agent, _, _, tasks = _build_agent(tmp_path / "unknown", [_plan(), unknown])
    run = agent.create_run("Reject unknown commands.")
    agent.review_plan(run.run_id, approved=True)
    failed = agent.advance(run.run_id)
    assert failed.status is DeveloperRunStatus.FAILED
    assert tasks.list_tasks() == ()

    early, _, _, _ = _build_agent(tmp_path / "early", [_plan(), _completion()])
    run = early.create_run("Do not claim success without evidence.")
    early.review_plan(run.run_id, approved=True)
    assert early.advance(run.run_id).status is DeveloperRunStatus.FAILED


def test_action_limit_stops_additional_provider_commands(tmp_path: Path) -> None:
    agent, _, _, _ = _build_agent(
        tmp_path, [_plan(), _action(), _verification(False), _action(9)]
    )
    run = agent.create_run("Stop at the configured action bound.", maximum_actions=1)
    agent.review_plan(run.run_id, approved=True)
    proposed = agent.advance(run.run_id)
    verified = agent.review_action(proposed.run_id, approved=True)
    assert verified.last_verification_accepted is False

    limited = agent.advance(run.run_id)

    assert limited.status is DeveloperRunStatus.LIMIT_REACHED
    assert limited.action_count == 1


def test_cancel_and_invalid_persistent_document_are_enforced(tmp_path: Path) -> None:
    agent, _, _, _ = _build_agent(tmp_path, [_plan()])
    run = agent.create_run("Cancel this reviewed run.")
    cancelled = agent.cancel(run.run_id)
    assert cancelled.status is DeveloperRunStatus.CANCELLED
    with pytest.raises(DeveloperAgentError, match="terminal"):
        agent.cancel(run.run_id)

    path = tmp_path / "developer.json"
    document = json.loads(path.read_text(encoding="utf-8"))
    document["runs"][0]["provider_calls"] = True
    path.write_text(json.dumps(document), encoding="utf-8")
    with pytest.raises(DeveloperAgentError, match="counters"):
        agent.get(run.run_id)


def test_reviewed_exact_command_steps_constrain_action_catalog(tmp_path: Path) -> None:
    registry = CommandRegistry()
    registry.register(
        CommandDefinition(
            "demo.first",
            "First safe command.",
            "demo.run",
            RiskLevel.LOW,
            _validate_value,
            lambda payload, context: payload,
            payload_contract='{"value":"<integer>"}',
        )
    )
    registry.register(
        CommandDefinition(
            "demo.second",
            "Second safe command.",
            "demo.run",
            RiskLevel.LOW,
            _validate_value,
            lambda payload, context: payload,
            payload_contract='{"value":"<integer>"}',
        )
    )
    approvals = ApprovalManager(tmp_path / "approvals.json")
    tasks = TaskEngine(
        CommandEngine(registry, PermissionPolicy(frozenset({"demo.run"})), approvals),
        tmp_path / "tasks.json",
    )
    scripted = ScriptedGenerator(
        [
            json.dumps({"summary": "Use only the first tool.", "steps": ["demo.first"]}),
            json.dumps({
                "kind": "command",
                "command_name": "demo.first",
                "payload": {"value": 1},
                "rationale": "Use the reviewed tool.",
            }),
        ]
    )
    agent = DeveloperAgent(
        ProviderAdapter("test-provider", scripted), tasks, tmp_path / "developer.json"
    )
    run = agent.create_run("Use one reviewed tool only.")
    agent.review_plan(run.run_id, approved=True)
    agent.advance(run.run_id)

    assert {item.name for item in scripted.requests[0].available_commands} == {
        "demo.first",
        "demo.second",
    }
    assert [item.name for item in scripted.requests[1].available_commands] == ["demo.first"]
    assert scripted.requests[1].available_commands[0].payload_contract == '{"value":"<integer>"}'


def test_explicit_only_tools_scope_filters_plan_catalog_and_rejects_escape(tmp_path: Path) -> None:
    registry = CommandRegistry()
    registry.register(
        CommandDefinition(
            "project.scan",
            "Scan project.",
            "project.read",
            RiskLevel.LOW,
            _validate_value,
            lambda payload, context: payload,
            payload_contract='{"value":"<integer>"}',
        )
    )
    registry.register(
        CommandDefinition(
            "file.read",
            "Read file.",
            "file.read",
            RiskLevel.LOW,
            _validate_value,
            lambda payload, context: payload,
            payload_contract='{"value":"<integer>"}',
        )
    )
    registry.register(
        CommandDefinition(
            "autocad.detect",
            "Detect AutoCAD.",
            "autocad.read",
            RiskLevel.LOW,
            _validate_value,
            lambda payload, context: payload,
            payload_contract='{"value":"<integer>"}',
        )
    )
    approvals = ApprovalManager(tmp_path / "approvals.json")
    tasks = TaskEngine(
        CommandEngine(
            registry,
            PermissionPolicy(frozenset({"project.read", "file.read", "autocad.read"})),
            approvals,
        ),
        tmp_path / "tasks.json",
    )
    scripted = ScriptedGenerator(
        [json.dumps({"summary": "Inspect safely.", "steps": ["project.scan", "autocad.detect"]})]
    )
    agent = DeveloperAgent(
        ProviderAdapter("test-provider", scripted), tasks, tmp_path / "developer.json"
    )
    goal = """Inspect the project using ONLY these tools:\n- project.scan\n- file.read\n\nDo not use anything else."""
    run = agent.create_run(goal)

    assert [item.name for item in scripted.requests[0].available_commands] == [
        "file.read",
        "project.scan",
    ] or [item.name for item in scripted.requests[0].available_commands] == [
        "project.scan",
        "file.read",
    ]
    assert run.status is DeveloperRunStatus.FAILED
    assert run.error is not None
    assert "explicit user tool scope" in run.error


def test_explicit_only_tools_scope_accepts_exact_plan(tmp_path: Path) -> None:
    registry = CommandRegistry()
    for name, permission in (("project.scan", "project.read"), ("file.read", "file.read"), ("autocad.detect", "autocad.read")):
        registry.register(
            CommandDefinition(
                name,
                "Safe command.",
                permission,
                RiskLevel.LOW,
                _validate_value,
                lambda payload, context: payload,
                payload_contract='{"value":"<integer>"}',
            )
        )
    approvals = ApprovalManager(tmp_path / "approvals.json")
    tasks = TaskEngine(
        CommandEngine(
            registry,
            PermissionPolicy(frozenset({"project.read", "file.read", "autocad.read"})),
            approvals,
        ),
        tmp_path / "tasks.json",
    )
    scripted = ScriptedGenerator(
        [json.dumps({"summary": "Inspect safely.", "steps": ["project.scan", "file.read"]})]
    )
    agent = DeveloperAgent(
        ProviderAdapter("test-provider", scripted), tasks, tmp_path / "developer.json"
    )
    goal = """Inspect the project using ONLY these tools:\n- project.scan\n- file.read\n\nDo not use anything else."""
    run = agent.create_run(goal)

    assert run.status is DeveloperRunStatus.AWAITING_PLAN_REVIEW
    assert run.plan_steps == ("project.scan", "file.read")


def test_verified_duplicate_action_is_blocked_and_provider_is_reasked(tmp_path: Path) -> None:
    agent, scripted, _, tasks = _build_agent(
        tmp_path,
        [
            _plan(),
            _action(3),
            _verification(True),
            _action(3),
            _completion(),
        ],
    )
    run = agent.create_run("Complete one verified demonstration.", maximum_actions=3)
    agent.review_plan(run.run_id, approved=True)
    proposed = agent.advance(run.run_id)
    verified = agent.review_action(proposed.run_id, approved=True)
    assert verified.action_count == 1
    assert verified.last_verification_accepted is True

    completed = agent.advance(run.run_id)

    assert completed.status is DeveloperRunStatus.SUCCEEDED
    assert completed.action_count == 1
    assert len(tasks.list_tasks()) == 1
    assert [request.phase for request in scripted.requests] == [
        ProviderPhase.PLAN,
        ProviderPhase.ACT,
        ProviderPhase.VERIFY,
        ProviderPhase.ACT,
        ProviderPhase.ACT,
    ]
    feedback = scripted.requests[-1].observation
    assert feedback is not None
    assert feedback["kind"] == "policy_feedback"
    assert feedback["blocked_command_name"] == "demo.execute"


def test_successful_duplicate_is_blocked_after_provider_rejects_verification(
    tmp_path: Path,
) -> None:
    agent, scripted, _, tasks = _build_agent(
        tmp_path,
        [
            _plan(),
            _action(3),
            _verification(False),
            _action(3),
            _action(4),
        ],
    )
    run = agent.create_run("Use a different useful step after rejected verification.")
    agent.review_plan(run.run_id, approved=True)
    proposed = agent.advance(run.run_id)
    unverified = agent.review_action(proposed.run_id, approved=True)
    assert unverified.last_verification_accepted is False

    next_action = agent.advance(run.run_id)

    assert next_action.status is DeveloperRunStatus.AWAITING_ACTION_REVIEW
    assert next_action.pending_action is not None
    assert next_action.pending_action.payload == {"value": 4}
    assert len(tasks.list_tasks()) == 1
    feedback = scripted.requests[-1].observation
    assert feedback is not None
    assert feedback["kind"] == "policy_feedback"
    assert feedback["blocked_payload"] == {"value": 3}
    assert "already succeeded" in feedback["reason"]


def test_last_verified_action_is_not_repeated_after_unverified_intervening_action(
    tmp_path: Path,
) -> None:
    agent, scripted, _, tasks = _build_agent(
        tmp_path,
        [
            _plan(),
            _action(3),
            _verification(True),
            _action(4),
            _verification(False),
            _action(3),
            _action(5),
        ],
    )
    run = agent.create_run("Do not revisit verified evidence without verified progress.")
    agent.review_plan(run.run_id, approved=True)
    first = agent.advance(run.run_id)
    agent.review_action(first.run_id, approved=True)
    second = agent.advance(run.run_id)
    unverified = agent.review_action(second.run_id, approved=True)
    assert unverified.last_verification_accepted is False

    next_action = agent.advance(run.run_id)

    assert next_action.status is DeveloperRunStatus.AWAITING_ACTION_REVIEW
    assert next_action.pending_action is not None
    assert next_action.pending_action.payload == {"value": 5}
    assert len(tasks.list_tasks()) == 2
    feedback = scripted.requests[-1].observation
    assert feedback is not None
    assert feedback["blocked_payload"] == {"value": 3}
    assert "no intervening state-changing command" in feedback["reason"]


def test_explicit_tool_scope_stays_available_during_act_even_if_plan_is_terse(
    tmp_path: Path,
) -> None:
    registry = CommandRegistry()
    registry.register(
        CommandDefinition(
            "project.scan",
            "Scan project.",
            "project.read",
            RiskLevel.LOW,
            _validate_value,
            lambda payload, context: payload,
            payload_contract='{"value":"<integer>"}',
        )
    )
    registry.register(
        CommandDefinition(
            "file.read",
            "Read file.",
            "file.read",
            RiskLevel.LOW,
            _validate_value,
            lambda payload, context: payload,
            payload_contract='{"value":"<integer>"}',
        )
    )
    registry.register(
        CommandDefinition(
            "autocad.detect",
            "Detect AutoCAD.",
            "autocad.read",
            RiskLevel.LOW,
            _validate_value,
            lambda payload, context: payload,
            payload_contract='{"value":"<integer>"}',
        )
    )
    approvals = ApprovalManager(tmp_path / "approvals.json")
    tasks = TaskEngine(
        CommandEngine(
            registry,
            PermissionPolicy(frozenset({"project.read", "file.read", "autocad.read"})),
            approvals,
        ),
        tmp_path / "tasks.json",
    )
    scripted = ScriptedGenerator(
        [
            json.dumps({"summary": "Inspect safely.", "steps": ["project.scan"]}),
            json.dumps(
                {
                    "kind": "command",
                    "command_name": "file.read",
                    "payload": {"value": 1},
                    "rationale": "Read a discovered project file.",
                }
            ),
        ]
    )
    agent = DeveloperAgent(
        ProviderAdapter("test-provider", scripted), tasks, tmp_path / "developer.json"
    )
    goal = """Inspect using ONLY these tools:\n- project.scan\n- file.read\n\nDo not use anything else."""
    run = agent.create_run(goal)
    assert run.status is DeveloperRunStatus.AWAITING_PLAN_REVIEW
    agent.review_plan(run.run_id, approved=True)

    proposed = agent.advance(run.run_id)

    assert proposed.status is DeveloperRunStatus.AWAITING_ACTION_REVIEW
    assert proposed.pending_action is not None
    assert proposed.pending_action.command_name == "file.read"
    assert {item.name for item in scripted.requests[-1].available_commands} == {
        "project.scan",
        "file.read",
    }


def test_action_outside_reviewed_scope_fails_before_submission(tmp_path: Path) -> None:
    registry = CommandRegistry()
    for name in ("demo.first", "demo.second"):
        registry.register(
            CommandDefinition(
                name,
                "Safe command.",
                "demo.run",
                RiskLevel.LOW,
                _validate_value,
                lambda payload, context: payload,
                payload_contract='{"value":"<integer>"}',
            )
        )
    approvals = ApprovalManager(tmp_path / "approvals.json")
    tasks = TaskEngine(
        CommandEngine(registry, PermissionPolicy(frozenset({"demo.run"})), approvals),
        tmp_path / "tasks.json",
    )
    scripted = ScriptedGenerator(
        [
            json.dumps({"summary": "Use first only.", "steps": ["demo.first"]}),
            json.dumps(
                {
                    "kind": "command",
                    "command_name": "demo.second",
                    "payload": {"value": 1},
                    "rationale": "Try to escape the reviewed plan.",
                }
            ),
        ]
    )
    agent = DeveloperAgent(
        ProviderAdapter("test-provider", scripted), tasks, tmp_path / "developer.json"
    )
    run = agent.create_run("Use the reviewed plan.")
    agent.review_plan(run.run_id, approved=True)

    failed = agent.advance(run.run_id)

    assert failed.status is DeveloperRunStatus.FAILED
    assert failed.error is not None
    assert "reviewed/user command scope" in failed.error
    assert tasks.list_tasks() == ()
