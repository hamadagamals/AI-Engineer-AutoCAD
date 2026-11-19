from __future__ import annotations

import hashlib
import json
from pathlib import Path

from integrations.revit_model_line_execution import submit_approved_model_lines
from integrations.revit_model_line_preview import create_model_line_preview


def test_approved_model_line_request_creates_verified_backup(tmp_path: Path) -> None:
    model = tmp_path / "fixture.rvt"
    model.write_bytes(b"fixture")
    digest = hashlib.sha256(model.read_bytes()).hexdigest().upper()
    preview_path = tmp_path / "preview.json"
    preview = create_model_line_preview(
        workspace_root=tmp_path, source_model=model, expected_source_sha256=digest,
        document_title="Fixture", segments_mm=[((0, 0, 0), (1000, 0, 0))],
        output_path=preview_path,
    )
    bridge = tmp_path / "bridge"
    request_id, response_path, backup = submit_approved_model_lines(
        workspace_root=tmp_path, preview_path=preview_path, source_model=model,
        confirmed_preview_sha256=preview["preview_sha256"], bridge_root=bridge,
    )
    request = json.loads((bridge / "requests" / "pending.json").read_text())
    assert request["request_id"] == request_id
    assert request["document_mutation_permitted"] is True
    assert request["save_allowed"] is False
    assert response_path.name == f"{request_id}.json"
    assert hashlib.sha256(backup.read_bytes()).hexdigest().upper() == digest
