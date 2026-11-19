"""Tests for the allowlisted no-mutation AutoCAD operator channel."""

from __future__ import annotations

import hashlib
import json
from dataclasses import replace
from datetime import UTC, datetime
from pathlib import Path

import pytest

from agent.exceptions import AutoCADContextError
from integrations.autocad_operator_channel import (
    cancel_operator_command,
    discover_active_documents,
    enqueue_operator_command,
    load_operator_result,
    validate_operator_result_binding,
)


ROOT = Path(__file__).resolve().parents[1]


def _result(action: str = "capture_drawing_context") -> dict[str, object]:
    capture = action in {
        "capture_drawing_context", "capture_drawing_index", "capture_drawing_delta"
    }
    return {
        "schema_version": 1,
        "result_version": "autocad.operator_command_result.v1",
        "result_id": "a" * 32,
        "request_id": "b" * 32,
        "request_sha256": "c" * 64,
        "completed_at": "2026-08-16T12:00:01.000000Z",
        "document_id": "12345678-1234-1234-1234-1234567890ab",
        "drawing_name": "lighting - Read Only.dwg",
        "source_drawing_sha256": "d" * 64,
        "requested_action": action,
        "status": "completed",
        "database_modified_before": 0,
        "database_modified_after": 0,
        "context_id": "e" * 32 if capture else None,
        "context_sha256": "f" * 64 if capture else None,
        "entity_count": 512 if capture else None,
        "duration_milliseconds": 125.5 if capture else None,
        "drawing_mutation_performed": False,
        "error": None,
    }


def test_operator_request_is_exact_expiring_and_no_mutation(tmp_path: Path) -> None:
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    queued = enqueue_operator_command(
        workspace,
        document_id="12345678-1234-1234-1234-1234567890ab",
        source_drawing_sha256="d" * 64,
        requested_action="capture_drawing_context",
        now=datetime(2026, 8, 16, 12, 0, tzinfo=UTC),
    )
    raw = queued.path.read_bytes()
    assert hashlib.sha256(raw).hexdigest() == queued.request_sha256
    assert json.loads(raw)["drawing_mutation_allowed"] is False
    with pytest.raises(AutoCADContextError, match="allowlisted"):
        replace(queued.request, requested_action="_.ERASE ALL")


def test_operator_result_is_hash_bound_and_request_bound(tmp_path: Path) -> None:
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    queued = enqueue_operator_command(
        workspace,
        document_id="12345678-1234-1234-1234-1234567890ab",
        source_drawing_sha256="d" * 64,
        requested_action="capture_drawing_context",
        now=datetime(2026, 8, 16, 12, 0, tzinfo=UTC),
    )
    raw = _result()
    raw["request_id"] = queued.request.request_id
    raw["request_sha256"] = queued.request_sha256
    path = tmp_path / "result.json"
    encoded = json.dumps(raw, separators=(",", ":")).encode()
    path.write_bytes(encoded)
    result = load_operator_result(
        path, expected_sha256=hashlib.sha256(encoded).hexdigest()
    )
    validate_operator_result_binding(result, queued)
    assert result.entity_count == 512
    assert result.duration_milliseconds == 125.5
    with pytest.raises(AutoCADContextError, match="exact request"):
        validate_operator_result_binding(replace(result, requested_action="show_palette"), queued)


def test_operator_result_rejects_mutation_and_unbound_context() -> None:
    changed = _result()
    changed["drawing_mutation_performed"] = True
    with pytest.raises(AutoCADContextError, match="mutation"):
        from integrations.autocad_operator_channel import parse_operator_result
        parse_operator_result(changed)
    status = _result("get_document_status")
    status["context_id"] = "e" * 32
    with pytest.raises(AutoCADContextError, match="Non-capture"):
        parse_operator_result(status)


def test_operator_drawing_index_allows_bounded_full_drawing_count(
    tmp_path: Path,
) -> None:
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    queued = enqueue_operator_command(
        workspace,
        document_id="12345678-1234-1234-1234-1234567890ab",
        source_drawing_sha256="d" * 64,
        requested_action="capture_drawing_index",
        now=datetime(2026, 8, 16, 12, 0, tzinfo=UTC),
    )
    raw = _result("capture_drawing_index")
    raw["request_id"] = queued.request.request_id
    raw["request_sha256"] = queued.request_sha256
    raw["entity_count"] = 125_000
    path = tmp_path / "drawing-index-result.json"
    encoded = json.dumps(raw, separators=(",", ":")).encode()
    path.write_bytes(encoded)
    result = load_operator_result(
        path, expected_sha256=hashlib.sha256(encoded).hexdigest()
    )
    validate_operator_result_binding(result, queued)
    assert result.requested_action == "capture_drawing_index"
    assert result.entity_count == 125_000

    raw["entity_count"] = 200_001
    from integrations.autocad_operator_channel import parse_operator_result

    with pytest.raises(AutoCADContextError, match="context evidence"):
        parse_operator_result(raw)


def test_operator_drawing_delta_is_bounded_and_allowlisted(tmp_path: Path) -> None:
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    queued = enqueue_operator_command(
        workspace,
        document_id="12345678-1234-1234-1234-1234567890ab",
        source_drawing_sha256="d" * 64,
        requested_action="capture_drawing_delta",
        now=datetime(2026, 8, 16, 12, 0, tzinfo=UTC),
    )
    raw = _result("capture_drawing_delta")
    raw["request_id"] = queued.request.request_id
    raw["request_sha256"] = queued.request_sha256
    raw["entity_count"] = 4_096
    from integrations.autocad_operator_channel import parse_operator_result
    result = parse_operator_result(raw)
    validate_operator_result_binding(result, queued)
    assert result.entity_count == 4_096
    raw["entity_count"] = 4_097
    with pytest.raises(AutoCADContextError, match="context evidence"):
        parse_operator_result(raw)


