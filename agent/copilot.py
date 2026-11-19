"""Immutable provider-independent contracts for the local read-only copilot."""

from __future__ import annotations

import re
import hashlib
import json
from dataclasses import dataclass, fields, is_dataclass, replace
from datetime import datetime
from enum import Enum
from typing import Any

from agent.exceptions import CopilotContractError
from core.electrical_model import PrivacyClassification


_IDENTIFIER = re.compile(r"[a-z][a-z0-9_.:-]{0,127}")
_HEX32 = re.compile(r"[0-9a-f]{32}")
_SHA256 = re.compile(r"[0-9a-f]{64}")


class CopilotLanguage(str, Enum):
    """User-visible languages supported by the initial palette contract."""

    ARABIC = "ar"
    ENGLISH = "en"


class MessageRole(str, Enum):
    """Allowlisted local conversation roles with no provider-specific role names."""

    ENGINEER = "engineer"
    COPILOT = "copilot"
    SYSTEM = "system"


class SessionStatus(str, Enum):
    """Lifecycle states for one bounded local conversation."""

    ACTIVE = "active"
    CANCELLED = "cancelled"
    COMPLETED = "completed"
    EXPIRED = "expired"


class ContextScope(str, Enum):
    """The exact local context bound to a conversation."""

    SELECTION = "selection"
    DRAWING = "drawing"
    GENERAL = "general"


class EgressReviewStatus(str, Enum):
    """Human decision state for an exact provider data preview."""

    PENDING = "pending"
    APPROVED = "approved"
    REJECTED = "rejected"
    EXPIRED = "expired"


class JsonContract:
    """Serialize immutable contracts without provider or executable behavior."""

    def to_dict(self) -> dict[str, Any]:
        value = _json_value(self)
        if not isinstance(value, dict):
            raise CopilotContractError("Copilot contract must serialize as an object.")
        return value


@dataclass(frozen=True, slots=True)
class CopilotCitation(JsonContract):
    """Provider citation bound to one exact approved egress field."""

    source_field: str
    title: str

    def __post_init__(self) -> None:
        _require_field_name(self.source_field)
        _require_text(self.title, "citation title", 512)


@dataclass(frozen=True, slots=True)
class CopilotAnswer(JsonContract):
    """Validated read-only answer produced from one approved exact preview."""

    answer_id: str
    session_id: str
    context_id: str
    review_id: str
    preview_sha256: str
    provider_name: str
    language: CopilotLanguage
    text: str
    citations: tuple[CopilotCitation, ...]
    created_at: str
    data_egress_performed: bool
    drawing_write_performed: bool

    def __post_init__(self) -> None:
        _require_pattern(self.answer_id, _HEX32, "answer ID")
        _require_pattern(self.session_id, _HEX32, "answer session ID")
        _require_pattern(self.context_id, _HEX32, "answer context ID")
        _require_pattern(self.review_id, _HEX32, "answer review ID")
        _require_pattern(self.preview_sha256, _SHA256, "answer preview SHA-256")
        _require_identifier(self.provider_name, "answer provider name")
        if not isinstance(self.language, CopilotLanguage):
            raise CopilotContractError("Answer language is invalid.")
        _require_text(self.text, "answer text", 16_384)
        if not isinstance(self.citations, tuple) or len(self.citations) > 16:
            raise CopilotContractError(
                "Answer citations must be an immutable tuple of at most 16 records."
            )
        if any(not isinstance(item, CopilotCitation) for item in self.citations):
            raise CopilotContractError("Answer citations contain an invalid record.")
        sources = [item.source_field for item in self.citations]
        if len(sources) != len(set(sources)):
            raise CopilotContractError("Answer citation sources must be unique.")
        _require_utc(self.created_at, "answer created_at")
        if self.data_egress_performed is not True:
            raise CopilotContractError("Provider answer must record performed data egress.")
        if self.drawing_write_performed is not False:
            raise CopilotContractError("Copilot answer cannot record a drawing write.")


@dataclass(frozen=True, slots=True)
class ContextBinding(JsonContract):
    """Bind a session to one exact hash-reviewed local drawing context."""

    context_id: str
    evidence_sha256: str
    scope: ContextScope

    def __post_init__(self) -> None:
        _require_pattern(self.context_id, _HEX32, "context ID")
        _require_pattern(self.evidence_sha256, _SHA256, "context evidence SHA-256")
        if not isinstance(self.scope, ContextScope):
            raise CopilotContractError("Context scope is invalid.")


