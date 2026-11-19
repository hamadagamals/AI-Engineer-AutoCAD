"""Tests for evidence-bound, atomic AutoCAD drawing handoff."""

from __future__ import annotations

import hashlib
import logging
import os
from pathlib import Path

import pytest

from agent.exceptions import (
    ConfirmationRequiredError,
    DrawingHandoffError,
    PathPolicyError,
)
from core.file_manager import FileManager
from integrations import drawing_handoff
from integrations.drawing_handoff import (
    handoff_verified_output,
    persist_drawing_output_evidence,
)


RUN_ID = "a" * 32


def _digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def _evidence(tmp_path: Path) -> tuple[FileManager, Path, Path, bytes, str]:
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    files = FileManager([workspace], tmp_path / "backup")
    source_data = b"original-drawing"
    output_data = b"verified-output-drawing"
    source = workspace / "original.dwg"
    source.write_bytes(source_data)
    run_dir = workspace / ".ai-engineer" / "autocad" / "runs" / RUN_ID
    run_dir.mkdir(parents=True)
    output = run_dir / "source.dwg"
    output.write_bytes(output_data)
    persist_drawing_output_evidence(
        files,
        run_id=RUN_ID,
        operation="autocad.create_line_scratch",
        source=source,
        source_sha256=_digest(source_data),
        output=output,
        output_sha256=_digest(output_data),
        maximum_bytes=1024,
    )
    return files, workspace, source, output_data, _digest(output_data)


def _handoff(
    files: FileManager,
    tmp_path: Path,
    destination: str | Path,
    output_digest: str,
    **kwargs: object,
):
    return handoff_verified_output(
        files,
        tmp_path / "handoff-backups",
        run_id=RUN_ID,
        output_sha256=output_digest,
        destination=destination,
        replace_existing=bool(kwargs.pop("replace_existing", False)),
        confirmed=bool(kwargs.pop("confirmed", True)),
        maximum_bytes=1024,
        **kwargs,
    )


def test_new_handoff_requires_confirmation_and_creates_exact_bytes(
    tmp_path: Path, caplog: pytest.LogCaptureFixture
) -> None:
    files, workspace, source, output_data, digest = _evidence(tmp_path)
    destination = workspace / "released.dwg"
    with pytest.raises(ConfirmationRequiredError):
        _handoff(files, tmp_path, destination, digest, confirmed=False)

    with caplog.at_level(logging.INFO):
        result = _handoff(files, tmp_path, destination, digest)

    assert destination.read_bytes() == output_data
    assert result.output_sha256 == digest
    assert result.replaced is False
    assert result.backup_path is None
    assert source.read_bytes() == b"original-drawing"
    assert "verified-output-drawing" not in caplog.text


def test_handoff_rejects_path_escape_internal_destination_and_bad_identity(
    tmp_path: Path,
) -> None:
    files, workspace, _, _, digest = _evidence(tmp_path)
    with pytest.raises(PathPolicyError):
        _handoff(files, tmp_path, tmp_path / "outside.dwg", digest)
    with pytest.raises(DrawingHandoffError, match="internal"):
        _handoff(
            files,
            tmp_path,
            workspace / ".ai-engineer" / "released.dwg",
            digest,
        )
    with pytest.raises(DrawingHandoffError, match="digest"):
        _handoff(files, tmp_path, workspace / "bad.dwg", "b" * 64)
    with pytest.raises(DrawingHandoffError, match="suffix"):
        _handoff(files, tmp_path, workspace / "bad.dwt", digest)
    with pytest.raises(DrawingHandoffError, match="Exactly one"):
        handoff_verified_output(
            files,
            tmp_path / "handoff-backups",
            run_id="b" * 32,
            output_sha256=digest,
            destination=workspace / "missing.dwg",
            replace_existing=False,
            confirmed=True,
            maximum_bytes=1024,
        )


def test_new_handoff_refuses_existing_and_concurrent_destination(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    files, workspace, _, _, digest = _evidence(tmp_path)
    destination = workspace / "released.dwg"
    destination.write_bytes(b"user-file")
    with pytest.raises(DrawingHandoffError, match="existing"):
        _handoff(files, tmp_path, destination, digest)
    assert destination.read_bytes() == b"user-file"

    destination.unlink()
    real_link = os.link

    def racing_link(source: Path, target: Path) -> None:
        target.write_bytes(b"concurrent-file")
        real_link(source, target)

    monkeypatch.setattr(drawing_handoff.os, "link", racing_link)
    with pytest.raises(DrawingHandoffError, match="concurrently"):
        _handoff(files, tmp_path, destination, digest)
    assert destination.read_bytes() == b"concurrent-file"


def test_replacement_requires_expected_hash_and_preserves_verified_backup(
    tmp_path: Path,
) -> None:
    files, workspace, _, output_data, digest = _evidence(tmp_path)
    destination = workspace / "released.dwg"
    previous = b"previous-user-drawing"
    destination.write_bytes(previous)
    with pytest.raises(DrawingHandoffError, match="current destination digest"):
        _handoff(
            files,
            tmp_path,
            destination,
            digest,
            replace_existing=True,
        )
    with pytest.raises(DrawingHandoffError, match="changed"):
        _handoff(
            files,
            tmp_path,
            destination,
            digest,
            replace_existing=True,
            expected_destination_sha256="c" * 64,
        )

    result = _handoff(
        files,
        tmp_path,
        destination,
        digest,
        replace_existing=True,
        expected_destination_sha256=_digest(previous),
    )

    assert destination.read_bytes() == output_data
    assert result.backup_path is not None
    assert result.backup_path.read_bytes() == previous
    assert result.replaced is True


def test_replacement_rolls_back_after_post_write_corruption(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    files, workspace, _, _, digest = _evidence(tmp_path)
    destination = workspace / "released.dwg"
    previous = b"previous-user-drawing"
    destination.write_bytes(previous)
    real_replace = os.replace
    target_replacements = 0

    def corrupt_first_target(source: Path, target: Path) -> None:
        nonlocal target_replacements
        real_replace(source, target)
        if Path(target) == destination and target_replacements == 0:
            destination.write_bytes(b"corrupt")
            target_replacements += 1

    monkeypatch.setattr(drawing_handoff.os, "replace", corrupt_first_target)
    with pytest.raises(DrawingHandoffError, match="restoring backup"):
        _handoff(
            files,
            tmp_path,
            destination,
            digest,
            replace_existing=True,
            expected_destination_sha256=_digest(previous),
        )
    assert destination.read_bytes() == previous


def test_handoff_rejects_symlink_escape_when_supported(tmp_path: Path) -> None:
    files, workspace, _, _, digest = _evidence(tmp_path)
    outside = tmp_path / "outside"
    outside.mkdir()
    link = workspace / "linked"
    try:
        link.symlink_to(outside, target_is_directory=True)
    except OSError:
        pytest.skip("Directory symlinks are not available for this Windows account.")
    with pytest.raises(PathPolicyError):
        _handoff(files, tmp_path, link / "escaped.dwg", digest)
