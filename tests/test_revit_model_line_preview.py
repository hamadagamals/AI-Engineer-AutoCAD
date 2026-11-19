from __future__ import annotations

import hashlib
import json
from pathlib import Path

import pytest

from agent.exceptions import RevitIntegrationError
from integrations.revit_model_line_preview import (
    create_model_line_preview,
    load_model_line_preview,
)


def test_model_line_preview_is_hash_bound_and_review_only(tmp_path: Path) -> None:
    model = tmp_path / "fixture.rvt"
    model.write_bytes(b"fixture")
    digest = hashlib.sha256(model.read_bytes()).hexdigest().upper()
    output = tmp_path / "preview.json"
    value = create_model_line_preview(
        workspace_root=tmp_path, source_model=model, expected_source_sha256=digest,
        document_title="Fixture", segments_mm=[((0, 0, 0), (1000, 0, 0))],
        output_path=output,
    )
    assert value["execution_allowed"] is False
    assert value["save_allowed"] is False
    assert load_model_line_preview(output, workspace_root=tmp_path)["segments"][0]["length_mm"] == 1000


def test_model_line_preview_rejects_tamper_and_non_horizontal_line(tmp_path: Path) -> None:
    model = tmp_path / "fixture.rvt"
    model.write_bytes(b"fixture")
    digest = hashlib.sha256(model.read_bytes()).hexdigest().upper()
    output = tmp_path / "preview.json"
    with pytest.raises(RevitIntegrationError, match="horizontal"):
        create_model_line_preview(
            workspace_root=tmp_path, source_model=model, expected_source_sha256=digest,
            document_title="Fixture", segments_mm=[((0, 0, 0), (1000, 0, 1))],
            output_path=output,
        )
    create_model_line_preview(
        workspace_root=tmp_path, source_model=model, expected_source_sha256=digest,
        document_title="Fixture", segments_mm=[((0, 0, 0), (1000, 0, 0))],
        output_path=output,
    )
    value = json.loads(output.read_text())
    value["segments"][0]["end_mm"][0] = 2000
    output.write_text(json.dumps(value), encoding="utf-8")
    with pytest.raises(RevitIntegrationError, match="hash"):
        load_model_line_preview(output, workspace_root=tmp_path)
