"""Minimal stdio MCP facade for Gemini Code Assist and AI-Engineer.

The first accepted boundary exposes one fixed read-only AutoCAD status tool.  The
server cannot accept AutoCAD command text, cannot read drawing content, and delegates
execution to the authenticated localhost AI-Engineer task engine.
"""

from __future__ import annotations

import json
import os
import re
import sys
import urllib.error
import urllib.request
from dataclasses import dataclass
from typing import Any, BinaryIO, Callable


SERVER_NAME = "ai-engineer-autocad-readonly"
SERVER_VERSION = "0.1.0"
MAX_MESSAGE_BYTES = 1_048_576
DRAWING_NAME_PATTERN = re.compile(r'^[^\\/:*?"<>|]{1,255}\.dwg$', re.IGNORECASE)


class McpServerError(RuntimeError):
    """Actionable bounded MCP or local API failure."""


@dataclass(frozen=True, slots=True)
class LocalApiConfig:
    """Validated loopback API connection inherited by the MCP child process."""

    port: int
    token: str

    @classmethod
    def from_environment(cls) -> "LocalApiConfig":
        raw_port = os.environ.get("AI_ENGINEER_API_PORT", "8766")
        try:
            port = int(raw_port)
        except ValueError as exc:
            raise McpServerError("AI_ENGINEER_API_PORT is invalid") from exc
        if not 1024 <= port <= 65535:
            raise McpServerError("AI_ENGINEER_API_PORT is outside the allowed range")
        token = os.environ.get("AI_ENGINEER_API_TOKEN", "")
        if len(token) < 32 or any(character.isspace() for character in token):
            raise McpServerError("AI_ENGINEER_API_TOKEN is unavailable in the Gemini process")
        return cls(port=port, token=token)


