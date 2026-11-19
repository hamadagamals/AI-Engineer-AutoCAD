"""Tests for immutable provider-independent local copilot contracts."""

from __future__ import annotations

from dataclasses import replace

import pytest

from agent.copilot import (
    ContextBinding,
    ContextScope,
    ContextSummary,
    CopilotLanguage,
    CopilotMessage,
    CopilotSession,
    DataEgressReview,
    EgressReviewStatus,
    MessageRole,
    SessionStatus,
    append_message,
    create_egress_field,
    create_egress_review,
    decide_egress_review,
)
from agent.exceptions import CopilotContractError
from core.electrical_model import PrivacyClassification


SESSION_ID = "a" * 32
CONTEXT_ID = "b" * 32


def _session(*, maximum_messages: int = 64) -> CopilotSession:
    return CopilotSession(
        schema_version=1,
        session_id=SESSION_ID,
        project_id="project:test",
        context=ContextBinding(CONTEXT_ID, "c" * 64, ContextScope.SELECTION),
        context_summary=ContextSummary(3, 2, 1, 1, 1, 1, False, 0),
        language=CopilotLanguage.ARABIC,
        status=SessionStatus.ACTIVE,
        created_at="2026-08-06T12:00:00Z",
        updated_at="2026-08-06T12:00:00Z",
        expires_at="2026-08-06T13:00:00Z",
        messages=(),
        maximum_messages=maximum_messages,
    )


def _message(
    content: str = "راجع العناصر المحددة فقط.",
    *,
    message_id: str = "d" * 32,
    context_id: str = CONTEXT_ID,
) -> CopilotMessage:
    return CopilotMessage(
        message_id=message_id,
        session_id=SESSION_ID,
        context_id=context_id,
        role=MessageRole.ENGINEER,
        language=CopilotLanguage.ARABIC,
        content=content,
        created_at="2026-08-06T12:01:00Z",
    )


def test_session_is_immutable_provider_independent_and_json_serializable() -> None:
    session = append_message(
        _session(), _message(), updated_at="2026-08-06T12:01:00Z"
    )

    value = session.to_dict()

    assert value["language"] == "ar"
    assert value["context"]["scope"] == "selection"
    assert value["messages"][0]["content"] == "راجع العناصر المحددة فقط."
    assert value["drawing_write_allowed"] is False
    assert "provider" not in value
    with pytest.raises(AttributeError):
        session.status = SessionStatus.COMPLETED  # type: ignore[misc]


def test_general_scope_is_explicit_and_retains_no_drawing_authority() -> None:
    session = replace(
        _session(),
        project_id="autocad.general",
        context=ContextBinding(CONTEXT_ID, "c" * 64, ContextScope.GENERAL),
        context_summary=ContextSummary(0, 0, 0, 0, 0, 0, False, 0),
    )

    value = session.to_dict()

    assert value["context"]["scope"] == "general"
    assert value["context_summary"]["entity_count"] == 0
    assert value["drawing_write_allowed"] is False


def test_session_rejects_write_capability_and_mutable_history() -> None:
    with pytest.raises(CopilotContractError, match="drawing writes"):
        replace(_session(), drawing_write_allowed=True)
    with pytest.raises(CopilotContractError, match="immutable tuple"):
        replace(_session(), messages=[])  # type: ignore[arg-type]


def test_session_enforces_context_identity_chronology_and_unique_messages() -> None:
    with pytest.raises(CopilotContractError, match="different context"):
        append_message(
            _session(),
            _message(context_id="e" * 32),
            updated_at="2026-08-06T12:01:00Z",
        )
    one = append_message(_session(), _message(), updated_at="2026-08-06T12:01:00Z")
    with pytest.raises(CopilotContractError, match="unique"):
        replace(one, messages=one.messages + (one.messages[0],))
    with pytest.raises(CopilotContractError, match="timestamps"):
        replace(_session(), expires_at="2026-08-06T11:00:00Z")


