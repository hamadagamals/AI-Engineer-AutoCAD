"""Thread-safe in-memory ownership and bounds for local copilot sessions."""

from __future__ import annotations

import hashlib
import logging
import secrets
import threading
import uuid
from collections import deque
from dataclasses import dataclass, replace
from datetime import UTC, datetime, timedelta
from typing import Callable

from agent.copilot import (
    ContextBinding,
    ContextSummary,
    CopilotAnswer,
    CopilotCitation,
    CopilotLanguage,
    CopilotMessage,
    CopilotSession,
    DataEgressReview,
    EgressField,
    EgressReviewStatus,
    MessageRole,
    SessionStatus,
    append_message,
    create_egress_review,
    decide_egress_review,
)
from agent.exceptions import CopilotContractError, ResourceNotFoundError
from agent.copilot_provider import CopilotProviderAdapter, CopilotProviderRequest
from core.electrical_model import PrivacyClassification


@dataclass(frozen=True, slots=True)
class CreatedCopilotSession:
    """New session plus its one-time plaintext ownership key."""

    session: CopilotSession
    owner_key: str


@dataclass(slots=True)
class _Entry:
    session: CopilotSession
    owner_key_sha256: str
    operations: deque[datetime]
    reviews: dict[str, DataEgressReview]
    answers: dict[str, CopilotAnswer]
    used_review_ids: set[str]
    in_flight_review_ids: set[str]


