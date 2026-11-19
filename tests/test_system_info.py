"""Tests for bounded system information."""

from __future__ import annotations

import json
import logging
import os
from pathlib import Path

import pytest

from core.file_manager import FileManager
from core.system_info import SystemInfo


def test_snapshot_is_json_serializable_and_omits_identity(tmp_path: Path) -> None:
    workspace = tmp_path / "workspace"
    manager = FileManager([workspace], tmp_path / "backup")

    snapshot = SystemInfo(manager).snapshot()
    payload = snapshot.to_dict()

    json.dumps(payload)
    assert payload["operating_system"]
    assert payload["python_version"]
    assert payload["memory_total_bytes"] > 0
    assert payload["disks"][0]["path"] == str(workspace.resolve())
    assert "hostname" not in payload
    assert "username" not in payload
    assert "environment" not in payload


def test_snapshot_emits_audit_event(tmp_path: Path) -> None:
    workspace = tmp_path / "workspace"
    manager = FileManager([workspace], tmp_path / "backup")
    records: list[logging.LogRecord] = []

    class CaptureHandler(logging.Handler):
        def emit(self, record: logging.LogRecord) -> None:
            records.append(record)

    logger = logging.getLogger(f"test.system_info.{os.getpid()}")
    logger.handlers = [CaptureHandler()]
    logger.setLevel(logging.INFO)
    logger.propagate = False

    SystemInfo(manager, logger=logger).snapshot()

    assert [record.getMessage() for record in records] == ["system_snapshot_collected"]
