"""Tests for the initial read-only Gemini Code Assist MCP boundary."""

from __future__ import annotations

import io
import json

import pytest

from integrations.gemini_mcp_server import (
    LocalApiConfig,
    McpServer,
    McpServerError,
    _validate_status_arguments,
    serve,
    tool_definition,
)


def test_only_fixed_read_only_status_tool_is_exposed():
    definition = tool_definition()

    assert definition["name"] == "ai_engineer_autocad_status"
    assert "Does not read drawing entities" in definition["description"]
    assert definition["inputSchema"]["additionalProperties"] is False
    assert "command" not in definition["inputSchema"]["properties"]


def test_status_arguments_reject_command_text_and_unsafe_names():
    assert _validate_status_arguments({}) == {"drawing_name": None, "timeout_seconds": 30.0}
    assert _validate_status_arguments({"drawing_name": "lighting.dwg", "timeout_seconds": 5}) == {
        "drawing_name": "lighting.dwg",
        "timeout_seconds": 5.0,
    }
    with pytest.raises(McpServerError, match="Unexpected tool arguments"):
        _validate_status_arguments({"command": "_.ERASE ALL"})
    with pytest.raises(McpServerError, match="exact bounded DWG"):
        _validate_status_arguments({"drawing_name": "..\\unsafe.dwg"})


def test_environment_requires_loopback_port_and_session_token(monkeypatch: pytest.MonkeyPatch):
    monkeypatch.setenv("AI_ENGINEER_API_PORT", "8766")
    monkeypatch.setenv("AI_ENGINEER_API_TOKEN", "x" * 32)
    assert LocalApiConfig.from_environment() == LocalApiConfig(8766, "x" * 32)
    monkeypatch.setenv("AI_ENGINEER_API_TOKEN", "short")
    with pytest.raises(McpServerError, match="unavailable"):
        LocalApiConfig.from_environment()


def test_mcp_initialize_lists_and_calls_status_without_mutation():
    observed = []

    def status(arguments):
        observed.append(arguments)
        return {
            "document_name": "lighting.dwg",
            "dbmod_before": 0,
            "dbmod_after": 0,
            "drawing_mutation_performed": False,
        }

    server = McpServer(status)
    initialized = server.handle(
        {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2025-06-18"}}
    )
    listed = server.handle({"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}})
    called = server.handle(
        {
            "jsonrpc": "2.0",
            "id": 3,
            "method": "tools/call",
            "params": {"name": "ai_engineer_autocad_status", "arguments": {"drawing_name": "lighting.dwg"}},
        }
    )

    assert initialized["result"]["serverInfo"]["name"] == "ai-engineer-autocad-readonly"
    assert [item["name"] for item in listed["result"]["tools"]] == ["ai_engineer_autocad_status"]
    assert called["result"]["structuredContent"]["drawing_mutation_performed"] is False
    assert observed == [{"drawing_name": "lighting.dwg"}]


def test_stdio_protocol_ignores_notifications_and_returns_bounded_json():
    requests = b"\n".join(
        [
            json.dumps({"jsonrpc": "2.0", "id": 1, "method": "tools/list", "params": {}}).encode(),
            json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized", "params": {}}).encode(),
        ]
    ) + b"\n"
    output = io.BytesIO()
    assert serve(io.BytesIO(requests), output, McpServer(lambda _: {})) == 0
    messages = [json.loads(line) for line in output.getvalue().splitlines()]
    assert len(messages) == 1
    assert messages[0]["id"] == 1
