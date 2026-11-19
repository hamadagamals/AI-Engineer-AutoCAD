"""Tests for local Ollama, ChatGPT-authenticated Codex, and hybrid provider routing."""

from __future__ import annotations

import json
import subprocess
from pathlib import Path

import pytest

from agent.exceptions import ApiConfigurationError, ProviderError
from agent.operator_runtime import OperatorRuntimeConfig
from agent.provider import ProviderAdapter, ProviderCommand, ProviderPhase, ProviderRequest
from integrations.codex_provider import CodexExecClient, CodexProviderConfig
from integrations.hybrid_provider import HybridDeveloperGenerator
from integrations.ollama_provider import OllamaChatClient, OllamaProviderConfig


def _request(phase: ProviderPhase = ProviderPhase.PLAN) -> ProviderRequest:
    return ProviderRequest(
        run_id="a" * 32,
        phase=phase,
        goal="Inspect the project safely",
        plan_summary=None,
        plan_steps=(),
        action_number=1,
        maximum_actions=5,
        available_commands=(
            ProviderCommand("file.read", "Read file", "file.read", "low"),
        ),
    )


def _project(tmp_path: Path) -> Path:
    (tmp_path / "workspace").mkdir()
    return tmp_path


def test_ollama_config_rejects_non_loopback_url() -> None:
    with pytest.raises(ApiConfigurationError, match="loopback"):
        OllamaProviderConfig(base_url="http://192.168.1.10:11434")
    with pytest.raises(ApiConfigurationError, match="loopback"):
        OllamaProviderConfig(base_url="https://127.0.0.1:11434")


def test_ollama_client_requests_structured_output_on_loopback() -> None:
    seen: dict[str, object] = {}

    def post(url: str, headers, body: bytes, timeout: float) -> bytes:
        seen.update(url=url, headers=dict(headers), body=json.loads(body), timeout=timeout)
        return json.dumps(
            {
                "message": {
                    "role": "assistant",
                    "content": '{"summary":"Inspect safely","steps":["Read status"]}',
                }
            }
        ).encode()

    client = OllamaChatClient(OllamaProviderConfig(), http_post=post)
    result = client.generate_developer(_request())

    assert json.loads(result)["summary"] == "Inspect safely"
    assert seen["url"] == "http://127.0.0.1:11434/api/chat"
    body = seen["body"]
    assert body["model"] == "qwen2.5-coder:7b"
    assert body["stream"] is False
    assert body["keep_alive"] == "10m"
    assert body["options"]["temperature"] == 0
    assert body["options"]["num_ctx"] == 4096
    assert seen["timeout"] == 240.0
    assert body["format"]["additionalProperties"] is False


def test_codex_exec_is_ephemeral_read_only_and_does_not_inherit_bridge_secrets(
    tmp_path: Path,
) -> None:
    executable = tmp_path / "codex"
    executable.write_text("placeholder", encoding="utf-8")
    seen: dict[str, object] = {}

    def run(argv, **kwargs):
        seen.update(argv=list(argv), kwargs=kwargs)
        return subprocess.CompletedProcess(
            argv,
            0,
            stdout='{"summary":"Safe plan","steps":["Inspect"]}',
            stderr="progress that must not be exposed",
        )

    client = CodexExecClient(
        CodexProviderConfig(command=str(executable), timeout_seconds=30),
        run_process=run,
        environment={
            "PATH": "/safe/bin",
            "HOME": "/home/test",
            "OPENAI_API_KEY": "must-not-leak",
            "CODEX_API_KEY": "must-not-leak",
            "AI_ENGINEER_API_TOKEN": "must-not-leak",
            "AWS_SECRET_ACCESS_KEY": "must-not-leak",
        },
    )

    result = client.generate_developer(_request())

    assert json.loads(result)["summary"] == "Safe plan"
    argv = seen["argv"]
    assert argv[1] == "exec"
    assert "--ephemeral" in argv
    assert "--sandbox" in argv
    assert argv[argv.index("--sandbox") + 1] == "read-only"
    assert "--ignore-user-config" in argv
    assert "--ignore-rules" in argv
    assert "--skip-git-repo-check" in argv
    assert "--output-schema" in argv
    assert argv[-1] == "-"
    kwargs = seen["kwargs"]
    assert kwargs["shell"] is False
    assert kwargs["cwd"] != str(tmp_path)
    assert kwargs["env"]["HOME"] == "/home/test"
    assert "OPENAI_API_KEY" not in kwargs["env"]
    assert "CODEX_API_KEY" not in kwargs["env"]
    assert "AI_ENGINEER_API_TOKEN" not in kwargs["env"]
    assert "AWS_SECRET_ACCESS_KEY" not in kwargs["env"]