class LocalApiClient:
    """Small authenticated HTTP client restricted to fixed loopback API routes."""

    def __init__(self, config: LocalApiConfig) -> None:
        self.config = config

    def _request(self, method: str, path: str, body: dict[str, Any]) -> dict[str, Any]:
        payload = json.dumps(body, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        request = urllib.request.Request(
            f"http://127.0.0.1:{self.config.port}{path}",
            data=payload,
            method=method,
            headers={
                "Accept": "application/json",
                "Authorization": f"Bearer {self.config.token}",
                "Content-Type": "application/json",
            },
        )
        try:
            with urllib.request.urlopen(request, timeout=125) as response:
                raw = response.read(MAX_MESSAGE_BYTES + 1)
        except urllib.error.HTTPError as exc:
            detail = exc.read(4096).decode("utf-8", errors="replace")
            raise McpServerError(f"AI-Engineer Local API returned HTTP {exc.code}: {detail}") from exc
        except (OSError, urllib.error.URLError) as exc:
            raise McpServerError(f"AI-Engineer Local API is unavailable: {exc}") from exc
        if len(raw) > MAX_MESSAGE_BYTES:
            raise McpServerError("AI-Engineer Local API response exceeded 1 MiB")
        try:
            value = json.loads(raw.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise McpServerError("AI-Engineer Local API returned invalid JSON") from exc
        if not isinstance(value, dict):
            raise McpServerError("AI-Engineer Local API returned a non-object response")
        return value

    def get_live_document_status(self, arguments: dict[str, Any]) -> dict[str, Any]:
        """Submit and run exactly one LOW-risk fixed status command."""

        payload = _validate_status_arguments(arguments)
        submitted = self._request(
            "POST",
            "/v1/tasks",
            {"command_name": "autocad.get_live_document_status", "payload": payload, "max_attempts": 1},
        )
        task_id = submitted.get("task_id")
        if not isinstance(task_id, str) or not task_id:
            raise McpServerError("AI-Engineer did not return a task identifier")
        completed = self._request("POST", f"/v1/tasks/{task_id}/run", {"approval_id": None})
        if completed.get("status") != "succeeded" or not isinstance(completed.get("result"), dict):
            status = str(completed.get("status") or "unknown")
            error = str(completed.get("error") or "no bounded error supplied")
            raise McpServerError(f"AutoCAD status task did not succeed ({status}): {error}")
        result = completed["result"]
        allowed = {
            "document_name",
            "document_id",
            "source_drawing_sha256",
            "requested_action",
            "dbmod_before",
            "dbmod_after",
            "drawing_mutation_performed",
            "result_sha256",
        }
        return {key: result[key] for key in sorted(allowed) if key in result}


def _validate_status_arguments(arguments: object) -> dict[str, Any]:
    if not isinstance(arguments, dict):
        raise McpServerError("Tool arguments must be an object")
    unexpected = sorted(set(arguments) - {"drawing_name", "timeout_seconds"})
    if unexpected:
        raise McpServerError(f"Unexpected tool arguments: {', '.join(unexpected)}")
    drawing_name = arguments.get("drawing_name")
    if drawing_name is not None and (
        not isinstance(drawing_name, str) or not DRAWING_NAME_PATTERN.fullmatch(drawing_name)
    ):
        raise McpServerError("drawing_name must be an exact bounded DWG file name")
    timeout = arguments.get("timeout_seconds", 30.0)
    if isinstance(timeout, bool) or not isinstance(timeout, (int, float)) or not 1 <= float(timeout) <= 120:
        raise McpServerError("timeout_seconds must be from 1 through 120")
    return {"drawing_name": drawing_name, "timeout_seconds": float(timeout)}


def tool_definition() -> dict[str, Any]:
    """Return the only tool exposed in the initial Gemini boundary."""

    return {
        "name": "ai_engineer_autocad_status",
        "description": (
            "Read only the active AutoCAD document status through AI-Engineer. "
            "Does not read drawing entities and cannot modify or save the drawing."
        ),
        "inputSchema": {
            "type": "object",
            "additionalProperties": False,
            "properties": {
                "drawing_name": {
                    "type": "string",
                    "description": "Optional exact active .dwg file name.",
                    "pattern": r'^[^\\/:*?"<>|]{1,255}\.dwg$',
                },
                "timeout_seconds": {
                    "type": "number",
                    "minimum": 1,
                    "maximum": 120,
                    "default": 30,
                },
            },
        },
    }


class McpServer:
    """Line-delimited JSON-RPC MCP server suitable for local stdio launch."""

    def __init__(self, status_handler: Callable[[dict[str, Any]], dict[str, Any]]) -> None:
        self.status_handler = status_handler

    def handle(self, message: object) -> dict[str, Any] | None:
        if not isinstance(message, dict) or message.get("jsonrpc") != "2.0":
            return self._error(None, -32600, "Invalid JSON-RPC request")
        request_id = message.get("id")
        method = message.get("method")
        if not isinstance(method, str):
            return self._error(request_id, -32600, "JSON-RPC method is required")
        if method.startswith("notifications/"):
            return None
        try:
            if method == "initialize":
                params = message.get("params") if isinstance(message.get("params"), dict) else {}
                requested = params.get("protocolVersion")
                protocol = requested if isinstance(requested, str) and requested else "2025-06-18"
                return self._result(
                    request_id,
                    {
                        "protocolVersion": protocol,
                        "capabilities": {"tools": {"listChanged": False}},
                        "serverInfo": {"name": SERVER_NAME, "version": SERVER_VERSION},
                    },
                )
            if method == "ping":
                return self._result(request_id, {})
            if method == "tools/list":
                return self._result(request_id, {"tools": [tool_definition()]})
            if method == "tools/call":
                params = message.get("params")
                if not isinstance(params, dict) or params.get("name") != "ai_engineer_autocad_status":
                    raise McpServerError("Unknown or missing MCP tool name")
                arguments = params.get("arguments", {})
                result = self.status_handler(arguments)
                return self._result(
                    request_id,
                    {
                        "content": [
                            {
                                "type": "text",
                                "text": json.dumps(result, ensure_ascii=False, sort_keys=True),
                            }
                        ],
                        "structuredContent": result,
                        "isError": False,
                    },
                )
            return self._error(request_id, -32601, "Method not found")
        except McpServerError as exc:
            return self._result(
                request_id,
                {"content": [{"type": "text", "text": str(exc)}], "isError": True},
            )

    @staticmethod
    def _result(request_id: object, value: object) -> dict[str, Any]:
        return {"jsonrpc": "2.0", "id": request_id, "result": value}

    @staticmethod
    def _error(request_id: object, code: int, message: str) -> dict[str, Any]:
        return {"jsonrpc": "2.0", "id": request_id, "error": {"code": code, "message": message}}


def serve(stdin: BinaryIO, stdout: BinaryIO, server: McpServer) -> int:
    """Serve bounded newline-delimited MCP requests until stdin closes."""

    for raw in stdin:
        if not raw.strip():
            continue
        if len(raw) > MAX_MESSAGE_BYTES:
            response = McpServer._error(None, -32600, "MCP request exceeded 1 MiB")
        else:
            try:
                message = json.loads(raw.decode("utf-8"))
                response = server.handle(message)
            except (UnicodeDecodeError, json.JSONDecodeError):
                response = McpServer._error(None, -32700, "Invalid JSON")
        if response is not None:
            stdout.write(json.dumps(response, ensure_ascii=False, separators=(",", ":")).encode("utf-8") + b"\n")
            stdout.flush()
    return 0


def main() -> int:
    config = LocalApiConfig.from_environment()
    client = LocalApiClient(config)
    return serve(sys.stdin.buffer, sys.stdout.buffer, McpServer(client.get_live_document_status))


if __name__ == "__main__":
    raise SystemExit(main())
