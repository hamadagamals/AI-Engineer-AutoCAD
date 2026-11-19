"""Tests for the opt-in, fixed-endpoint Gemini reasoning provider."""

from __future__ import annotations

import json
import io
import urllib.error
from pathlib import Path

import pytest

from agent.exceptions import ApiConfigurationError, ProviderError
from agent.operator_runtime import OperatorRuntimeConfig, build_operator_providers
from agent.provider import ProviderCommand, ProviderPhase, ProviderRequest
from integrations.gemini_provider import (
    GeminiInteractionsClient,
    GeminiProviderConfig,
    _extract_output_text,
)
from integrations.gemini_provider import _post_without_redirects_or_proxies


def _request() -> ProviderRequest:
    return ProviderRequest(
        run_id="a" * 32,
        phase=ProviderPhase.PLAN,
        goal="Inspect safely",
        plan_summary=None,
        plan_steps=(),
        action_number=1,
        maximum_actions=5,
        available_commands=(
            ProviderCommand("file.read", "Read file", "file.read", "low"),
        ),
    )


def _environment() -> dict[str, str]:
    return {
        "AI_ENGINEER_PROVIDER": "gemini",
        "AI_ENGINEER_GEMINI_ENABLE": "1",
        "AI_ENGINEER_GEMINI_EGRESS_CONFIRMED": "1",
        "GEMINI_API_KEY": "gemini-test-abcdefghijklmnopqrstuvwxyz123456",
        "AI_ENGINEER_OPERATOR_BACKUP_ROOT": "C:\\operator-backup",
    }


def test_gemini_config_requires_enable_egress_and_environment_key() -> None:
    with pytest.raises(ApiConfigurationError, match="disabled"):
        GeminiProviderConfig.from_environment({})
    with pytest.raises(ApiConfigurationError, match="egress"):
        GeminiProviderConfig.from_environment({"AI_ENGINEER_GEMINI_ENABLE": "1"})
    with pytest.raises(ApiConfigurationError, match="GEMINI_API_KEY"):
        GeminiProviderConfig.from_environment(
            {
                "AI_ENGINEER_GEMINI_ENABLE": "1",
                "AI_ENGINEER_GEMINI_EGRESS_CONFIRMED": "1",
            }
        )


def test_gemini_client_uses_fixed_interactions_endpoint_and_json_schema() -> None:
    seen: dict[str, object] = {}

    def post(url: str, headers, body: bytes, timeout: float) -> bytes:
        seen.update(url=url, headers=dict(headers), body=json.loads(body), timeout=timeout)
        return json.dumps(
            {"output_text": '{"summary":"Inspect safely","steps":["file.read"]}'}
        ).encode()

    client = GeminiInteractionsClient(
        GeminiProviderConfig(api_key="gemini-test-abcdefghijklmnopqrstuvwxyz123456"),
        http_post=post,
    )
    result = client.generate_developer(_request())

    assert json.loads(result)["summary"] == "Inspect safely"
    assert seen["url"] == "https://generativelanguage.googleapis.com/v1beta/interactions"
    assert seen["headers"]["x-goog-api-key"].startswith("gemini-test-")
    body = seen["body"]
    assert body["model"] == "gemini-3.5-flash-lite"
    assert body["store"] is False
    assert body["response_format"]["mime_type"] == "application/json"
    assert body["response_format"]["schema"]["type"] == "object"
    assert "additionalProperties" not in body["response_format"]["schema"]


def test_gemini_client_retries_transient_503_with_exponential_backoff() -> None:
    attempts = 0
    delays: list[float] = []

    def post(url: str, headers, body: bytes, timeout: float) -> bytes:
        nonlocal attempts
        attempts += 1
        if attempts < 3:
            raise ProviderError("Gemini provider returned HTTP 503: UNAVAILABLE")
        return json.dumps(
            {"output_text": '{"summary":"Recovered","steps":["file.read"]}'}
        ).encode()

    client = GeminiInteractionsClient(
        GeminiProviderConfig(api_key="gemini-test-abcdefghijklmnopqrstuvwxyz123456"),
        http_post=post,
        sleep=delays.append,
    )

    assert json.loads(client.generate_developer(_request()))["summary"] == "Recovered"
    assert attempts == 3
    assert delays == [1.0, 2.0]


def test_operator_runtime_selects_gemini_only_after_explicit_enable(tmp_path: Path) -> None:
    (tmp_path / "workspace").mkdir()
    values = _environment()
    runtime = OperatorRuntimeConfig.from_environment(
        tmp_path,
        allow_project_root=True,
        environment=values,
    )
    developer, copilot = build_operator_providers(environment=values)

    assert runtime.provider_mode == "gemini"
    assert runtime.gemini_enabled is True
    assert runtime.openai_enabled is False
    assert developer is not None and developer.name == "gemini"
    assert copilot is not None and copilot.name == "gemini"


def test_auto_mode_never_enables_unconfigured_gemini(tmp_path: Path) -> None:
    (tmp_path / "workspace").mkdir()
    runtime = OperatorRuntimeConfig.from_environment(
        tmp_path,
        allow_project_root=True,
        environment={"AI_ENGINEER_OPERATOR_BACKUP_ROOT": "C:\\operator-backup"},
    )
    assert runtime.provider_mode == "none"
    assert runtime.gemini_enabled is False


def test_gemini_http_error_exposes_only_bounded_redacted_google_detail(monkeypatch) -> None:
    body = json.dumps(
        {
            "error": {
                "code": 400,
                "status": "INVALID_ARGUMENT",
                "message": "API key AIzaabcdefghijklmnopqrstuvwxyz123456 is not valid.",
            }
        }
    ).encode()

    class FailingOpener:
        def open(self, request, timeout):
            raise urllib.error.HTTPError(
                request.full_url, 400, "Bad Request", {}, io.BytesIO(body)
            )

    monkeypatch.setattr(urllib.request, "build_opener", lambda *handlers: FailingOpener())
    with pytest.raises(ProviderError) as captured:
        _post_without_redirects_or_proxies(
            "https://generativelanguage.googleapis.com/v1beta/interactions",
            {"x-goog-api-key": "AIzaabcdefghijklmnopqrstuvwxyz123456"},
            b"{}",
            10,
        )

    message = str(captured.value)
    assert "INVALID_ARGUMENT" in message
    assert "[REDACTED_API_KEY]" in message
    assert "AIzaabcdefghijklmnopqrstuvwxyz123456" not in message


def test_gemini_extracts_text_from_official_rest_steps_shape() -> None:
    document = {
        "id": "int_example",
        "status": "completed",
        "steps": [
            {"type": "user_input", "content": [{"type": "text", "text": "question"}]},
            {
                "type": "model_output",
                "content": [
                    {"type": "text", "text": '{"language":"ar","answer":"إجابة","citations":[]}'},
                ],
            },
        ],
    }

    assert _extract_output_text(document) == (
        '{"language":"ar","answer":"إجابة","citations":[]}'
    )