def test_codex_act_flat_schema_is_normalized_to_existing_exact_contract(tmp_path: Path) -> None:
    executable = tmp_path / "codex"
    executable.write_text("placeholder", encoding="utf-8")

    def run(argv, **kwargs):
        return subprocess.CompletedProcess(
            argv,
            0,
            stdout=json.dumps(
                {
                    "kind": "command",
                    "command_name": "file.read",
                    "payload": {"path": "README.md"},
                    "rationale": "Read first",
                    "summary": None,
                }
            ),
            stderr="",
        )

    client = CodexExecClient(
        CodexProviderConfig(command=str(executable), timeout_seconds=30),
        run_process=run,
        environment={"PATH": "/safe/bin", "HOME": "/home/test"},
    )

    value = json.loads(client.generate_developer(_request(ProviderPhase.ACT)))

    assert value == {
        "kind": "command",
        "command_name": "file.read",
        "payload": {"path": "README.md"},
        "rationale": "Read first",
    }


def test_hybrid_developer_falls_back_when_local_output_breaks_contract() -> None:
    calls = {"local": 0, "codex": 0}

    def local(_request: ProviderRequest) -> str:
        calls["local"] += 1
        return "not json"

    def codex(_request: ProviderRequest) -> str:
        calls["codex"] += 1
        return '{"summary":"Escalated safely","steps":["Inspect"]}'

    generator = HybridDeveloperGenerator(
        ProviderAdapter("ollama", local),
        ProviderAdapter("codex", codex),
    )

    result = generator(_request())

    assert json.loads(result)["summary"] == "Escalated safely"
    assert calls == {"local": 1, "codex": 1}


def test_hybrid_developer_does_not_spend_codex_when_local_contract_is_valid() -> None:
    calls = {"codex": 0}

    def codex(_request: ProviderRequest) -> str:
        calls["codex"] += 1
        raise ProviderError("should not be called")

    generator = HybridDeveloperGenerator(
        ProviderAdapter(
            "ollama",
            lambda _request: '{"summary":"Local plan","steps":["Inspect"]}',
        ),
        ProviderAdapter("codex", codex),
    )

    result = generator(_request())

    assert json.loads(result)["summary"] == "Local plan"
    assert calls["codex"] == 0


def test_operator_auto_mode_prefers_hybrid_without_enabling_openai(tmp_path: Path) -> None:
    root = _project(tmp_path)
    runtime = OperatorRuntimeConfig.from_environment(
        root,
        allow_project_root=True,
        environment={
            "AI_ENGINEER_PROVIDER": "auto",
            "AI_ENGINEER_OLLAMA_ENABLE": "1",
            "AI_ENGINEER_CODEX_ENABLE": "1",
            "AI_ENGINEER_CODEX_EGRESS_CONFIRMED": "1",
        },
    )

    assert runtime.provider_mode == "hybrid"
    assert runtime.ollama_enabled is True
    assert runtime.codex_enabled is True
    assert runtime.openai_enabled is False
    assert runtime.gemini_enabled is False