class CopilotSessionManager:
    """Own bounded local sessions without provider calls or drawing capabilities."""

    def __init__(
        self,
        *,
        clock: Callable[[], datetime] | None = None,
        id_factory: Callable[[], str] | None = None,
        key_factory: Callable[[], str] | None = None,
        maximum_active_sessions: int = 8,
        maximum_operations_per_minute: int = 30,
        logger: logging.Logger | None = None,
        provider: CopilotProviderAdapter | None = None,
    ) -> None:
        if not 1 <= maximum_active_sessions <= 32:
            raise CopilotContractError("Active-session limit must be from 1 through 32.")
        if not 1 <= maximum_operations_per_minute <= 120:
            raise CopilotContractError("Operation rate limit must be from 1 through 120.")
        self._clock = clock or (lambda: datetime.now(UTC))
        self._id_factory = id_factory or (lambda: uuid.uuid4().hex)
        self._key_factory = key_factory or (lambda: secrets.token_urlsafe(32))
        self.maximum_active_sessions = maximum_active_sessions
        self.maximum_operations_per_minute = maximum_operations_per_minute
        self._logger = logger or logging.getLogger("ai_engineer.copilot")
        self.provider = provider
        self._entries: dict[str, _Entry] = {}
        self._issued_ids: set[str] = set()
        self._lock = threading.RLock()

    def create_session(
        self,
        *,
        project_id: str,
        context: ContextBinding,
        context_summary: ContextSummary,
        language: CopilotLanguage,
        ttl_minutes: int = 60,
    ) -> CreatedCopilotSession:
        """Create one local session and return an unpersisted ownership secret."""
        if not isinstance(ttl_minutes, int) or isinstance(ttl_minutes, bool) or not 5 <= ttl_minutes <= 120:
            raise CopilotContractError("Session TTL must be from 5 through 120 minutes.")
        with self._lock:
            now = self._now()
            self._expire_entries(now)
            active = sum(
                entry.session.status is SessionStatus.ACTIVE
                for entry in self._entries.values()
            )
            if active >= self.maximum_active_sessions:
                raise CopilotContractError("Concurrent active-session limit reached.")
            session_id = self._new_id("session")
            owner_key = self._key_factory()
            if not isinstance(owner_key, str) or len(owner_key) < 32:
                raise CopilotContractError("Generated session ownership key is invalid.")
            timestamp = _timestamp(now)
            session = CopilotSession(
                1,
                session_id,
                project_id,
                context,
                context_summary,
                language,
                SessionStatus.ACTIVE,
                timestamp,
                timestamp,
                _timestamp(now + timedelta(minutes=ttl_minutes)),
                (),
            )
            self._entries[session_id] = _Entry(
                session,
                _secret_digest(owner_key),
                deque(),
                {},
                {},
                set(),
                set(),
            )
            self._logger.info(
                "copilot_session_created",
                extra={
                    "session_id": session_id,
                    "context_id": context.context_id,
                    "scope": context.scope.value,
                    "language": language.value,
                    "drawing_write_allowed": False,
                },
            )
            return CreatedCopilotSession(session, owner_key)

    def get_session(self, session_id: str, owner_key: str) -> CopilotSession:
        with self._lock:
            entry = self._owned_entry(session_id, owner_key)
            self._record_operation(entry)
            return entry.session

    def add_engineer_message(
        self,
        session_id: str,
        owner_key: str,
        *,
        language: CopilotLanguage,
        content: str,
    ) -> CopilotSession:
        with self._lock:
            entry = self._owned_entry(session_id, owner_key)
            self._record_operation(entry)
            now = self._now()
            message = CopilotMessage(
                self._new_id("message"),
                session_id,
                entry.session.context.context_id,
                MessageRole.ENGINEER,
                language,
                content,
                _timestamp(now),
            )
            entry.session = append_message(
                entry.session, message, updated_at=_timestamp(now)
            )
            self._logger.info(
                "copilot_message_added",
                extra={
                    "session_id": session_id,
                    "message_id": message.message_id,
                    "language": language.value,
                    "content_bytes": len(content.encode("utf-8")),
                },
            )
            return entry.session

    def cancel_session(self, session_id: str, owner_key: str) -> CopilotSession:
        with self._lock:
            entry = self._owned_entry(session_id, owner_key)
            self._record_operation(entry)
            if entry.session.status is not SessionStatus.ACTIVE:
                raise CopilotContractError("Only an active session may be cancelled.")
            if entry.in_flight_review_ids:
                raise CopilotContractError("Session cannot be cancelled during a provider call.")
            entry.session = replace(
                entry.session,
                status=SessionStatus.CANCELLED,
                updated_at=_timestamp(self._now()),
            )
            self._logger.info(
                "copilot_session_cancelled", extra={"session_id": session_id}
            )
            return entry.session

    def create_review(
        self,
        session_id: str,
        owner_key: str,
        *,
        destination: str,
        privacy: PrivacyClassification,
        fields: tuple[EgressField, ...],
        ttl_minutes: int = 10,
    ) -> DataEgressReview:
        with self._lock:
            entry = self._owned_entry(session_id, owner_key)
            self._record_operation(entry)
            if entry.session.status is not SessionStatus.ACTIVE:
                raise CopilotContractError("Egress review requires an active session.")
            if not isinstance(ttl_minutes, int) or isinstance(ttl_minutes, bool) or not 1 <= ttl_minutes <= 30:
                raise CopilotContractError("Egress review TTL must be from 1 through 30 minutes.")
            now = self._now()
            review_id = self._new_id("review")
            if review_id in entry.reviews:
                raise CopilotContractError("Generated review ID is not unique.")
            review = create_egress_review(
                review_id=review_id,
                session=entry.session,
                destination=destination,
                privacy=privacy,
                fields=fields,
                created_at=_timestamp(now),
                expires_at=_timestamp(min(now + timedelta(minutes=ttl_minutes), _parse(entry.session.expires_at))),
            )
            entry.reviews[review.review_id] = review
            self._logger.info(
                "copilot_egress_review_created",
                extra={
                    "session_id": session_id,
                    "review_id": review.review_id,
                    "destination": review.destination,
                    "field_count": len(review.fields),
                    "total_utf8_bytes": review.total_utf8_bytes,
                    "transmission_performed": False,
                },
            )
            return review

    def get_review(
        self, session_id: str, owner_key: str, review_id: str
    ) -> DataEgressReview:
        with self._lock:
            entry = self._owned_entry(session_id, owner_key)
            self._record_operation(entry)
            try:
                return entry.reviews[review_id]
            except KeyError as exc:
                raise ResourceNotFoundError("Copilot egress review was not found.") from exc

    def decide_review(
        self,
        session_id: str,
        owner_key: str,
        review_id: str,
        *,
        approved: bool,
    ) -> DataEgressReview:
        with self._lock:
            entry = self._owned_entry(session_id, owner_key)
            self._record_operation(entry)
            try:
                review = entry.reviews[review_id]
            except KeyError as exc:
                raise ResourceNotFoundError("Copilot egress review was not found.") from exc
            now = self._now()
            if now >= _parse(review.expires_at):
                entry.reviews[review_id] = replace(
                    review,
                    status=EgressReviewStatus.EXPIRED,
                    decided_at=review.expires_at,
                )
                raise CopilotContractError("Copilot egress review has expired.")
            decided = decide_egress_review(
                review, approved=approved, decided_at=_timestamp(now)
            )
            entry.reviews[review_id] = decided
            self._logger.info(
                "copilot_egress_review_decided",
                extra={
                    "session_id": session_id,
                    "review_id": review_id,
                    "status": decided.status.value,
                    "transmission_performed": False,
                },
            )
            return decided

    def request_answer(
        self, session_id: str, owner_key: str, review_id: str
    ) -> tuple[CopilotSession, CopilotAnswer]:
        """Use one approved preview once and append only a validated read-only answer."""
        with self._lock:
            entry = self._owned_entry(session_id, owner_key)
            self._record_operation(entry)
            if self.provider is None:
                raise CopilotContractError("Copilot provider is not configured.")
            if entry.session.status is not SessionStatus.ACTIVE:
                raise CopilotContractError("Copilot answer requires an active session.")
            try:
                review = entry.reviews[review_id]
            except KeyError as exc:
                raise ResourceNotFoundError("Copilot egress review was not found.") from exc
            if review.status is not EgressReviewStatus.APPROVED:
                raise CopilotContractError("Copilot provider requires an approved egress review.")
            if self._now() >= _parse(review.expires_at):
                raise CopilotContractError("Copilot egress review has expired.")
            if review_id in entry.used_review_ids or review_id in entry.in_flight_review_ids:
                raise CopilotContractError("Copilot egress review has already been used.")
            entry.used_review_ids.add(review_id)
            entry.in_flight_review_ids.add(review_id)
            request = CopilotProviderRequest(
                session_id,
                entry.session.context.context_id,
                review_id,
                review.preview_sha256,
                entry.session.language,
                review.fields,
            )
            provider = self.provider
        try:
            output = provider.generate(request)
        except Exception:
            with self._lock:
                current = self._entries.get(session_id)
                if current is not None:
                    current.in_flight_review_ids.discard(review_id)
                self._logger.error(
                    "copilot_provider_call_failed",
                    extra={
                        "session_id": session_id,
                        "review_id": review_id,
                        "provider_name": provider.name,
                        "egress_attempted": True,
                        "drawing_write_performed": False,
                    },
                )
            raise
        with self._lock:
            entry = self._entries[session_id]
            entry.in_flight_review_ids.discard(review_id)
            now = self._now()
            answer = CopilotAnswer(
                self._new_id("answer"),
                session_id,
                entry.session.context.context_id,
                review_id,
                review.preview_sha256,
                provider.name,
                output.language,
                output.answer,
                tuple(
                    CopilotCitation(item.source_field, item.title)
                    for item in output.citations
                ),
                _timestamp(now),
                True,
                False,
            )
            message = CopilotMessage(
                self._new_id("message"),
                session_id,
                entry.session.context.context_id,
                MessageRole.COPILOT,
                output.language,
                output.answer,
                _timestamp(now),
            )
            entry.session = append_message(
                entry.session, message, updated_at=_timestamp(now)
            )
            entry.answers[answer.answer_id] = answer
            self._logger.info(
                "copilot_answer_recorded",
                extra={
                    "session_id": session_id,
                    "review_id": review_id,
                    "answer_id": answer.answer_id,
                    "provider_name": provider.name,
                    "citation_count": len(answer.citations),
                    "answer_bytes": len(answer.text.encode("utf-8")),
                    "data_egress_performed": True,
                    "drawing_write_performed": False,
                },
            )
            return entry.session, answer

    def get_answer(
        self, session_id: str, owner_key: str, answer_id: str
    ) -> CopilotAnswer:
        with self._lock:
            entry = self._owned_entry(session_id, owner_key)
            self._record_operation(entry)
            try:
                return entry.answers[answer_id]
            except KeyError as exc:
                raise ResourceNotFoundError("Copilot answer was not found.") from exc

    def _owned_entry(self, session_id: str, owner_key: str) -> _Entry:
        if (
            not isinstance(session_id, str)
            or not isinstance(owner_key, str)
            or not 32 <= len(owner_key) <= 128
        ):
            raise ResourceNotFoundError("Copilot session was not found.")
        entry = self._entries.get(session_id)
        if entry is None or not secrets.compare_digest(
            entry.owner_key_sha256, _secret_digest(owner_key)
        ):
            raise ResourceNotFoundError("Copilot session was not found.")
        now = self._now()
        if now >= _parse(entry.session.expires_at):
            if entry.session.status is SessionStatus.ACTIVE:
                entry.session = replace(entry.session, status=SessionStatus.EXPIRED)
            raise CopilotContractError("Copilot session has expired.")
        return entry

    def _record_operation(self, entry: _Entry) -> None:
        now = self._now()
        cutoff = now - timedelta(minutes=1)
        while entry.operations and entry.operations[0] <= cutoff:
            entry.operations.popleft()
        if len(entry.operations) >= self.maximum_operations_per_minute:
            raise CopilotContractError("Copilot session operation rate limit reached.")
        entry.operations.append(now)

    def _expire_entries(self, now: datetime) -> None:
        for entry in self._entries.values():
            if (
                entry.session.status is SessionStatus.ACTIVE
                and now >= _parse(entry.session.expires_at)
            ):
                entry.session = replace(entry.session, status=SessionStatus.EXPIRED)

    def _new_id(self, name: str) -> str:
        value = self._id_factory()
        if not isinstance(value, str) or len(value) != 32 or any(
            character not in "0123456789abcdef" for character in value
        ):
            raise CopilotContractError(f"Generated {name} ID is invalid.")
        if value in self._issued_ids:
            raise CopilotContractError(f"Generated {name} ID is not unique.")
        self._issued_ids.add(value)
        return value

    def _now(self) -> datetime:
        value = self._clock()
        if not isinstance(value, datetime) or value.tzinfo is None:
            raise CopilotContractError("Copilot clock must return timezone-aware values.")
        return value.astimezone(UTC)


def _secret_digest(value: str) -> str:
    return hashlib.sha256(value.encode("utf-8")).hexdigest()


def _timestamp(value: datetime) -> str:
    return value.astimezone(UTC).isoformat().replace("+00:00", "Z")


def _parse(value: str) -> datetime:
    return datetime.fromisoformat(value[:-1] + "+00:00")