@dataclass(frozen=True, slots=True)
class ContextSummary(JsonContract):
    """Bounded local counts and uncertainty shown by the read-only palette."""

    entity_count: int
    layer_count: int
    device_count: int
    route_count: int
    unknown_count: int
    warning_count: int
    truncated: bool
    database_modified: int
    block_reference_count: int = 0
    route_primitive_count: int = 0

    def __post_init__(self) -> None:
        for name in (
            "entity_count",
            "layer_count",
            "device_count",
            "route_count",
            "unknown_count",
            "warning_count",
            "block_reference_count",
            "route_primitive_count",
        ):
            value = getattr(self, name)
            if (
                not isinstance(value, int)
                or isinstance(value, bool)
                or not 0 <= value <= 1_000_000
            ):
                raise CopilotContractError(f"Context summary {name} is invalid.")
        if self.device_count + self.route_count + self.unknown_count > self.entity_count:
            raise CopilotContractError("Context summary classifications exceed entities.")
        if self.block_reference_count > self.entity_count:
            raise CopilotContractError("Context summary block references exceed entities.")
        if self.route_primitive_count > self.entity_count:
            raise CopilotContractError("Context summary route primitives exceed entities.")
        if not isinstance(self.truncated, bool):
            raise CopilotContractError("Context summary truncated flag must be boolean.")
        if (
            not isinstance(self.database_modified, int)
            or isinstance(self.database_modified, bool)
            or not 0 <= self.database_modified <= 63
        ):
            raise CopilotContractError("Context summary DBMOD is invalid.")


@dataclass(frozen=True, slots=True)
class CopilotMessage(JsonContract):
    """One bounded Arabic or English local message tied to the session context."""

    message_id: str
    session_id: str
    context_id: str
    role: MessageRole
    language: CopilotLanguage
    content: str
    created_at: str

    def __post_init__(self) -> None:
        _require_pattern(self.message_id, _HEX32, "message ID")
        _require_pattern(self.session_id, _HEX32, "message session ID")
        _require_pattern(self.context_id, _HEX32, "message context ID")
        if not isinstance(self.role, MessageRole):
            raise CopilotContractError("Message role is invalid.")
        if not isinstance(self.language, CopilotLanguage):
            raise CopilotContractError("Message language is invalid.")
        _require_text(self.content, "message content", 16_384)
        _require_utc(self.created_at, "message created_at")


@dataclass(frozen=True, slots=True)
class EgressField(JsonContract):
    """One exact bounded field proposed for optional provider transmission."""

    field_name: str
    value: str
    utf8_bytes: int
    value_sha256: str

    def __post_init__(self) -> None:
        _require_field_name(self.field_name)
        _require_text(self.value, "egress field value", 65_536)
        encoded = self.value.encode("utf-8")
        if self.utf8_bytes != len(encoded):
            raise CopilotContractError("Egress field byte count does not match its value.")
        _require_pattern(self.value_sha256, _SHA256, "egress field SHA-256")
        if self.value_sha256 != hashlib.sha256(encoded).hexdigest():
            raise CopilotContractError("Egress field SHA-256 does not match its value.")


def create_egress_field(field_name: str, value: str) -> EgressField:
    """Create a hash-bound preview field without transmitting it."""
    if not isinstance(value, str):
        raise CopilotContractError("Egress field value must be text.")
    encoded = value.encode("utf-8")
    return EgressField(
        field_name,
        value,
        len(encoded),
        hashlib.sha256(encoded).hexdigest(),
    )


