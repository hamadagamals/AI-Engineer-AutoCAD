"""Bounded file channel for one reviewed read-only AutoCAD connection capability."""

from __future__ import annotations

import hashlib
import json
import os
import re
import tempfile
import uuid
from dataclasses import asdict, dataclass
from datetime import UTC, datetime, timedelta
from pathlib import Path

from agent.exceptions import AutoCADContextError
from integrations.autocad_project_topology import (
    AnchorCaptureDocument,
    AnchorCaptureSelection,
    ConnectionAnchorCapture,
)


REQUEST_VERSION = "autocad.connection_pair_review_request.v1"
RESULT_VERSION = "autocad.connection_pair_review_result.v1"
_ID = re.compile(r"[0-9a-f]{32}")
_DOCUMENT = re.compile(r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")
_HANDLE = re.compile(r"[0-9A-F]{1,32}")
_SHA = re.compile(r"[0-9a-f]{64}")
_MAX_BYTES = 16_384


@dataclass(frozen=True, slots=True)
class ConnectionPairReviewRequest:
    schema_version: int
    request_version: str
    request_id: str
    created_at: str
    expires_at: str
    document_id: str
    source_drawing_sha256: str
    device_object_id: str
    route_object_id: str
    endpoint: str
    terminal_name: str
    requested_action: str
    drawing_mutation_allowed: bool

    def __post_init__(self) -> None:
        if self.schema_version != 1 or self.request_version != REQUEST_VERSION:
            raise AutoCADContextError("AutoCAD review request version is unsupported.")
        if _ID.fullmatch(self.request_id) is None or _DOCUMENT.fullmatch(self.document_id) is None:
            raise AutoCADContextError("AutoCAD review request identity is invalid.")
        if _SHA.fullmatch(self.source_drawing_sha256) is None:
            raise AutoCADContextError("AutoCAD review source SHA-256 is invalid.")
        if _HANDLE.fullmatch(self.device_object_id) is None or _HANDLE.fullmatch(self.route_object_id) is None:
            raise AutoCADContextError("AutoCAD review object handle is invalid.")
        if self.endpoint not in {"start", "end"}:
            raise AutoCADContextError("AutoCAD review endpoint is invalid.")
        if not isinstance(self.terminal_name, str) or not 1 <= len(self.terminal_name) <= 64:
            raise AutoCADContextError("AutoCAD review terminal name is invalid.")
        if self.requested_action != "review_connection_pair" or self.drawing_mutation_allowed is not False:
            raise AutoCADContextError("AutoCAD review request capability is unsupported.")
        created = _timestamp(self.created_at, "created")
        expires = _timestamp(self.expires_at, "expiry")
        if not created < expires <= created + timedelta(minutes=30):
            raise AutoCADContextError("AutoCAD review request lifetime is invalid.")


@dataclass(frozen=True, slots=True)
class ConnectionPairReviewResult:
    result_id: str
    request_id: str
    request_sha256: str
    captured_at: str
    document_id: str
    drawing_name: str
    drawing_units: int
    layout: str
    status: str
    source_drawing_sha256: str | None
    device_object_id: str | None
    block_name: str | None
    route_object_id: str | None
    route_entity_type: str | None
    endpoint: str | None
    world_position: tuple[float, float, float] | None
    alternate_endpoint_distance: float | None
    terminal_name: str | None
    database_modified_before: int | None
    database_modified_after: int | None
    approval_method: str | None
    drawing_mutation_performed: bool
    error: str | None


@dataclass(frozen=True, slots=True)
class EnqueuedReview:
    request: ConnectionPairReviewRequest
    path: Path
    request_sha256: str


@dataclass(frozen=True, slots=True)
class StoredAnchorCapture:
    capture: ConnectionAnchorCapture
    path: Path
    capture_sha256: str


def enqueue_connection_pair_review(
    workspace_root: Path,
    *,
    document_id: str,
    source_drawing_sha256: str,
    device_object_id: str,
    route_object_id: str,
    endpoint: str,
    terminal_name: str = "CIRCUIT",
    now: datetime | None = None,
) -> EnqueuedReview:
    """Atomically enqueue one expiring no-mutation connection review request."""
    created = (now or datetime.now(UTC)).astimezone(UTC)
    request = ConnectionPairReviewRequest(
        1,
        REQUEST_VERSION,
        uuid.uuid4().hex,
        _format_time(created),
        _format_time(created + timedelta(minutes=10)),
        document_id,
        source_drawing_sha256,
        device_object_id,
        route_object_id,
        endpoint,
        terminal_name,
        "review_connection_pair",
        False,
    )
    encoded = json.dumps(asdict(request), ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    inbox = workspace_root.resolve(strict=True) / ".ai-engineer" / "autocad" / "review-channel" / "inbox"
    inbox.mkdir(parents=True, exist_ok=True)
    target = inbox / f"{request.request_id}.json"
    _write_new(target, encoded)
    return EnqueuedReview(request, target, hashlib.sha256(encoded).hexdigest())


def load_connection_pair_review_result(path: Path, *, expected_sha256: str) -> ConnectionPairReviewResult:
    """Load and validate one command-channel result by its observed file digest."""
    if _SHA.fullmatch(expected_sha256) is None:
        raise AutoCADContextError("AutoCAD review result expected SHA-256 is invalid.")
    resolved = path.resolve(strict=True)
    if path.is_symlink() or not resolved.is_file():
        raise AutoCADContextError("AutoCAD review result must be a regular non-link file.")
    raw = resolved.read_bytes()
    if not raw or len(raw) > _MAX_BYTES or hashlib.sha256(raw).hexdigest() != expected_sha256:
        raise AutoCADContextError("AutoCAD review result bytes do not match evidence.")
    try:
        value = json.loads(raw.decode("utf-8"))
    except (UnicodeError, json.JSONDecodeError) as exc:
        raise AutoCADContextError("AutoCAD review result must be UTF-8 JSON.") from exc
    return parse_connection_pair_review_result(value)


def parse_connection_pair_review_result(value: object) -> ConnectionPairReviewResult:
    if not isinstance(value, dict):
        raise AutoCADContextError("AutoCAD review result must be an object.")
    common = {"schema_version", "result_version", "result_id", "request_id", "request_sha256", "captured_at", "document_id", "drawing_name", "drawing_units", "layout", "status"}
    detail = {"source_drawing_sha256", "device_object_id", "block_name", "route_object_id", "route_entity_type", "endpoint", "world_position", "alternate_endpoint_distance", "terminal_name", "database_modified_before", "database_modified_after", "approval_method", "drawing_mutation_performed", "error"}
    if set(value) != common | detail:
        raise AutoCADContextError("AutoCAD review result must use the exact completed schema.")
    if value["schema_version"] != 1 or value["result_version"] != RESULT_VERSION:
        raise AutoCADContextError("AutoCAD review result version is unsupported.")
    for item, pattern in ((value["result_id"], _ID), (value["request_id"], _ID), (value["request_sha256"], _SHA), (value["document_id"], _DOCUMENT), (value["source_drawing_sha256"], _SHA), (value["device_object_id"], _HANDLE), (value["route_object_id"], _HANDLE)):
        if not isinstance(item, str) or pattern.fullmatch(item) is None:
            raise AutoCADContextError("AutoCAD review result identity is invalid.")
    _timestamp(value["captured_at"], "result")
    if value["status"] not in {"approved", "rejected"} or value["drawing_mutation_performed"] is not False:
        raise AutoCADContextError("AutoCAD review result status or mutation flag is invalid.")
    if value["route_entity_type"] not in {"LINE", "LWPOLYLINE"} or value["endpoint"] not in {"start", "end"} or value["approval_method"] not in {"engineer_modal_yes_no", "engineer_modal_endpoint_marker_v2"}:
        raise AutoCADContextError("AutoCAD review result selection is invalid.")
    point = value["world_position"]
    if not isinstance(point, list) or len(point) != 3 or any(not isinstance(item, (int, float)) or isinstance(item, bool) for item in point):
        raise AutoCADContextError("AutoCAD review result point is invalid.")
    before, after = value["database_modified_before"], value["database_modified_after"]
    if not isinstance(before, int) or not isinstance(after, int) or before & ~24 or after & ~24:
        raise AutoCADContextError("AutoCAD review result DBMOD is invalid.")
    distance = value["alternate_endpoint_distance"]
    if not isinstance(distance, (int, float)) or isinstance(distance, bool) or distance <= 0:
        raise AutoCADContextError("AutoCAD review alternate endpoint distance is invalid.")
    if value["error"] is not None:
        raise AutoCADContextError("Completed AutoCAD review result must not contain an error.")
    return ConnectionPairReviewResult(
        result_id=value["result_id"], request_id=value["request_id"], request_sha256=value["request_sha256"], captured_at=value["captured_at"], document_id=value["document_id"], drawing_name=value["drawing_name"], drawing_units=value["drawing_units"], layout=value["layout"], status=value["status"], source_drawing_sha256=value["source_drawing_sha256"], device_object_id=value["device_object_id"], block_name=value["block_name"], route_object_id=value["route_object_id"], route_entity_type=value["route_entity_type"], endpoint=value["endpoint"], world_position=tuple(point), alternate_endpoint_distance=float(distance), terminal_name=value["terminal_name"], database_modified_before=before, database_modified_after=after, approval_method=value["approval_method"], drawing_mutation_performed=False, error=None,
    )


def approved_result_to_anchor_capture(result: ConnectionPairReviewResult) -> ConnectionAnchorCapture:
    """Convert an approved modal result into the existing exact anchor-capture contract."""
    if result.status != "approved" or result.world_position is None or result.alternate_endpoint_distance is None:
        raise AutoCADContextError("Only an approved complete result can become anchor evidence.")
    return ConnectionAnchorCapture(
        schema_version=1,
        capture_version="autocad.connection_anchor_capture.v1",
        capture_id=result.result_id,
        captured_at=result.captured_at,
        document=AnchorCaptureDocument(result.document_id, "fingerprint_guid", result.drawing_name, result.drawing_units, result.layout, result.database_modified_before, result.database_modified_after),
        selection=AnchorCaptureSelection(result.device_object_id, result.block_name, result.route_object_id, result.route_entity_type, result.endpoint, result.world_position, result.world_position, 0.0, result.alternate_endpoint_distance, "engineer_confirmed_review_request", result.terminal_name),
        drawing_mutation_performed=False,
    )


def validate_review_result_binding(
    result: ConnectionPairReviewResult,
    queued: EnqueuedReview,
) -> None:
    """Require the result to match every safety-significant request field."""
    request = queued.request
    if result.request_id != request.request_id or result.request_sha256 != queued.request_sha256:
        raise AutoCADContextError("AutoCAD review result does not match the exact request evidence.")
    if (
        result.document_id != request.document_id
        or result.source_drawing_sha256 != request.source_drawing_sha256
        or result.device_object_id != request.device_object_id
        or result.route_object_id != request.route_object_id
        or result.endpoint != request.endpoint
        or result.terminal_name != request.terminal_name
    ):
        raise AutoCADContextError("AutoCAD review result fields do not match the exact request.")


def store_approved_anchor_capture(
    workspace_root: Path,
    *,
    result: ConnectionPairReviewResult,
    queued: EnqueuedReview,
) -> StoredAnchorCapture:
    """Validate request binding and atomically retain approved evidence for assembly."""
    validate_review_result_binding(result, queued)
    capture = approved_result_to_anchor_capture(result)
    encoded = json.dumps(asdict(capture), ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    target_root = workspace_root.resolve(strict=True) / ".ai-engineer" / "autocad" / "connection-anchors" / "inbox"
    target_root.mkdir(parents=True, exist_ok=True)
    target = target_root / f"{capture.capture_id}.json"
    _write_new(target, encoded)
    return StoredAnchorCapture(capture, target, hashlib.sha256(encoded).hexdigest())


def _timestamp(value: object, label: str) -> datetime:
    if not isinstance(value, str) or not value.endswith("Z"):
        raise AutoCADContextError(f"AutoCAD review {label} timestamp must be UTC.")
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as exc:
        raise AutoCADContextError(f"AutoCAD review {label} timestamp is invalid.") from exc
    return parsed


def _format_time(value: datetime) -> str:
    return value.astimezone(UTC).isoformat(timespec="microseconds").replace("+00:00", "Z")


def _write_new(path: Path, content: bytes) -> None:
    descriptor, name = tempfile.mkstemp(dir=path.parent, prefix=f".{path.name}.", suffix=".tmp")
    temporary = Path(name)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
        if path.exists():
            raise AutoCADContextError("AutoCAD review request target already exists.")
        os.replace(temporary, path)
    finally:
        if temporary.exists():
            temporary.unlink()