def test_active_document_beacon_bootstraps_exact_request_identity(
    tmp_path: Path,
) -> None:
    workspace = tmp_path / "workspace"
    documents = (
        workspace
        / ".ai-engineer"
        / "autocad"
        / "operator-channel"
        / "documents"
    )
    documents.mkdir(parents=True)
    document_id = "12345678-1234-1234-1234-1234567890ab"
    status = {
        "schema_version": 1,
        "status_version": "autocad.active_document_status.v1",
        "observed_at": "2026-08-16T12:00:00.000000Z",
        "document_id": document_id,
        "drawing_name": "lighting.dwg",
        "source_drawing_sha256": "d" * 64,
        "database_modified": 16,
        "read_only_channel_ready": True,
        "drawing_mutation_performed": False,
    }
    (documents / f"{document_id}.json").write_text(
        json.dumps(status, separators=(",", ":")), encoding="utf-8"
    )

    active = discover_active_documents(
        workspace,
        now=datetime(2026, 8, 16, 12, 0, 5, tzinfo=UTC),
    )

    assert len(active) == 1
    assert active[0].drawing_name == "lighting.dwg"
    assert active[0].database_modified == 16
    queued = enqueue_operator_command(
        workspace,
        document_id=active[0].document_id,
        source_drawing_sha256=active[0].source_drawing_sha256,
        requested_action="capture_drawing_index",
        now=datetime(2026, 8, 16, 12, 0, 5, tzinfo=UTC),
    )
    assert queued.request.document_id == active[0].document_id

    status["database_modified"] = 1
    status["read_only_channel_ready"] = False
    (documents / f"{document_id}.json").write_text(
        json.dumps(status, separators=(",", ":")), encoding="utf-8"
    )
    assert discover_active_documents(
        workspace,
        now=datetime(2026, 8, 16, 12, 0, 5, tzinfo=UTC),
    ) == ()


def test_operator_cancellation_is_exact_and_cancelled_result_is_bounded(
    tmp_path: Path,
) -> None:
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    queued = enqueue_operator_command(
        workspace,
        document_id="12345678-1234-1234-1234-1234567890ab",
        source_drawing_sha256="d" * 64,
        requested_action="capture_drawing_context",
        now=datetime(2026, 8, 16, 12, 0, tzinfo=UTC),
    )
    cancellation = cancel_operator_command(
        workspace,
        queued=queued,
        now=datetime(2026, 8, 16, 12, 0, 1, tzinfo=UTC),
    )
    payload = json.loads(cancellation.path.read_text(encoding="utf-8"))
    assert payload["request_id"] == queued.request.request_id
    assert payload["request_sha256"] == queued.request_sha256
    assert hashlib.sha256(cancellation.path.read_bytes()).hexdigest() == cancellation.cancellation_sha256

    from integrations.autocad_operator_channel import parse_operator_result
    raw = _result()
    raw.update(
        status="cancelled",
        database_modified_before=None,
        database_modified_after=None,
        context_id=None,
        context_sha256=None,
        entity_count=None,
        duration_milliseconds=None,
    )
    assert parse_operator_result(raw).status == "cancelled"


def test_plugin_channel_is_allowlisted_and_has_no_command_string_path() -> None:
    context = (ROOT / "integrations" / "autocad_context_plugin" / "ContextCommands.cs").read_text(encoding="utf-8")
    channel = (ROOT / "integrations" / "autocad_context_plugin" / "OperatorCommandChannel.cs").read_text(encoding="utf-8")
    build = (ROOT / "integrations" / "autocad_context_plugin" / "build.ps1").read_text(encoding="utf-8")
    assert "OperatorCommandChannel.Start();" in context
    assert "OperatorCommandChannel.TryPublishNow();" in context
    assert "PublishDocumentStatus(document, root, false);" in channel
    assert "PublishDocumentStatus(document, root, true);" in channel
    assert 'CommandMethod("AIENGOPERATORPULSE", CommandFlags.NoHistory)' in context
    assert "OperatorCommandChannel.TryPublishNow()" in context
    assert "operator status failed safely" in context
    assert "public static bool TryPublishNow()" in channel
    assert "RecordPulseFailure(error);" in channel
    assert '"operator-pulse-error.json"' in channel
    assert '"drawing_mutation_performed", false' in channel
    assert "Operator status pulse requires one idle active document" not in channel
    assert '"capture_drawing_context"' in channel
    assert '"capture_drawing_index"' in channel
    assert '"capture_drawing_delta"' in channel
    assert '"get_document_status"' in channel
    assert '"show_palette"' in channel
    assert "ContextCommands.ExportActiveDrawingForChannel(" in channel
    assert "ContextCommands.ExportActiveDrawingIndexForChannel(" in channel
    assert "ContextCommands.ExportActiveDrawingDeltaForChannel(" in channel
    assert "IsCancellationRequested" in channel
    assert "OperationCanceledException" in channel
    assert "drawing_mutation_performed" in channel
    assert "SendStringToExecute" not in channel
    assert "OpenMode.ForWrite" not in channel
    assert "OperatorCommandChannel.cs" in build
    assert "DrawingChangeTracker.cs" in build
    tracker = (ROOT / "integrations" / "autocad_context_plugin" / "DrawingChangeTracker.cs").read_text(encoding="utf-8")
    assert "document.Database.UnmanagedObject" in tracker
    assert "Object.ReferenceEquals(_database, document.Database)" not in tracker
    assert "ObjectAppended" in tracker
    assert "ObjectModified" in tracker
    assert "ObjectErased" in tracker
    assert "OpenMode.ForWrite" not in tracker
