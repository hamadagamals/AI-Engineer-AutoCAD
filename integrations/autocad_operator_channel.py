"""Exact, allowlisted, no-mutation command channel for the AutoCAD plugin."""

from __future__ import annotations

import hashlib
import argparse
import json
import os
import re
import tempfile
import time
import uuid
from dataclasses import asdict, dataclass
from datetime import UTC, datetime, timedelta
from pathlib import Path
from typing import Sequence

from agent.exceptions import AutoCADContextError


REQUEST_VERSION = "autocad.operator_command_request.v1"
RESULT_VERSION = "autocad.operator_command_result.v1"
CANCEL_VERSION = "autocad.operator_cancel_request.v1"
ALLOWED_ACTIONS = frozenset(
    {
        "capture_drawing_context",
        "capture_drawing_index",
        "capture_drawing_delta",
        "get_document_status",
        "show_palette",
    }
)
_ID = re.compile(r"[0-9a-f]{32}")
_DOCUMENT = re.compile(
    r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}"
)
_SHA = re.compile(r"[0-9a-f]{64}")
_MAX_BYTES = 16_384
_STATUS_VERSION = "autocad.active_document_status.v1"


@dataclass(frozen=True, slots=True)
class AutoCADOperatorRequest:
    schema_version: int
    request_version: str
    request_id: str
    created_at: str
    expires_at: str
    document_id: str
    source_drawing_sha256: str
    requested_action: str
    drawing_mutation_allowed: bool

    def __post_init__(self) -> None:
        if self.schema_version != 1 or self.request_version != REQUEST_VERSION:
            raise AutoCADContextError("AutoCAD operator request version is unsupported.")
        if _ID.fullmatch(self.request_id) is None:
            raise AutoCADContextError("AutoCAD operator request ID is invalid.")
        if _DOCUMENT.fullmatch(self.document_id) is None:
            raise AutoCADContextError("AutoCAD operator document ID is invalid.")
        if _SHA.fullmatch(self.source_drawing_sha256) is None:
            raise AutoCADContextError("AutoCAD operator source SHA-256 is invalid.")
        if self.requested_action not in ALLOWED_ACTIONS:
            raise AutoCADContextError("AutoCAD operator action is not allowlisted.")
        if self.drawing_mutation_allowed is not False:
            raise AutoCADContextError("AutoCAD operator channel cannot authorize mutation.")
        created = _timestamp(self.created_at, "created")
        expires = _timestamp(self.expires_at, "expiry")
        if not created < expires <= created + timedelta(minutes=10):
            raise AutoCADContextError("AutoCAD operator request lifetime is invalid.")


@dataclass(frozen=True, slots=True)
class AutoCADOperatorResult:
    schema_version: int
    result_version: str
    result_id: str
    request_id: str
    request_sha256: str
    completed_at: str
    document_id: str
    drawing_name: str
    source_drawing_sha256: str
    requested_action: str
    status: str
    database_modified_before: int | None
    database_modified_after: int | None
    context_id: str | None
    context_sha256: str | None
    entity_count: int | None
    duration_milliseconds: float | None
    drawing_mutation_performed: bool
    error: str | None


@dataclass(frozen=True, slots=True)
class EnqueuedOperatorCommand:
    request: AutoCADOperatorRequest
    path: Path
    request_sha256: str


@dataclass(frozen=True, slots=True)
class EnqueuedOperatorCancellation:
    path: Path
    cancellation_sha256: str


@dataclass(frozen=True, slots=True)
class ActiveAutoCADDocument:
    """Fresh local status published by the idle AutoCAD plugin."""

    observed_at: str
    document_id: str
    drawing_name: str
    source_drawing_sha256: str
    database_modified: int
    read_only_channel_ready: bool
    status_path: Path
    status_sha256: str