@dataclass(frozen=True, slots=True)
class DataEgressReview(JsonContract):
    """Exact local-only preview requiring a separate decision before provider use."""

    schema_version: int
    review_id: str
    session_id: str
    context_id: str
    destination: str
    privacy: PrivacyClassification
    status: EgressReviewStatus
    fields: tuple[EgressField, ...]
    total_utf8_bytes: int
    preview_sha256: str
    created_at: str
    expires_at: str
    decided_at: str | None
    whole_drawing_included: bool = False
    transmission_performed: bool = False

    def __post_init__(self) -> None:
        if self.schema_version != 1:
            raise CopilotContractError("Data-egress review schema_version must be 1.")
        _require_pattern(self.review_id, _HEX32, "egress review ID")
        _require_pattern(self.session_id, _HEX32, "egress session ID")
        _require_pattern(self.context_id, _HEX32, "egress context ID")
        _require_identifier(self.destination, "egress destination")
        if not isinstance(self.privacy, PrivacyClassification):
            raise CopilotContractError("Egress privacy classification is invalid.")
        if not isinstance(self.status, EgressReviewStatus):
            raise CopilotContractError("Egress review status is invalid.")
        if not isinstance(self.fields, tuple) or not 1 <= len(self.fields) <= 32:
            raise CopilotContractError("Egress review must contain 1 through 32 immutable fields.")
        if any(not isinstance(field, EgressField) for field in self.fields):
            raise CopilotContractError("Egress review fields must contain typed records.")
        names = [field.field_name.casefold() for field in self.fields]
        if len(names) != len(set(names)):
            raise CopilotContractError("Egress review field names must be unique.")
        total = sum(field.utf8_bytes for field in self.fields)
        if self.total_utf8_bytes != total or not 1 <= total <= 65_536:
            raise CopilotContractError("Egress review total byte count is invalid.")
        _require_pattern(self.preview_sha256, _SHA256, "egress preview SHA-256")
        if self.preview_sha256 != _preview_sha256(self.fields):
            raise CopilotContractError("Egress preview SHA-256 does not match its fields.")
        created = _require_utc(self.created_at, "egress review created_at")
        expires = _require_utc(self.expires_at, "egress review expires_at")
        if created >= expires:
            raise CopilotContractError("Egress review must expire after it is created.")
        if self.status is EgressReviewStatus.PENDING:
            if self.decided_at is not None:
                raise CopilotContractError("Pending egress review cannot have decided_at.")
        else:
            decided = _require_utc(self.decided_at, "egress review decided_at")
            if not created <= decided <= expires:
                raise CopilotContractError("Egress decision timestamp is outside its review window.")
        if self.whole_drawing_included is not False:
            raise CopilotContractError("Whole-drawing provider egress is prohibited.")
        if self.transmission_performed is not False:
            raise CopilotContractError("An egress review cannot claim transmission occurred.")


def create_egress_review(
    *,
    review_id: str,
    session: CopilotSession,
    destination: str,
    privacy: PrivacyClassification,
    fields: tuple[EgressField, ...],
    created_at: str,
    expires_at: str,
) -> DataEgressReview:
    """Create a pending exact preview; this function performs no provider call."""
    if not isinstance(session, CopilotSession):
        raise CopilotContractError("Egress review requires a typed copilot session.")
    total = sum(field.utf8_bytes for field in fields) if isinstance(fields, tuple) else 0
    return DataEgressReview(
        1,
        review_id,
        session.session_id,
        session.context.context_id,
        destination,
        privacy,
        EgressReviewStatus.PENDING,
        fields,
        total,
        _preview_sha256(fields),
        created_at,
        expires_at,
        None,
        False,
        False,
    )


def decide_egress_review(
    review: DataEgressReview, *, approved: bool, decided_at: str
) -> DataEgressReview:
    """Record one local human decision without transmitting preview data."""
    if not isinstance(review, DataEgressReview):
        raise CopilotContractError("Egress decision requires a typed review.")
    if review.status is not EgressReviewStatus.PENDING:
        raise CopilotContractError("Only a pending egress review may be decided.")
    return replace(
        review,
        status=(
            EgressReviewStatus.APPROVED if approved else EgressReviewStatus.REJECTED
        ),
        decided_at=decided_at,
    )


