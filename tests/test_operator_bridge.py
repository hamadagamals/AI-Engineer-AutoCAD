"""Tests for the opt-in operator bridge and OpenAI runtime adapters."""

from __future__ import annotations

import json
from pathlib import Path
import zipfile

import pytest

from agent.app import Application
from agent.exceptions import ApiConfigurationError
from agent.operator_runtime import OperatorRuntimeConfig, build_manufacturer_provider
from agent.provider import ProviderCommand, ProviderPhase, ProviderRequest
from agent.permissions import RiskLevel
from integrations.openai_provider import OpenAIProviderConfig, OpenAIResponsesClient


def _project(tmp_path: Path) -> Path:
    data = tmp_path / "data"
    data.mkdir()
    (data / "settings.json").write_text(
        json.dumps({"schema_version": 1, "log_level": "INFO"}), encoding="utf-8"
    )
    (data / "agent_state.json").write_text(
        json.dumps({"schema_version": 1, "status": "stopped", "start_count": 0}),
        encoding="utf-8",
    )
    return tmp_path


def test_openai_config_requires_explicit_enable_egress_and_key() -> None:
    with pytest.raises(ApiConfigurationError, match="disabled"):
        OpenAIProviderConfig.from_environment({})
    with pytest.raises(ApiConfigurationError, match="egress"):
        OpenAIProviderConfig.from_environment({"AI_ENGINEER_OPENAI_ENABLE": "1"})
    with pytest.raises(ApiConfigurationError, match="OPENAI_API_KEY"):
        OpenAIProviderConfig.from_environment(
            {
                "AI_ENGINEER_OPENAI_ENABLE": "1",
                "AI_ENGINEER_OPENAI_EGRESS_CONFIRMED": "1",
            }
        )


def test_manufacturer_search_has_a_separate_explicit_runtime_gate() -> None:
    assert build_manufacturer_provider(environment={}) is None
    values = {
        "AI_ENGINEER_MANUFACTURER_DISCOVERY_ENABLE": "1",
        "AI_ENGINEER_OPENAI_ENABLE": "1",
        "AI_ENGINEER_OPENAI_EGRESS_CONFIRMED": "1",
        "OPENAI_API_KEY": "sk-test-abcdefghijklmnopqrstuvwxyz123456",
    }
    provider = build_manufacturer_provider(environment=values)
    assert provider is not None
    assert provider.name == "openai-official-search:gpt-5.6"
    economy = build_manufacturer_provider(
        environment={**values, "AI_ENGINEER_OPENAI_MODEL": "gpt-5.6-luna"}
    )
    assert economy is not None
    assert economy.name == "openai-official-search:gpt-5.6-luna"


def test_openai_developer_client_uses_fixed_responses_endpoint_and_extracts_text() -> None:
    seen: dict[str, object] = {}

    def post(url: str, headers, body: bytes, timeout: float) -> bytes:
        seen.update(url=url, headers=dict(headers), body=json.loads(body), timeout=timeout)
        return json.dumps(
            {
                "output": [
                    {
                        "type": "message",
                        "content": [
                            {
                                "type": "output_text",
                                "text": '{"summary":"Inspect safely","steps":["Read status"]}',
                            }
                        ],
                    }
                ]
            }
        ).encode()

    client = OpenAIResponsesClient(
        OpenAIProviderConfig(api_key="sk-test-abcdefghijklmnopqrstuvwxyz123456"),
        http_post=post,
    )
    request = ProviderRequest(
        run_id="a" * 32,
        phase=ProviderPhase.PLAN,
        goal="Inspect the repository",
        plan_summary=None,
        plan_steps=(),
        action_number=1,
        maximum_actions=5,
        available_commands=(ProviderCommand("file.read", "Read file", "file.read", "low"),),
    )

    result = client.generate_developer(request)

    assert json.loads(result)["summary"] == "Inspect safely"
    assert seen["url"] == "https://api.openai.com/v1/responses"
    assert seen["headers"]["Authorization"].startswith("Bearer sk-test-")
    assert seen["body"]["model"] == "gpt-5.6"
    assert seen["body"]["store"] is False


def test_operator_mode_can_explicitly_scope_repository_root(tmp_path: Path) -> None:
    root = _project(tmp_path)
    runtime = OperatorRuntimeConfig.from_environment(
        root,
        allow_project_root=True,
        environment={},
    )
    assert runtime.workspace_roots == (root.resolve(),)
    assert runtime.openai_enabled is False
    assert runtime.gemini_enabled is False
    assert not str(runtime.backup_root).startswith(str(root.resolve()))