def enqueue_operator_command(
    workspace_root: Path,
    *,
    document_id: str,
    source_drawing_sha256: str,
    requested_action: str,
    now: datetime | None = None,
) -> EnqueuedOperatorCommand:
    """Atomically enqueue one exact, expiring, read-only AutoCAD action."""
    root = workspace_root.resolve(strict=True)
    created = (now or datetime.now(UTC)).astimezone(UTC)
    request = AutoCADOperatorRequest(
        1,
        REQUEST_VERSION,
        uuid.uuid4().hex,
        _format_time(created),
        _format_time(created + timedelta(minutes=5)),
        document_id,
        source_drawing_sha256,
        requested_action,
        False,
    )
    encoded = json.dumps(
        asdict(request), ensure_ascii=False, separators=(",", ":")
    ).encode("utf-8")
    inbox = root / ".ai-engineer" / "autocad" / "operator-channel" / "inbox"
    inbox.mkdir(parents=True, exist_ok=True)
    target = inbox / f"{request.request_id}.json"
    _write_new(target, encoded)
    return EnqueuedOperatorCommand(
        request, target, hashlib.sha256(encoded).hexdigest()
    )


def discover_active_documents(
    workspace_root: Path,
    *,
    now: datetime | None = None,
    maximum_age_seconds: float = 10.0,
) -> tuple[ActiveAutoCADDocument, ...]:
    """Discover fresh idle-document beacons without attaching to the AutoCAD GUI."""
    if not 1 <= maximum_age_seconds <= 60:
        raise AutoCADContextError("AutoCAD status age must be from 1 through 60 seconds.")
    root = workspace_root.resolve(strict=True)
    directory = root / ".ai-engineer" / "autocad" / "operator-channel" / "documents"
    if not directory.exists():
        return ()
    resolved_directory = directory.resolve(strict=True)
    if directory.is_symlink() or not resolved_directory.is_dir() or not resolved_directory.is_relative_to(root):
        raise AutoCADContextError("AutoCAD status directory is unsafe.")
    paths = sorted(directory.glob("*.json"))
    if len(paths) > 32:
        raise AutoCADContextError("AutoCAD active-document count exceeds 32.")
    current = (now or datetime.now(UTC)).astimezone(UTC)
    documents: list[ActiveAutoCADDocument] = []
    for path in paths:
        if path.is_symlink() or not path.is_file() or _DOCUMENT.fullmatch(path.stem) is None:
            raise AutoCADContextError("AutoCAD status evidence filename is invalid.")
        raw = path.read_bytes()
        if not raw or len(raw) > _MAX_BYTES:
            raise AutoCADContextError("AutoCAD status evidence size is invalid.")
        try:
            value = json.loads(raw.decode("utf-8"))
        except (UnicodeError, json.JSONDecodeError) as exc:
            raise AutoCADContextError("AutoCAD status evidence must be UTF-8 JSON.") from exc
        document = _parse_active_document(
            value,
            path=path.resolve(strict=True),
            evidence_sha256=hashlib.sha256(raw).hexdigest(),
        )
        observed = _timestamp(document.observed_at, "status")
        age = (current - observed).total_seconds()
        if -1 <= age <= maximum_age_seconds and document.read_only_channel_ready:
            documents.append(document)
    return tuple(documents)


def load_operator_result(
    path: Path, *, expected_sha256: str
) -> AutoCADOperatorResult:
    """Load one exact result file by its observed SHA-256."""
    if _SHA.fullmatch(expected_sha256) is None:
        raise AutoCADContextError("AutoCAD operator result SHA-256 is invalid.")
    resolved = path.resolve(strict=True)
    if path.is_symlink() or not resolved.is_file():
        raise AutoCADContextError("AutoCAD operator result must be a regular non-link file.")
    raw = resolved.read_bytes()
    if (
        not raw
        or len(raw) > _MAX_BYTES
        or hashlib.sha256(raw).hexdigest() != expected_sha256
    ):
        raise AutoCADContextError("AutoCAD operator result bytes do not match evidence.")
    try:
        value = json.loads(raw.decode("utf-8"))
    except (UnicodeError, json.JSONDecodeError) as exc:
        raise AutoCADContextError("AutoCAD operator result must be UTF-8 JSON.") from exc
    return parse_operator_result(value)


