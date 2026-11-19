"""Tests for the bounded AutoCAD connection-pair review channel."""

from __future__ import annotations

import hashlib
import json
from dataclasses import replace
from datetime import UTC, datetime
from pathlib import Path

import pytest

from agent.exceptions import AutoCADContextError
from integrations.autocad_review_channel import (
    approved_result_to_anchor_capture,
    enqueue_connection_pair_review,
    load_connection_pair_review_result,
    store_approved_anchor_capture,
    validate_review_result_binding,
)


ROOT = Path(__file__).resolve().parents[1]


def test_review_request_is_atomic_expiring_and_no_mutation(tmp_path: Path) -> None:
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    queued = enqueue_connection_pair_review(
        workspace,
        document_id="12345678-1234-1234-1234-1234567890ab",
        source_drawing_sha256="d" * 64,
        device_object_id="A1",
        route_object_id="B2",
        endpoint="start",
        now=datetime(2026, 8, 16, 10, 0, tzinfo=UTC),
    )
    raw = queued.path.read_bytes()
    assert hashlib.sha256(raw).hexdigest() == queued.request_sha256
    assert json.loads(raw)["drawing_mutation_allowed"] is False
    assert queued.request.requested_action == "review_connection_pair"


def _result() -> dict[str, object]:
    return {
        "schema_version": 1,
        "result_version": "autocad.connection_pair_review_result.v1",
        "result_id": "a" * 32,
        "request_id": "b" * 32,
        "request_sha256": "c" * 64,
        "captured_at": "2026-08-16T10:01:00.0000000Z",
        "document_id": "12345678-1234-1234-1234-1234567890ab",
        "drawing_name": "lighting - Read Only.dwg",
        "drawing_units": 4,
        "layout": "Model",
        "status": "approved",
        "source_drawing_sha256": "d" * 64,
        "device_object_id": "A1",
        "block_name": "L02",
        "route_object_id": "B2",
        "route_entity_type": "LWPOLYLINE",
        "endpoint": "start",
        "world_position": [10.0, 20.0, 0.0],
        "alternate_endpoint_distance": 500.0,
        "terminal_name": "CIRCUIT",
        "database_modified_before": 0,
        "database_modified_after": 16,
        "approval_method": "engineer_modal_yes_no",
        "drawing_mutation_performed": False,
        "error": None,
    }


def test_approved_result_is_hash_bound_and_becomes_anchor_capture(tmp_path: Path) -> None:
    encoded = json.dumps(_result(), separators=(",", ":")).encode("utf-8")
    path = tmp_path / "result.json"
    path.write_bytes(encoded)
    digest = hashlib.sha256(encoded).hexdigest()
    result = load_connection_pair_review_result(path, expected_sha256=digest)
    capture = approved_result_to_anchor_capture(result)
    assert capture.capture_id == "a" * 32
    assert capture.selection.endpoint_selection_method == "engineer_confirmed_review_request"
    assert capture.selection.endpoint_pick_distance == 0
    assert capture.drawing_mutation_performed is False
    with pytest.raises(AutoCADContextError, match="match evidence"):
        load_connection_pair_review_result(path, expected_sha256="0" * 64)


def test_result_binding_and_atomic_capture_storage(tmp_path: Path) -> None:
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    queued = enqueue_connection_pair_review(
        workspace,
        document_id="12345678-1234-1234-1234-1234567890ab",
        source_drawing_sha256="d" * 64,
        device_object_id="A1",
        route_object_id="B2",
        endpoint="start",
        now=datetime(2026, 8, 16, 10, 0, tzinfo=UTC),
    )
    raw = _result()
    raw["request_id"] = queued.request.request_id
    raw["request_sha256"] = queued.request_sha256
    result_path = tmp_path / "result.json"
    encoded = json.dumps(raw, separators=(",", ":")).encode("utf-8")
    result_path.write_bytes(encoded)
    result = load_connection_pair_review_result(
        result_path, expected_sha256=hashlib.sha256(encoded).hexdigest()
    )
    validate_review_result_binding(result, queued)
    stored = store_approved_anchor_capture(workspace, result=result, queued=queued)
    assert stored.path.is_file()
    assert hashlib.sha256(stored.path.read_bytes()).hexdigest() == stored.capture_sha256
    changed = replace(result, endpoint="end")
    with pytest.raises(AutoCADContextError, match="fields"):
        validate_review_result_binding(changed, queued)


def test_result_rejects_mutation_or_unsupported_dbmod(tmp_path: Path) -> None:
    for field, value, message in (
        ("drawing_mutation_performed", True, "mutation"),
        ("database_modified_after", 20, "DBMOD"),
    ):
        raw = _result()
        raw[field] = value
        encoded = json.dumps(raw, separators=(",", ":")).encode("utf-8")
        path = tmp_path / f"{field}.json"
        path.write_bytes(encoded)
        with pytest.raises(AutoCADContextError, match=message):
            load_connection_pair_review_result(
                path, expected_sha256=hashlib.sha256(encoded).hexdigest()
            )


def test_plugin_starts_only_the_allowlisted_review_channel() -> None:
    context = (ROOT / "integrations" / "autocad_context_plugin" / "ContextCommands.cs").read_text(encoding="utf-8")
    channel = (ROOT / "integrations" / "autocad_context_plugin" / "ConnectionReviewChannel.cs").read_text(encoding="utf-8")
    build = (ROOT / "integrations" / "autocad_context_plugin" / "build.ps1").read_text(encoding="utf-8")
    assert "ConnectionReviewChannel.Start();" in context
    assert '"review_connection_pair"' in channel
    assert '"drawing_mutation_allowed"' in channel
    assert "MessageBoxButtons.YesNo" in channel
    assert "SetCurrentView" in channel
    assert "DrawEndpointMarker" in channel
    assert "RED CROSS = exact reviewed route endpoint" in channel
    assert '"engineer_modal_endpoint_marker_v2"' in channel
    assert "route.GeometricExtents" not in channel
    assert "SendStringToExecute" not in channel
    assert "ConnectionReviewChannel.cs" in build