def test_operator_workspace_root_exposes_source_only_when_explicit(tmp_path: Path) -> None:
    root = _project(tmp_path)
    source = root / "example.py"
    source.write_text("VALUE = 1\n", encoding="utf-8")
    application = Application(
        root,
        workspace_roots=(root.resolve(),),
        backup_root=(tmp_path.parent / "operator-backup"),
    )
    application.start()
    try:
        assert application.command_registry is not None
        compile_definition = application.command_registry.get("python.compileall")
        test_definition = application.command_registry.get("python.pytest")
        scan_definition = application.command_registry.get("project.scan")
        read_definition = application.command_registry.get("file.read")
        assert scan_definition.payload_contract == '{"path":"<relative workspace path; use . for the workspace root>"}'
        assert read_definition.payload_contract == scan_definition.payload_contract
        assert compile_definition.risk is RiskLevel.MODERATE
        assert test_definition.risk is RiskLevel.HIGH
        assert test_definition.permission == "python.test"
    finally:
        application.stop()


def test_bounded_pytest_uses_private_temp_disables_cache_and_fails_truthfully(
    tmp_path: Path,
) -> None:
    root = _project(tmp_path)
    passing = root / "passing_test.py"
    passing.write_text(
        "from pathlib import Path\n"
        "import stat\n\n"
        "def test_ok(tmp_path: Path):\n"
        "    artifact = tmp_path / 'readonly.txt'\n"
        "    artifact.write_text('bounded', encoding='utf-8')\n"
        "    artifact.chmod(stat.S_IREAD)\n"
        "    assert artifact.read_text(encoding='utf-8') == 'bounded'\n",
        encoding="utf-8",
    )
    failing = root / "failing_test.py"
    failing.write_text("def test_bad():\n    assert False\n", encoding="utf-8")
    application = Application(
        root,
        workspace_roots=(root.resolve(),),
        backup_root=(tmp_path.parent / "operator-backup"),
    )
    application.start()
    try:
        assert application.task_engine is not None
        assert application.approvals is not None

        passed = application.task_engine.submit(
            "python.pytest",
            {"cwd": ".", "targets": ["passing_test.py"], "quiet": True},
        )
        passed = application.task_engine.run(passed.task_id)
        assert passed.status.value == "awaiting_approval"
        assert passed.approval_id is not None
        application.approvals.decide(passed.approval_id, approved=True)
        passed = application.task_engine.run(
            passed.task_id, approval_id=passed.approval_id
        )
        assert passed.status.value == "succeeded"
        assert passed.result is not None
        assert "no:cacheprovider" in passed.result["command"]
        assert "--basetemp" in passed.result["command"]

        failed = application.task_engine.submit(
            "python.pytest",
            {"cwd": ".", "targets": ["failing_test.py"], "quiet": True},
        )
        failed = application.task_engine.run(failed.task_id)
        assert failed.approval_id is not None
        application.approvals.decide(failed.approval_id, approved=True)
        failed = application.task_engine.run(
            failed.task_id, approval_id=failed.approval_id
        )
        assert failed.status.value == "failed"
        assert failed.error == "Pytest failed with exit code 1."
        assert not list(root.glob(".ai-engineer-pytest-*"))
        assert not (root / ".pytest_cache").exists()
    finally:
        application.stop()


def test_vscode_operator_ui_surfaces_live_progress_and_cancel() -> None:
    root = Path(__file__).resolve().parents[1]
    extension = (root / "clients" / "vscode" / "extension.js").read_text(encoding="utf-8")
    operator_script = (root / "clients" / "vscode" / "media" / "operator.js").read_text(
        encoding="utf-8"
    )
    package = json.loads(
        (root / "clients" / "vscode" / "package.json").read_text(encoding="utf-8")
    )

    assert package["version"] == "0.1.6"
    assert "Live activity" in extension
    assert "Elapsed 00:00" in extension
    assert "progressFill" in extension
    assert "Cancel requested" in operator_script
    assert "for (let count = 0; count < 24; count += 1)" in extension
    assert "API unavailable:" in extension
    assert "Authentication failed" in extension
    assert "Provider unavailable" in extension
    assert "'/health', undefined, false, 5000" in extension