def parse_operator_result(value: object) -> AutoCADOperatorResult:
    """Strictly parse a completed or fail-closed channel result."""
    if not isinstance(value, dict):
        raise AutoCADContextError("AutoCAD operator result must be an object.")
    expected = {
        "schema_version", "result_version", "result_id", "request_id",
        "request_sha256", "completed_at", "document_id", "drawing_name",
        "source_drawing_sha256", "requested_action", "status",
        "database_modified_before", "database_modified_after", "context_id",
        "context_sha256", "entity_count", "duration_milliseconds",
        "drawing_mutation_performed", "error",
    }
    if set(value) != expected:
        raise AutoCADContextError("AutoCAD operator result schema is invalid.")
    if value["schema_version"] != 1 or value["result_version"] != RESULT_VERSION:
        raise AutoCADContextError("AutoCAD operator result version is unsupported.")
    for item, pattern in (
        (value["result_id"], _ID), (value["request_id"], _ID),
        (value["request_sha256"], _SHA), (value["document_id"], _DOCUMENT),
        (value["source_drawing_sha256"], _SHA),
    ):
        if not isinstance(item, str) or pattern.fullmatch(item) is None:
            raise AutoCADContextError("AutoCAD operator result identity is invalid.")
    _timestamp(value["completed_at"], "result")
    if value["requested_action"] not in ALLOWED_ACTIONS:
        raise AutoCADContextError("AutoCAD operator result action is unsupported.")
    if value["drawing_mutation_performed"] is not False:
        raise AutoCADContextError("AutoCAD operator result claims drawing mutation.")
    status = value["status"]
    if status not in {"completed", "cancelled", "error"}:
        raise AutoCADContextError("AutoCAD operator result status is invalid.")
    before = value["database_modified_before"]
    after = value["database_modified_after"]
    if status == "error":
        if not isinstance(value["error"], str) or not value["error"]:
            raise AutoCADContextError("AutoCAD operator error result lacks an error.")
        if any(
            value[key] is not None
            for key in (
                "database_modified_before", "database_modified_after", "context_id",
                "context_sha256", "entity_count", "duration_milliseconds",
            )
        ):
            raise AutoCADContextError("AutoCAD operator error details must be empty.")
    elif status == "cancelled":
        if value["error"] is not None:
            raise AutoCADContextError("Cancelled AutoCAD operator result contains an error.")
        if any(
            value[key] is not None
            for key in (
                "database_modified_before", "database_modified_after", "context_id",
                "context_sha256", "entity_count", "duration_milliseconds",
            )
        ):
            raise AutoCADContextError("Cancelled AutoCAD operator details must be empty.")
    else:
        if value["error"] is not None:
            raise AutoCADContextError("Completed AutoCAD operator result contains an error.")
        if (
            not isinstance(before, int) or isinstance(before, bool)
            or not isinstance(after, int) or isinstance(after, bool)
            or before & ~24 or after & ~24
        ):
            raise AutoCADContextError("AutoCAD operator result DBMOD is invalid.")
        is_context_capture = value["requested_action"] == "capture_drawing_context"
        is_index_capture = value["requested_action"] == "capture_drawing_index"
        is_delta_capture = value["requested_action"] == "capture_drawing_delta"
        is_capture = is_context_capture or is_index_capture or is_delta_capture
        context_values = (
            value["context_id"], value["context_sha256"], value["entity_count"],
            value["duration_milliseconds"],
        )
        if is_capture:
            if (
                not isinstance(context_values[0], str)
                or _ID.fullmatch(context_values[0]) is None
                or not isinstance(context_values[1], str)
                or _SHA.fullmatch(context_values[1]) is None
                or not isinstance(context_values[2], int)
                or isinstance(context_values[2], bool)
                or not 0 <= context_values[2] <= (
                    200_000 if is_index_capture else 4_096 if is_delta_capture else 512
                )
                or not isinstance(context_values[3], (int, float))
                or isinstance(context_values[3], bool)
                or not 0 <= float(context_values[3]) <= 30_000
            ):
                raise AutoCADContextError("AutoCAD operator context evidence is invalid.")
        elif any(item is not None for item in context_values):
            raise AutoCADContextError("Non-capture operator result contains context evidence.")
    return AutoCADOperatorResult(**value)