def test_session_enforces_message_count_and_utf8_byte_limits() -> None:
    one = append_message(
        _session(maximum_messages=1),
        _message(),
        updated_at="2026-08-06T12:01:00Z",
    )
    second = replace(_message(message_id="e" * 32), created_at="2026-08-06T12:02:00Z")
    with pytest.raises(CopilotContractError, match="message-count"):
        append_message(one, second, updated_at="2026-08-06T12:02:00Z")
    with pytest.raises(CopilotContractError, match="bounded UTF-8"):
        _message("أ" * 9_000)


def test_completed_or_cancelled_session_cannot_accept_messages() -> None:
    for status in (SessionStatus.COMPLETED, SessionStatus.CANCELLED, SessionStatus.EXPIRED):
        with pytest.raises(CopilotContractError, match="active session"):
            append_message(
                replace(_session(), status=status),
                _message(),
                updated_at="2026-08-06T12:01:00Z",
            )


def test_egress_review_previews_exact_fields_without_transmission() -> None:
    fields = (
        create_egress_field("question.text", "ما عدد المقابس المحددة؟"),
        create_egress_field("context.summary", "2 sockets; 1 route"),
    )

    review = create_egress_review(
        review_id="e" * 32,
        session=_session(),
        destination="provider:configured",
        privacy=PrivacyClassification.PROJECT_CONFIDENTIAL,
        fields=fields,
        created_at="2026-08-06T12:00:00Z",
        expires_at="2026-08-06T12:10:00Z",
    )
    approved = decide_egress_review(
        review, approved=True, decided_at="2026-08-06T12:01:00Z"
    )

    assert review.status is EgressReviewStatus.PENDING
    assert approved.status is EgressReviewStatus.APPROVED
    assert approved.total_utf8_bytes == sum(field.utf8_bytes for field in fields)
    assert approved.fields[0].to_dict()["value"] == "ما عدد المقابس المحددة؟"
    assert approved.whole_drawing_included is False
    assert approved.transmission_performed is False


def test_egress_review_rejects_tampering_whole_drawing_and_fake_transmission() -> None:
    field = create_egress_field("question.text", "Inspect selection")
    with pytest.raises(CopilotContractError, match="SHA-256"):
        replace(field, value="Inspect selectioN")
    review = create_egress_review(
        review_id="e" * 32,
        session=_session(),
        destination="provider:configured",
        privacy=PrivacyClassification.INTERNAL,
        fields=(field,),
        created_at="2026-08-06T12:00:00Z",
        expires_at="2026-08-06T12:10:00Z",
    )
    with pytest.raises(CopilotContractError, match="Whole-drawing"):
        replace(review, whole_drawing_included=True)
    with pytest.raises(CopilotContractError, match="cannot claim transmission"):
        replace(review, transmission_performed=True)
    with pytest.raises(CopilotContractError, match="Only a pending"):
        decide_egress_review(
            decide_egress_review(
                review, approved=False, decided_at="2026-08-06T12:01:00Z"
            ),
            approved=True,
            decided_at="2026-08-06T12:02:00Z",
        )


def test_egress_review_rejects_mutable_or_duplicate_fields() -> None:
    field = create_egress_field("context.summary", "bounded")
    review = create_egress_review(
        review_id="e" * 32,
        session=_session(),
        destination="provider:configured",
        privacy=PrivacyClassification.PUBLIC,
        fields=(field,),
        created_at="2026-08-06T12:00:00Z",
        expires_at="2026-08-06T12:10:00Z",
    )
    with pytest.raises(CopilotContractError, match="immutable fields"):
        replace(review, fields=[field])  # type: ignore[arg-type]
    with pytest.raises(CopilotContractError, match="unique"):
        replace(
            review,
            fields=(field, field),
            total_utf8_bytes=field.utf8_bytes * 2,
        )