def test_windows_launchers_wait_for_real_bridge_health() -> None:
    root = Path(__file__).resolve().parents[1]
    for name in (
        "START-AI-ENGINEER-PROVIDER.ps1",
        "START-AI-ENGINEER-WITH-OPENAI.ps1",
    ):
        source = (root / name).read_text(encoding="utf-8")
        assert "function Wait-BridgeReady" in source
        assert "/health" in source
        assert "Wait-BridgeReady -Process $bridge" in source
    setup_path = root / "SETUP-AI-ENGINEER.ps1"
    if setup_path.is_file():
        setup = setup_path.read_text(encoding="utf-8")
        assert "function Wait-BridgeReady" in setup
        assert "/health" in setup
        assert "Wait-BridgeReady -Process $bridge" in setup
        assert "--basetemp $pytestBase" in setup
        assert "-p no:cacheprovider" in setup
    else:
        manifest = json.loads((root / "package_manifest.json").read_text(encoding="utf-8"))
        assert "SETUP-AI-ENGINEER.ps1" not in manifest["files"]
    preview = (root / "START-AI-ENGINEER-AUTOCAD-PREVIEW.ps1").read_text(encoding="utf-8")
    gemini = (root / "START-AI-ENGINEER-WITH-GEMINI.ps1").read_text(encoding="utf-8")
    assert "[switch]$UseConfiguredProvider" in preview
    assert "UseConfiguredProvider requires one explicitly configured provider mode" in preview
    assert "[ValidateSet(2021, 2023)]" in preview
    assert "AutoCAD.Application.24.2" in preview
    autocad_2023 = (root / "START-AI-ENGINEER-AUTOCAD-2023.ps1").read_text(
        encoding="utf-8"
    )
    assert "-AutoCADYear 2023" in autocad_2023
    assert "-AutoCADRoot $AutoCADRoot" in autocad_2023
    assert "UseConfiguredProvider = $true" in gemini
    assert "START-AI-ENGINEER-AUTOCAD-PREVIEW.ps1" in gemini
    assert "{ 8766 }" in gemini
    assert "Get-Process -Name Code" not in gemini
    assert "Close ALL VS Code windows" not in gemini
    assert "--user-data-dir $vscodeProfile" in gemini
    assert "--extensions-dir $vscodeExtensions" in gemini
    assert "--install-extension $vsix --force" in gemini
    assert "Starting isolated VS Code Operator" in gemini
    assert "An isolated VS Code Operator window is connected" in gemini
    assert "ai-engineer-operator-0.1.6.vsix" in gemini


def test_vscode_operator_waits_for_webview_ready_before_status_refresh() -> None:
    root = Path(__file__).resolve().parents[1]
    extension = (root / "clients" / "vscode" / "extension.js").read_text(
        encoding="utf-8"
    )

    assert "message.type === 'ready'" in extension
    operator_script = (root / "clients" / "vscode" / "media" / "operator.js").read_text(
        encoding="utf-8"
    )
    assert "vscode.postMessage({ type: 'ready' })" in operator_script
    assert "Refreshing local connection" in operator_script
    assert "script-src ${webview.cspSource}" in extension
    assert "webview.onDidReceiveMessage" in extension


def test_bundled_vscode_extension_matches_source_version_and_content() -> None:
    root = Path(__file__).resolve().parents[1]
    source = root / "clients" / "vscode"
    package = json.loads((source / "package.json").read_text(encoding="utf-8"))
    archive_path = source / f"ai-engineer-operator-{package['version']}.vsix"

    with zipfile.ZipFile(archive_path) as archive:
        names = set(archive.namelist())
        assert {
            "extension/package.json",
            "extension/extension.js",
            "extension/media/operator.js",
            "extension.vsixmanifest",
            "[Content_Types].xml",
        } <= names
        assert archive.read("extension/package.json").replace(b"\r\n", b"\n") == (
            source / "package.json"
        ).read_bytes().replace(b"\r\n", b"\n")
        assert archive.read("extension/extension.js").replace(b"\r\n", b"\n") == (
            source / "extension.js"
        ).read_bytes().replace(b"\r\n", b"\n")
        assert archive.read("extension/media/operator.js").replace(b"\r\n", b"\n") == (
            source / "media" / "operator.js"
        ).read_bytes().replace(b"\r\n", b"\n")
        manifest = archive.read("extension.vsixmanifest").decode("utf-8")
        assert f'Version="{package["version"]}"' in manifest