def validate_operator_result_binding(
    result: AutoCADOperatorResult, queued: EnqueuedOperatorCommand
) -> None:
    """Bind result identity and every request capability field."""
    request = queued.request
    if (
        result.request_id != request.request_id
        or result.request_sha256 != queued.request_sha256
        or result.document_id != request.document_id
        or result.source_drawing_sha256 != request.source_drawing_sha256
        or result.requested_action != request.requested_action
    ):
        raise AutoCADContextError(
            "AutoCAD operator result does not match the exact request evidence."
        )


def cancel_operator_command(
    workspace_root: Path,
    *,
    queued: EnqueuedOperatorCommand,
    now: datetime | None = None,
) -> EnqueuedOperatorCancellation:
    """Atomically request cancellation of one exact queued or running command."""
    root = workspace_root.resolve(strict=True)
    payload = {
        "schema_version": 1,
        "cancel_version": CANCEL_VERSION,
        "request_id": queued.request.request_id,
        "request_sha256": queued.request_sha256,
        "requested_at": _format_time((now or datetime.now(UTC)).astimezone(UTC)),
    }
    encoded = json.dumps(payload, separators=(",", ":")).encode("utf-8")
    cancel_root = root / ".ai-engineer" / "autocad" / "operator-channel" / "cancel"
    cancel_root.mkdir(parents=True, exist_ok=True)
    target = cancel_root / f"{queued.request.request_id}.json"
    _write_new(target, encoded)
    return EnqueuedOperatorCancellation(target, hashlib.sha256(encoded).hexdigest())


def wait_for_operator_result(
    workspace_root: Path,
    *,
    queued: EnqueuedOperatorCommand,
    timeout_seconds: float = 30.0,
) -> tuple[AutoCADOperatorResult, Path, str]:
    """Boundedly wait for and validate the exact result produced by AutoCAD."""
    if not 0 < timeout_seconds <= 120:
        raise AutoCADContextError("AutoCAD operator wait must be from 0 through 120 seconds.")
    outbox = (
        workspace_root.resolve(strict=True)
        / ".ai-engineer" / "autocad" / "operator-channel" / "outbox"
    )
    target = outbox / f"{queued.request.request_id}.json"
    deadline = time.monotonic() + timeout_seconds
    while time.monotonic() < deadline:
        if target.is_file() and not target.is_symlink():
            raw = target.read_bytes()
            digest = hashlib.sha256(raw).hexdigest()
            result = load_operator_result(target, expected_sha256=digest)
            validate_operator_result_binding(result, queued)
            return result, target, digest
        time.sleep(0.1)
    raise AutoCADContextError("AutoCAD operator result did not arrive before timeout.")


def _parse_active_document(
    value: object,
    *,
    path: Path,
    evidence_sha256: str,
) -> ActiveAutoCADDocument:
    if not isinstance(value, dict) or set(value) != {
        "schema_version",
        "status_version",
        "observed_at",
        "document_id",
        "drawing_name",
        "source_drawing_sha256",
        "database_modified",
        "read_only_channel_ready",
        "drawing_mutation_performed",
    }:
        raise AutoCADContextError("AutoCAD active-document status schema is invalid.")
    if value["schema_version"] != 1 or value["status_version"] != _STATUS_VERSION:
        raise AutoCADContextError("AutoCAD active-document status version is unsupported.")
    _timestamp(value["observed_at"], "status")
    if (
        not isinstance(value["document_id"], str)
        or _DOCUMENT.fullmatch(value["document_id"]) is None
        or value["document_id"] != path.stem
        or not isinstance(value["source_drawing_sha256"], str)
        or _SHA.fullmatch(value["source_drawing_sha256"]) is None
    ):
        raise AutoCADContextError("AutoCAD active-document status identity is invalid.")
    drawing_name = value["drawing_name"]
    if (
        not isinstance(drawing_name, str)
        or not drawing_name
        or len(drawing_name.encode("utf-8")) > 4096
        or "/" in drawing_name
        or "\\" in drawing_name
    ):
        raise AutoCADContextError("AutoCAD active-document drawing name is invalid.")
    dbmod = value["database_modified"]
    if (
        not isinstance(dbmod, int)
        or isinstance(dbmod, bool)
        or dbmod < 0
        or dbmod > 63
    ):
        raise AutoCADContextError("AutoCAD active-document DBMOD is invalid.")
    ready = value["read_only_channel_ready"]
    if not isinstance(ready, bool) or ready != ((dbmod & ~24) == 0):
        raise AutoCADContextError("AutoCAD active-document readiness is inconsistent.")
    if value["drawing_mutation_performed"] is not False:
        raise AutoCADContextError("AutoCAD active-document status claims mutation.")
    return ActiveAutoCADDocument(
        observed_at=value["observed_at"],
        document_id=value["document_id"],
        drawing_name=drawing_name,
        source_drawing_sha256=value["source_drawing_sha256"],
        database_modified=dbmod,
        read_only_channel_ready=ready,
        status_path=path,
        status_sha256=evidence_sha256,
    )


