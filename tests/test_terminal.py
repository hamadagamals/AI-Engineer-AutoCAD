"""Tests for fail-closed command execution."""

from __future__ import annotations

import json
import logging
import os
import sys
from pathlib import Path

import pytest

from agent.exceptions import CommandDeniedError, ConfirmationRequiredError
from core.file_manager import FileManager
from core.terminal_manager import CommandPolicy, TerminalManager


@pytest.fixture
def workspace(tmp_path: Path) -> Path:
    root = tmp_path / "workspace"
    root.mkdir()
    return root


def make_manager(
    tmp_path: Path,
    workspace: Path,
    script_name: str,
    *,
    max_output_bytes: int = 1024,
    timeout: float = 2,
    logger: logging.Logger | None = None,
) -> TerminalManager:
    file_manager = FileManager([workspace], tmp_path / "backup")
    policy = CommandPolicy((("python", script_name),))
    return TerminalManager(
        file_manager,
        policy,
        default_timeout_seconds=timeout,
        maximum_timeout_seconds=10,
        max_output_bytes=max_output_bytes,
        logger=logger,
    )


def write_script(workspace: Path, name: str, source: str) -> Path:
    path = workspace / name
    path.write_text(source, encoding="utf-8")
    return path


def test_execute_captures_output_and_exit_code(tmp_path: Path, workspace: Path) -> None:
    script = write_script(
        workspace,
        "result.py",
        "import sys\nprint('out')\nprint('err', file=sys.stderr)\nraise SystemExit(3)\n",
    )
    manager = make_manager(tmp_path, workspace, script.name)

    result = manager.execute([sys.executable, script.name], cwd=workspace)

    assert result.return_code == 3
    assert result.stdout == "out\r\n" if os.name == "nt" else "out\n"
    assert "err" in result.stderr
    assert not result.succeeded
    assert not result.timed_out


def test_policy_denies_unlisted_command(tmp_path: Path, workspace: Path) -> None:
    manager = make_manager(tmp_path, workspace, "allowed.py")

    with pytest.raises(CommandDeniedError, match="allowed policy"):
        manager.execute([sys.executable, "different.py"], cwd=workspace)


@pytest.mark.parametrize("timeout", [0, -1, True, float("inf")])
def test_invalid_timeout_is_denied(
    tmp_path: Path, workspace: Path, timeout: float
) -> None:
    manager = make_manager(tmp_path, workspace, "allowed.py")

    with pytest.raises(CommandDeniedError, match="Timeout"):
        manager.execute(
            [sys.executable, "allowed.py"], cwd=workspace, timeout_seconds=timeout
        )


@pytest.mark.parametrize("shell", ["cmd.exe", "powershell.exe", "pwsh"])
def test_policy_permanently_denies_shells(shell: str) -> None:
    with pytest.raises(ValueError, match="Shell executable"):
        CommandPolicy(((shell, "/c"),))


def test_inline_python_is_denied_even_when_prefix_is_listed(
    tmp_path: Path, workspace: Path
) -> None:
    file_manager = FileManager([workspace], tmp_path / "backup")
    manager = TerminalManager(file_manager, CommandPolicy((("python", "-c"),)))

    with pytest.raises(CommandDeniedError, match="Inline Python"):
        manager.execute([sys.executable, "-c", "print('unsafe')"], cwd=workspace)


def test_confirmation_gated_prefix(tmp_path: Path, workspace: Path) -> None:
    script = write_script(workspace, "gated.py", "print('ok')\n")
    file_manager = FileManager([workspace], tmp_path / "backup")
    policy = CommandPolicy(
        (("python", script.name),), confirmation_prefixes=(("python", script.name),)
    )
    manager = TerminalManager(file_manager, policy)

    with pytest.raises(ConfirmationRequiredError):
        manager.execute([sys.executable, script.name], cwd=workspace)
    assert manager.execute(
        [sys.executable, script.name], cwd=workspace, confirmed=True
    ).succeeded


def test_working_directory_must_be_inside_workspace(
    tmp_path: Path, workspace: Path
) -> None:
    manager = make_manager(tmp_path, workspace, "allowed.py")

    with pytest.raises(CommandDeniedError, match="Working directory"):
        manager.execute([sys.executable, "allowed.py"], cwd=tmp_path)


def test_parent_traversal_argument_is_denied(tmp_path: Path, workspace: Path) -> None:
    manager = make_manager(tmp_path, workspace, "runner.py")

    with pytest.raises(CommandDeniedError, match="Parent traversal"):
        manager.execute([sys.executable, "runner.py", "../secret.txt"], cwd=workspace)


def test_timeout_terminates_process(tmp_path: Path, workspace: Path) -> None:
    script = write_script(workspace, "slow.py", "import time\ntime.sleep(5)\n")
    manager = make_manager(tmp_path, workspace, script.name, timeout=0.1)

    result = manager.execute([sys.executable, script.name], cwd=workspace)

    assert result.timed_out
    assert not result.succeeded
    assert result.duration_seconds < 4


def test_output_is_truncated_without_blocking_process(
    tmp_path: Path, workspace: Path
) -> None:
    script = write_script(workspace, "large.py", "print('x' * 10000)\n")
    manager = make_manager(tmp_path, workspace, script.name, max_output_bytes=100)

    result = manager.execute([sys.executable, script.name], cwd=workspace)

    assert result.succeeded
    assert len(result.stdout.encode("utf-8")) == 100
    assert result.stdout_truncated


def test_environment_is_filtered_and_rejects_unknown_override(
    tmp_path: Path, workspace: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    script = write_script(
        workspace,
        "env.py",
        "import json, os\nprint(json.dumps(dict(os.environ)))\n",
    )
    monkeypatch.setenv("AI_ENGINEER_SECRET_TEST", "hidden")
    manager = make_manager(tmp_path, workspace, script.name, max_output_bytes=10000)

    result = manager.execute([sys.executable, script.name], cwd=workspace)
    child_environment = json.loads(result.stdout)

    assert "AI_ENGINEER_SECRET_TEST" not in child_environment
    with pytest.raises(CommandDeniedError, match="Environment variable"):
        manager.execute(
            [sys.executable, script.name],
            cwd=workspace,
            environment={"AI_ENGINEER_SECRET_TEST": "expose"},
        )
    with pytest.raises(CommandDeniedError, match="only be set to 0"):
        manager.execute(
            [sys.executable, script.name],
            cwd=workspace,
            environment={"GIT_OPTIONAL_LOCKS": "1"},
        )


def test_command_audit_log_omits_argument_values(
    tmp_path: Path, workspace: Path
) -> None:
    script = write_script(workspace, "audit.py", "print('ok')\n")
    records: list[logging.LogRecord] = []

    class CaptureHandler(logging.Handler):
        def emit(self, record: logging.LogRecord) -> None:
            records.append(record)

    logger = logging.getLogger(f"test.terminal.{os.getpid()}")
    logger.handlers = [CaptureHandler()]
    logger.setLevel(logging.INFO)
    logger.propagate = False
    manager = make_manager(tmp_path, workspace, script.name, logger=logger)

    manager.execute([sys.executable, script.name, "sensitive-value"], cwd=workspace)

    assert [record.getMessage() for record in records] == [
        "command_started",
        "command_completed",
    ]
    assert all("sensitive-value" not in str(record.__dict__) for record in records)
