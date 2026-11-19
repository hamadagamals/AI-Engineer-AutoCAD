"""Tests for provider-planned, allowlisted live AutoCAD tools."""

from __future__ import annotations

import threading
from datetime import UTC, datetime, timedelta
from pathlib import Path

import pytest

import agent.core_commands as core_commands
from agent.command_engine import CommandContext, CommandRegistry
from agent.core_commands import CoreServices, register_core_commands
from agent.permissions import RiskLevel
from integrations.autocad_operator_channel import (
    ActiveAutoCADDocument,
    AutoCADOperatorResult,
)


DOCUMENT_ID = "12345678-1234-1234-1234-1234567890ab"
SOURCE_SHA = "d" * 64


def _registry(workspace: Path) -> CommandRegistry:
    services = CoreServices(
        files=None,  # type: ignore[arg-type]
        scanner=None,  # type: ignore[arg-type]
        system=None,  # type: ignore[arg-type]
        processes=None,  # type: ignore[arg-type]
        vscode=None,  # type: ignore[arg-type]
        autocad=None,  # type: ignore[arg-type]
        terminal=None,  # type: ignore[arg-type]
        operator_workspace_root=workspace,
    )
    registry = CommandRegistry()
    register_core_commands(registry, services)
    return registry


def test_live_autocad_tools_are_fixed_and_risk_scoped(tmp_path: Path) -> None:
    registry = _registry(tmp_path)
    expected = {
        "autocad.get_live_document_status": RiskLevel.LOW,
        "autocad.show_live_palette": RiskLevel.MODERATE,
        "autocad.capture_live_drawing_index": RiskLevel.HIGH,
    }

    for name, risk in expected.items():
        definition = registry.get(name)
        assert definition.risk is risk
        assert definition.permission == "autocad.context.read"
        assert "no command text is accepted" in definition.payload_contract


def test_live_autocad_tool_payload_rejects_command_text(tmp_path: Path) -> None:
    definition = _registry(tmp_path).get("autocad.get_live_document_status")

    assert definition.validator({}) == {
        "drawing_name": None,
        "timeout_seconds": 30.0,
    }
    with pytest.raises(ValueError, match="Unexpected fields: command"):
        definition.validator({"command": "_.ERASE ALL"})
    with pytest.raises(ValueError, match="exact bounded DWG file name"):
        definition.validator({"drawing_name": "..\\unsafe.dwg"})


def test_live_autocad_index_returns_metadata_without_paths_or_content(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    status_path = tmp_path / "status.json"
    active = ActiveAutoCADDocument(
        "2026-08-25T10:00:00.000000Z",
        DOCUMENT_ID,
        "lighting.dwg",
        SOURCE_SHA,
        0,
        True,
        status_path,
        "a" * 64,
    )
    result = AutoCADOperatorResult(
        1,
        "autocad.operator_command_result.v1",
        "b" * 32,
        "c" * 32,
        "e" * 64,
        "2026-08-25T10:00:01.000000Z",
        DOCUMENT_ID,
        "lighting.dwg",
        SOURCE_SHA,
        "capture_drawing_index",
        "completed",
        0,
        0,
        "f" * 32,
        "1" * 64,
        1792,
        240.5,
        False,
        None,
    )
    monkeypatch.setattr(
        core_commands, "discover_active_documents", lambda root: (active,)
    )
    monkeypatch.setattr(
        core_commands, "enqueue_operator_command", lambda *args, **kwargs: object()
    )
    monkeypatch.setattr(
        core_commands,
        "wait_for_operator_result",
        lambda *args, **kwargs: (result, tmp_path / "result.json", "2" * 64),
    )

    definition = _registry(tmp_path).get("autocad.capture_live_drawing_index")
    payload = definition.validator(
        {"drawing_name": "lighting.dwg", "timeout_seconds": 5}
    )
    context = CommandContext(
        task_id="gemini-autocad-live-test",
        cancellation_event=threading.Event(),
        deadline=datetime.now(UTC) + timedelta(seconds=10),
    )
    observed = definition.handler(payload, context)

    assert observed["requested_action"] == "capture_drawing_index"
    assert observed["entity_count"] == 1792
    assert observed["drawing_mutation_performed"] is False
    assert "result_path" not in observed
    assert "drawing_content" not in observed