def _timestamp(value: object, label: str) -> datetime:
    if not isinstance(value, str) or not value.endswith("Z"):
        raise AutoCADContextError(f"AutoCAD operator {label} timestamp must be UTC.")
    try:
        return datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as exc:
        raise AutoCADContextError(
            f"AutoCAD operator {label} timestamp is invalid."
        ) from exc


def _format_time(value: datetime) -> str:
    return value.astimezone(UTC).isoformat(timespec="microseconds").replace(
        "+00:00", "Z"
    )


def _write_new(path: Path, content: bytes) -> None:
    descriptor, name = tempfile.mkstemp(
        dir=path.parent, prefix=f".{path.name}.", suffix=".tmp"
    )
    temporary = Path(name)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
        if path.exists():
            raise AutoCADContextError("AutoCAD operator request already exists.")
        os.replace(temporary, path)
    finally:
        if temporary.exists():
            temporary.unlink()


def main(arguments: Sequence[str] | None = None) -> int:
    """Queue one reviewed action and optionally await its bound result."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--workspace-root", required=True, type=Path)
    parser.add_argument("--discover-active", action="store_true")
    parser.add_argument("--document-id")
    parser.add_argument("--source-drawing-sha256")
    parser.add_argument("--action", choices=sorted(ALLOWED_ACTIONS))
    parser.add_argument("--wait-seconds", type=float, default=30.0)
    parser.add_argument("--cancel-immediately", action="store_true")
    parsed = parser.parse_args(arguments)
    if parsed.discover_active:
        if parsed.document_id or parsed.source_drawing_sha256 or parsed.action:
            parser.error("--discover-active cannot be combined with request fields")
        print(
            json.dumps(
                [asdict(item) for item in discover_active_documents(parsed.workspace_root)],
                ensure_ascii=False,
                separators=(",", ":"),
                default=str,
            )
        )
        return 0
    if not parsed.document_id or not parsed.source_drawing_sha256 or not parsed.action:
        parser.error("request mode requires --document-id, --source-drawing-sha256, and --action")
    queued = enqueue_operator_command(
        parsed.workspace_root,
        document_id=parsed.document_id,
        source_drawing_sha256=parsed.source_drawing_sha256,
        requested_action=parsed.action,
    )
    cancellation = (
        cancel_operator_command(parsed.workspace_root, queued=queued)
        if parsed.cancel_immediately
        else None
    )
    result, path, digest = wait_for_operator_result(
        parsed.workspace_root,
        queued=queued,
        timeout_seconds=parsed.wait_seconds,
    )
    print(
        json.dumps(
            {
                "request": asdict(queued.request),
                "request_sha256": queued.request_sha256,
                "cancellation": (
                    {
                        "path": str(cancellation.path),
                        "sha256": cancellation.cancellation_sha256,
                    }
                    if cancellation is not None
                    else None
                ),
                "result": asdict(result),
                "result_path": str(path),
                "result_sha256": digest,
            },
            ensure_ascii=False,
            separators=(",", ":"),
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
