"""Tests for bounded process inspection and graceful termination."""

from __future__ import annotations

import os
import subprocess
import sys
from pathlib import Path

import psutil
import pytest

from agent.exceptions import ConfirmationRequiredError, ProcessManagerError
from core.process_manager import ProcessManager


def test_list_processes_is_bounded_and_omits_sensitive_fields() -> None:
    result = ProcessManager(max_processes=5).list_processes()

    assert len(result.processes) <= 5
    assert tuple(item.pid for item in result.processes) == tuple(
        sorted(item.pid for item in result.processes)
    )
    if result.processes:
        payload = result.processes[0].to_dict()
        assert "cmdline" not in payload
        assert "username" not in payload
        assert "environ" not in payload


def test_termination_requires_confirmation() -> None:
    with pytest.raises(ConfirmationRequiredError):
        ProcessManager().terminate(
            999_999, expected_create_time=0.0, confirmed=False
        )


@pytest.mark.parametrize("pid", [0, 1, 4, True])
def test_system_and_invalid_pids_are_protected(pid: int) -> None:
    with pytest.raises(ProcessManagerError, match="protected"):
        ProcessManager().terminate(pid, expected_create_time=0.0, confirmed=True)


def test_manager_cannot_terminate_itself() -> None:
    with pytest.raises(ProcessManagerError, match="own process"):
        ProcessManager().terminate(
            os.getpid(),
            expected_create_time=psutil.Process().create_time(),
            confirmed=True,
        )


def test_expected_create_time_prevents_pid_reuse_error(tmp_path: Path) -> None:
    script = tmp_path / "mismatch.py"
    script.write_text("import time\ntime.sleep(30)\n", encoding="utf-8")
    child = subprocess.Popen([sys.executable, str(script)])
    try:
        actual_create_time = psutil.Process(child.pid).create_time()
        with pytest.raises(ProcessManagerError, match="expected process instance"):
            ProcessManager().terminate(
                child.pid,
                expected_create_time=actual_create_time - 60,
                confirmed=True,
            )
        assert child.poll() is None
    finally:
        child.kill()
        child.wait(timeout=5)


def test_gracefully_terminates_confirmed_child_process(tmp_path: Path) -> None:
    script = tmp_path / "sleep.py"
    script.write_text("import time\ntime.sleep(30)\n", encoding="utf-8")
    child = subprocess.Popen(
        [sys.executable, str(script)],
        stdin=subprocess.DEVNULL,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )
    try:
        process = psutil.Process(child.pid)
        result = ProcessManager().terminate(
            child.pid,
            expected_create_time=process.create_time(),
            confirmed=True,
            timeout_seconds=5,
        )
        assert result.terminated
        assert not result.timed_out
    finally:
        if child.poll() is None:
            child.kill()
        child.wait(timeout=5)
