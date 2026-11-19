"""Tests for bounded ownership, expiry, rate, and concurrency of copilot sessions."""

from __future__ import annotations

from collections import deque
from datetime import UTC, datetime, timedelta

import pytest
import json

from agent.copilot import (
    ContextBinding,
    ContextScope,
    ContextSummary,
    CopilotLanguage,
    SessionStatus,
    create_egress_field,
)
from agent.copilot_provider import CopilotProviderAdapter
from agent.copilot_service import CopilotSessionManager
from agent.exceptions import CopilotContractError, ResourceNotFoundError
from core.electrical_model import PrivacyClassification


class MutableClock:
    def __init__(self) -> None:
        self.value = datetime(2026, 8, 6, 12, 0, tzinfo=UTC)

    def __call__(self) -> datetime:
        return self.value


def _manager(
    clock: MutableClock,
    *,
    maximum_active_sessions: int = 8,
    maximum_operations_per_minute: int = 30,
    provider: CopilotProviderAdapter | None = None,
) -> CopilotSessionManager:
    identifiers = deque(f"{value:032x}" for value in range(1, 20))
    keys = deque(("owner-key-" + str(value) + "x" * 40) for value in range(1, 10))
    return CopilotSessionManager(
        clock=clock,
        id_factory=identifiers.popleft,
        key_factory=keys.popleft,
        maximum_active_sessions=maximum_active_sessions,
        maximum_operations_per_minute=maximum_operations_per_minute,
        provider=provider,
    )


def _create(manager: CopilotSessionManager, *, ttl_minutes: int = 60):
    return manager.create_session(
        project_id="project:test",
        context=ContextBinding("a" * 32, "b" * 64, ContextScope.SELECTION),
        context_summary=ContextSummary(1, 1, 0, 1, 0, 0, False, 0),
        language=CopilotLanguage.ARABIC,
        ttl_minutes=ttl_minutes,
    )


def test_owner_key_is_required_and_not_stored_in_session_contract() -> None:
    clock = MutableClock()
    manager = _manager(clock)
    created = _create(manager)

    assert manager.get_session(created.session.session_id, created.owner_key) == created.session
    assert "owner" not in created.session.to_dict()
    with pytest.raises(ResourceNotFoundError):
        manager.get_session(created.session.session_id, "wrong-key" + "x" * 40)


def test_session_expiry_and_concurrent_active_limit_fail_closed() -> None:
    clock = MutableClock()
    manager = _manager(clock, maximum_active_sessions=1)
    first = _create(manager, ttl_minutes=5)
    with pytest.raises(CopilotContractError, match="Concurrent"):
        _create(manager)

    clock.value += timedelta(minutes=5)
    with pytest.raises(CopilotContractError, match="expired"):
        manager.get_session(first.session.session_id, first.owner_key)
    second = _create(manager)
    assert second.session.status is SessionStatus.ACTIVE


def test_per_session_rate_limit_and_cancellation_are_enforced() -> None:
    clock = MutableClock()
    manager = _manager(clock, maximum_operations_per_minute=2)
    created = _create(manager)
    manager.get_session(created.session.session_id, created.owner_key)
    manager.get_session(created.session.session_id, created.owner_key)
    with pytest.raises(CopilotContractError, match="rate limit"):
        manager.get_session(created.session.session_id, created.owner_key)

    clock.value += timedelta(minutes=1, seconds=1)
    cancelled = manager.cancel_session(created.session.session_id, created.owner_key)
    assert cancelled.status is SessionStatus.CANCELLED
    with pytest.raises(CopilotContractError, match="active session"):
        manager.add_engineer_message(
            created.session.session_id,
            created.owner_key,
            language=CopilotLanguage.ENGLISH,
            content="Inspect selection.",
        )


def test_provider_requires_one_approved_review_and_records_read_only_answer() -> None:
    clock = MutableClock()
    calls = []

    def generate(request):
        calls.append(request)
        return json.dumps(
            {
                "language": "ar",
                "answer": "يوجد مسار واحد في الملخص الموافق عليه.",
                "citations": [
                    {"source_field": "context.summary", "title": "ملخص السياق"}
                ],
            },
            ensure_ascii=False,
        )

    manager = _manager(
        clock, provider=CopilotProviderAdapter("provider:test", generate)
    )
    created = _create(manager)
    review = manager.create_review(
        created.session.session_id,
        created.owner_key,
        destination="provider:test",
        privacy=PrivacyClassification.INTERNAL,
        fields=(create_egress_field("context.summary", "route_count=1"),),
    )
    with pytest.raises(CopilotContractError, match="approved"):
        manager.request_answer(
            created.session.session_id, created.owner_key, review.review_id
        )
    approved = manager.decide_review(
        created.session.session_id,
        created.owner_key,
        review.review_id,
        approved=True,
    )

    session, answer = manager.request_answer(
        created.session.session_id, created.owner_key, approved.review_id
    )

    assert answer.data_egress_performed is True
    assert answer.drawing_write_performed is False
    assert session.messages[-1].role.value == "copilot"
    assert manager.get_answer(
        created.session.session_id, created.owner_key, answer.answer_id
    ) == answer
    assert len(calls) == 1
    with pytest.raises(CopilotContractError, match="already been used"):
        manager.request_answer(
            created.session.session_id, created.owner_key, approved.review_id
        )
