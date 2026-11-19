"""Tests for the opt-in Anthropic Claude reasoning and copilot provider."""

from __future__ import annotations

import json
import pytest

from agent.copilot import CopilotCitation, CopilotLanguage, EgressField
from agent.copilot_provider import CopilotProviderRequest
from agent.exceptions import ApiConfigurationError, ProviderError
from agent.operator_runtime import OperatorRuntimeConfig, build_operator_providers
from agent.provider import ProviderCommand, ProviderPhase, ProviderRequest
from integrations.claude_provider import (
    ClaudeMessagesClient,
    ClaudeProviderConfig,
    create_claude_providers,
)


def _provider_request() -> ProviderRequest:
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


def _copilot_request() -> CopilotProviderRequest:
    return CopilotProviderRequest(
        session_id="1" * 32,
        context_id="2" * 32,
        review_id="3" * 32,
        preview_sha256="4" * 64,
        language=CopilotLanguage.ARABIC,
        fields=(
            EgressField("user_prompt", "ارسم كشافات إنارة في الغرفة 101"),
        ),
    )


def test_claude_config_validation() -> None:
    # Invalid or missing key
    with pytest.raises(ApiConfigurationError, match="ANTHROPIC_API_KEY is missing or invalid"):
        ClaudeProviderConfig(api_key="short")

    # Valid config
    config = ClaudeProviderConfig(
        api_key="sk-ant-api03-abcdefghijklmnopqrstuvwxyz1234567890",
        model="claude-3-7-sonnet-20250219",
        timeout_seconds=45.0,
        max_output_tokens=2048,
    )
    assert config.model == "claude-3-7-sonnet-20250219"
    assert config.timeout_seconds == 45.0
    assert config.max_output_tokens == 2048


def test_claude_config_from_environment() -> None:
    env = {
        "ANTHROPIC_API_KEY": "sk-ant-api03-validkeytest12345678901234567890",
        "AI_ENGINEER_CLAUDE_MODEL": "claude-3-5-sonnet-20241022",
        "AI_ENGINEER_CLAUDE_TIMEOUT_SECONDS": "30",
        "AI_ENGINEER_CLAUDE_MAX_OUTPUT_TOKENS": "1024",
    }
    cfg = ClaudeProviderConfig.from_environment(env)
    assert cfg.model == "claude-3-5-sonnet-20241022"
    assert cfg.timeout_seconds == 30.0
    assert cfg.max_output_tokens == 1024


def test_claude_developer_and_copilot_generation() -> None:
    test_json_response = json.dumps({
        "content": [
            {
                "type": "text",
                "text": json.dumps({
                    "summary": "Sample plan",
                    "steps": ["Step 1: Check layers", "Step 2: Place fixtures"],
                })
            }
        ]
    }).encode("utf-8")

    def mock_post(url: str, headers: dict, body: bytes, timeout: float) -> bytes:
        assert "x-api-key" in headers
        assert headers["anthropic-version"] == "2023-06-01"
        data = json.loads(body.decode("utf-8"))
        assert "messages" in data
        assert data["model"] == "claude-3-7-sonnet-20250219"
        return test_json_response

    config = ClaudeProviderConfig(
        api_key="sk-ant-api03-testkeyabcdefghijklmnopqrstuvwxyz123",
        model="claude-3-7-sonnet-20250219",
    )
    developer_adapter, copilot_adapter = create_claude_providers(config, http_post=mock_post)

    assert developer_adapter.name == "claude"
    assert copilot_adapter.name == "claude"

    dev_output = developer_adapter.generate(_provider_request())
    parsed = json.loads(dev_output)
    assert parsed["summary"] == "Sample plan"
    assert len(parsed["steps"]) == 2


def test_operator_runtime_supports_claude() -> None:
    env = {
        "AI_ENGINEER_PROVIDER": "claude",
        "AI_ENGINEER_CLAUDE_ENABLE": "1",
        "ANTHROPIC_API_KEY": "sk-ant-api03-testkeyabcdefghijklmnopqrstuvwxyz123",
        "AI_ENGINEER_OPERATOR_BACKUP_ROOT": "C:\\operator-backup",
    }
    developer, copilot = build_operator_providers(environment=env)
    assert developer is not None
    assert developer.name == "claude"
    assert copilot is not None
    assert copilot.name == "claude"