@dataclass(frozen=True, slots=True)
class CopilotSession(JsonContract):
    """One bounded provider-independent conversation with no drawing-write power."""

    schema_version: int
    session_id: str
    project_id: str
    context: ContextBinding
    context_summary: ContextSummary
    language: CopilotLanguage
    status: SessionStatus
    created_at: str
    updated_at: str
    expires_at: str
    messages: tuple[CopilotMessage, ...]
    maximum_messages: int = 64
    maximum_total_text_bytes: int = 262_144
    drawing_write_allowed: bool = False

    def __post_init__(self) -> None:
        if self.schema_version != 1:
            raise CopilotContractError("Copilot session schema_version must be 1.")
        _require_pattern(self.session_id, _HEX32, "session ID")
        _require_identifier(self.project_id, "session project ID")
        if not isinstance(self.context, ContextBinding):
            raise CopilotContractError("Session context binding is invalid.")
        if not isinstance(self.context_summary, ContextSummary):
            raise CopilotContractError("Session context summary is invalid.")
        if not isinstance(self.language, CopilotLanguage):
            raise CopilotContractError("Session language is invalid.")
        if not isinstance(self.status, SessionStatus):
            raise CopilotContractError("Session status is invalid.")
        created = _require_utc(self.created_at, "session created_at")
        updated = _require_utc(self.updated_at, "session updated_at")
        expires = _require_utc(self.expires_at, "session expires_at")
        if not created <= updated < expires:
            raise CopilotContractError(
                "Session timestamps must satisfy created_at <= updated_at < expires_at."
            )
        if (
            not isinstance(self.maximum_messages, int)
            or isinstance(self.maximum_messages, bool)
            or not 1 <= self.maximum_messages <= 256
        ):
            raise CopilotContractError("Session maximum_messages must be from 1 through 256.")
        if (
            not isinstance(self.maximum_total_text_bytes, int)
            or isinstance(self.maximum_total_text_bytes, bool)
            or not 1_024 <= self.maximum_total_text_bytes <= 1_048_576
        ):
            raise CopilotContractError(
                "Session maximum_total_text_bytes must be from 1024 through 1048576."
            )
        if self.drawing_write_allowed is not False:
            raise CopilotContractError("Milestone 17 sessions cannot allow drawing writes.")
        if not isinstance(self.messages, tuple):
            raise CopilotContractError("Session messages must be an immutable tuple.")
        if len(self.messages) > self.maximum_messages:
            raise CopilotContractError("Session exceeds its message-count limit.")
        message_ids: list[str] = []
        previous = created
        total_bytes = 0
        for message in self.messages:
            if not isinstance(message, CopilotMessage):
                raise CopilotContractError("Session messages must contain typed records.")
            if message.session_id != self.session_id:
                raise CopilotContractError("Message is bound to a different session.")
            if message.context_id != self.context.context_id:
                raise CopilotContractError("Message is bound to a different context.")
            message_time = _require_utc(message.created_at, "message created_at")
            if not previous <= message_time <= updated:
                raise CopilotContractError("Session messages are not chronologically bounded.")
            previous = message_time
            message_ids.append(message.message_id)
            total_bytes += len(message.content.encode("utf-8"))
        if len(message_ids) != len(set(message_ids)):
            raise CopilotContractError("Session message IDs must be unique.")
        if total_bytes > self.maximum_total_text_bytes:
            raise CopilotContractError("Session exceeds its total text-byte limit.")


def append_message(
    session: CopilotSession, message: CopilotMessage, *, updated_at: str
) -> CopilotSession:
    """Return a new session after validating one context-bound local message."""
    if not isinstance(session, CopilotSession) or not isinstance(message, CopilotMessage):
        raise CopilotContractError("Appending requires typed session and message records.")
    if session.status is not SessionStatus.ACTIVE:
        raise CopilotContractError("Messages can be appended only to an active session.")
    return replace(
        session,
        updated_at=updated_at,
        messages=session.messages + (message,),
    )


def _json_value(value: Any) -> Any:
    if isinstance(value, Enum):
        return value.value
    if is_dataclass(value):
        return {item.name: _json_value(getattr(value, item.name)) for item in fields(value)}
    if isinstance(value, tuple):
        return [_json_value(item) for item in value]
    return value


def _preview_sha256(fields: tuple[EgressField, ...]) -> str:
    if not isinstance(fields, tuple) or any(
        not isinstance(field, EgressField) for field in fields
    ):
        raise CopilotContractError("Egress preview requires immutable typed fields.")
    value = [field.to_dict() for field in fields]
    encoded = json.dumps(
        value, ensure_ascii=False, sort_keys=True, separators=(",", ":")
    ).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest()


def _require_field_name(value: object) -> None:
    if (
        not isinstance(value, str)
        or re.fullmatch(r"[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*){0,7}", value) is None
    ):
        raise CopilotContractError("Egress field name must be a bounded dotted identifier.")


def _require_identifier(value: object, name: str) -> None:
    if not isinstance(value, str) or _IDENTIFIER.fullmatch(value) is None:
        raise CopilotContractError(f"{name} must be a bounded lowercase identifier.")


def _require_pattern(value: object, pattern: re.Pattern[str], name: str) -> None:
    if not isinstance(value, str) or pattern.fullmatch(value) is None:
        raise CopilotContractError(f"{name} has an invalid format.")


def _require_text(value: object, name: str, maximum_bytes: int) -> None:
    if (
        not isinstance(value, str)
        or not value.strip()
        or len(value.encode("utf-8")) > maximum_bytes
        or any(ord(character) < 32 and character not in "\t\n" for character in value)
    ):
        raise CopilotContractError(
            f"{name} must be non-empty bounded UTF-8 without unsupported controls."
        )


def _require_utc(value: object, name: str) -> datetime:
    if not isinstance(value, str) or not value.endswith("Z") or len(value) > 40:
        raise CopilotContractError(f"{name} must be a bounded UTC timestamp.")
    try:
        parsed = datetime.fromisoformat(value[:-1] + "+00:00")
    except ValueError as exc:
        raise CopilotContractError(f"{name} must be a valid UTC timestamp.") from exc
    return parsed
