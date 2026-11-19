"""Tests for the supported, workspace-confined VS Code CLI adapter."""

from __future__ import annotations

import subprocess
from pathlib import Path

import pytest

from agent.exceptions import (
    ConfirmationRequiredError,
    PathPolicyError,
    VSCodeIntegrationError,
)
from core.file_manager import FileManager
from integrations.vscode import VSCodeManager


def _manager(tmp_path: Path) -> tuple[VSCodeManager, Path, Path]:
    workspace = tmp_path / "workspace"
    backup = tmp_path / "backup"
    workspace.mkdir(parents=True)
    installation = tmp_path / "installation"
    wrapper = installation / "bin" / "code.cmd"
    executable = installation / "Code.exe"
    script = installation / "build" / "resources" / "app" / "out" / "cli.js"
    wrapper.parent.mkdir(parents=True)
    script.parent.mkdir(parents=True)
    executable.write_bytes(b"test executable")
    script.write_text("// test CLI", encoding="utf-8")
    wrapper.write_text(
        '@echo off\n"%~dp0..\\Code.exe" '
        '"%~dp0..\\build\\resources\\app\\out\\cli.js" %*\n',
        encoding="utf-8",
    )
    files = FileManager([workspace], backup)
    return VSCodeManager(files, locator=lambda name: str(wrapper)), workspace, wrapper


def test_detect_reports_not_installed_without_running_process(tmp_path: Path) -> None:
    files = FileManager([tmp_path / "workspace"], tmp_path / "backup")
    manager = VSCodeManager(files, locator=lambda name: None)

    assert manager.detect().to_dict() == {
        "installed": False,
        "version": None,
        "commit": None,
        "architecture": None,
        "cli_kind": None,
    }


def test_detect_resolves_official_windows_wrapper_without_shell(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manager, _, _ = _manager(tmp_path)
    captured: dict[str, object] = {}

    def fake_run(arguments: tuple[str, ...], **kwargs: object) -> subprocess.CompletedProcess[str]:
        captured["arguments"] = arguments
        captured.update(kwargs)
        return subprocess.CompletedProcess(
            arguments, 0, "1.131.0\nabcdef1234567890\nx64\n", "ignored warning"
        )

    monkeypatch.setattr("integrations.vscode.subprocess.run", fake_run)

    status = manager.detect()

    assert status.installed is True
    assert status.version == "1.131.0"
    assert status.commit == "abcdef1234567890"
    assert status.cli_kind == "windows-wrapper"
    arguments = captured["arguments"]
    assert isinstance(arguments, tuple)
    assert arguments[0].endswith("Code.exe")
    assert arguments[1].endswith("cli.js")
    assert arguments[2] == "--version"
    assert captured["shell"] is False
    environment = captured["env"]
    assert isinstance(environment, dict)
    assert environment["ELECTRON_RUN_AS_NODE"] == "1"
    assert not any("TOKEN" in key.upper() or "SECRET" in key.upper() for key in environment)


def test_open_requires_confirmation_and_workspace_containment(tmp_path: Path) -> None:
    manager, workspace, _ = _manager(tmp_path)
    target = workspace / "sample.py"
    target.write_text("print('safe')\n", encoding="utf-8")

    with pytest.raises(ConfirmationRequiredError):
        manager.open_path(target)
    with pytest.raises(PathPolicyError, match="outside configured workspace roots"):
        manager.open_path(tmp_path / "outside.py", confirmed=True)


def test_open_file_uses_reuse_window_and_bounded_goto(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manager, workspace, _ = _manager(tmp_path)
    target = workspace / "sample.py"
    target.write_text("print('safe')\n", encoding="utf-8")
    calls: list[tuple[str, ...]] = []

    def fake_run(arguments: tuple[str, ...], **kwargs: object) -> subprocess.CompletedProcess[str]:
        calls.append(arguments)
        return subprocess.CompletedProcess(arguments, 0, "", "")

    monkeypatch.setattr("integrations.vscode.subprocess.run", fake_run)

    result = manager.open_path(target, line=1, column=2, confirmed=True)

    assert result.return_code == 0
    assert calls[0][-3:] == ("--reuse-window", "--goto", f"{target.resolve()}:1:2")


@pytest.mark.parametrize(
    ("line", "column"),
    [(0, None), (True, None), (None, 1), (1_000_001, None)],
)
def test_open_rejects_invalid_navigation(
    tmp_path: Path, line: int | None, column: int | None
) -> None:
    manager, workspace, _ = _manager(tmp_path)
    target = workspace / "sample.py"
    target.write_text("content", encoding="utf-8")

    with pytest.raises(VSCodeIntegrationError):
        manager.open_path(target, line=line, column=column, confirmed=True)


def test_wrapper_escape_and_invalid_metadata_fail_closed(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manager, _, wrapper = _manager(tmp_path)
    outside = wrapper.parents[2] / "escape"
    outside.mkdir()
    (outside / "Code.exe").write_bytes(b"outside")
    wrapper.write_text(
        '@echo off\n"%~dp0..\\..\\escape\\Code.exe" '
        '"%~dp0..\\build\\resources\\app\\out\\cli.js" %*\n',
        encoding="utf-8",
    )
    with pytest.raises(VSCodeIntegrationError, match="escapes"):
        manager.detect()

    valid, _, _ = _manager(tmp_path / "metadata")
    monkeypatch.setattr(
        "integrations.vscode.subprocess.run",
        lambda arguments, **kwargs: subprocess.CompletedProcess(
            arguments, 0, "invalid\nnot-a-commit\nunknown\n", ""
        ),
    )
    with pytest.raises(VSCodeIntegrationError, match="version"):
        valid.detect()


def test_cli_timeout_and_nonzero_exit_are_actionable(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manager, workspace, _ = _manager(tmp_path)
    target = workspace / "project"
    target.mkdir()

    def timeout(arguments: tuple[str, ...], **kwargs: object) -> subprocess.CompletedProcess[str]:
        raise subprocess.TimeoutExpired(arguments, 1)

    monkeypatch.setattr("integrations.vscode.subprocess.run", timeout)
    with pytest.raises(VSCodeIntegrationError, match="timed out"):
        manager.detect()

    monkeypatch.setattr(
        "integrations.vscode.subprocess.run",
        lambda arguments, **kwargs: subprocess.CompletedProcess(arguments, 4, "", "private"),
    )
    with pytest.raises(VSCodeIntegrationError, match="exit code 4") as raised:
        manager.open_path(target, confirmed=True)
    assert "private" not in str(raised.value)
