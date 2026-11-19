"""Tests for strict one-use AutoCAD drawing-delta evidence."""

from __future__ import annotations

import hashlib
import json
from pathlib import Path

import pytest

from agent.exceptions import AutoCADContextError
import integrations.autocad_drawing_delta as delta_module
from integrations.autocad_context import ContextLimits
from integrations.autocad_drawing_delta import (
    load_drawing_delta,
    parse_drawing_delta_manifest,
)
from integrations.autocad_operator_channel import (
    ActiveAutoCADDocument,
    AutoCADOperatorResult,
)


DOCUMENT_ID = "12345678-1234-1234-1234-1234567890ab"


def _context(context_id: str) -> dict[str, object]:
    return {
        "schema_version": 1,
        "context_id": context_id,
        "captured_at": "2026-08-23T12:00:00.000000Z",
        "source_mode": "drawing",
        "privacy": {"classification": "project_confidential", "provider_egress_allowed": False},
        "document": {
            "document_id": DOCUMENT_ID,
            "identity_source": "fingerprint_guid",
            "name": "changed.dwg",
            "drawing_units": 4,
            "layout": "Model",
            "database_modified": 0,
        },
        "limits": ContextLimits().to_dict(),
        "truncated": False,
        "truncation_reasons": [],
        "layers": [{"name": "0", "color_index": 7, "linetype": "Continuous", "is_off": False, "is_frozen": False, "is_locked": False}],
        "entities": [{
            "object_id": "A1", "entity_type": "LINE",
            "original_type": "Autodesk.AutoCAD.DatabaseServices.Line",
            "layer": "0", "layout": "Model", "visible": True,
            "points": [[0.0, 0.0, 0.0], [1.0, 0.0, 0.0]],
            "radius": None, "rotation_degrees": None, "scale": None,
            "block_name": None, "text": None, "attributes": [],
        }],
    }


def _manifest(context_id: str, context_sha: str) -> dict[str, object]:
    return {
        "schema_version": 1,
        "delta_version": "autocad.drawing_delta.v1",
        "delta_id": "b" * 32,
        "captured_at": "2026-08-23T12:00:01.000000Z",
        "document": {
            "document_id": DOCUMENT_ID, "identity_source": "fingerprint_guid",
            "name": "changed.dwg", "drawing_units": 4,
            "baseline_source_drawing_sha256": "c" * 64,
            "current_source_drawing_sha256": "d" * 64,
            "database_modified_before": 0, "database_modified_after": 0,
        },
        "baseline": {"index_id": "e" * 32, "index_sha256": "f" * 64, "entity_count": 2},
        "complete_in_session_journal": True, "one_use": True,
        "maximum_candidates": 4096,
        "added_object_ids": ["A1"], "modified_object_ids": [], "removed_object_ids": ["B2"],
        "candidate_count": 2, "context_entity_count": 1,
        "chunk_count": 1,
        "chunks": [{"ordinal": 0, "context_id": context_id, "context_sha256": context_sha, "entity_count": 1}],
        "drawing_mutation_performed": False,
    }


def test_drawing_delta_loads_exact_changed_context_and_removed_handle(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    workspace = tmp_path / "workspace"
    context_root = workspace / ".ai-engineer/autocad/context/inbox"
    delta_root = workspace / ".ai-engineer/autocad/drawing-delta/inbox"
    context_root.mkdir(parents=True)
    delta_root.mkdir(parents=True)
    context_id = "a" * 32
    context_raw = json.dumps(_context(context_id), separators=(",", ":")).encode()
    context_sha = hashlib.sha256(context_raw).hexdigest()
    (context_root / f"{context_id}.json").write_bytes(context_raw)
    manifest_raw = json.dumps(_manifest(context_id, context_sha), separators=(",", ":")).encode()
    manifest_sha = hashlib.sha256(manifest_raw).hexdigest()
    (delta_root / f"{'b' * 32}.json").write_bytes(manifest_raw)

    result = load_drawing_delta(
        workspace, delta_id="b" * 32, evidence_sha256=manifest_sha,
        expected_document_id=DOCUMENT_ID,
        expected_current_source_sha256="d" * 64,
        expected_baseline_index_id="e" * 32,
        expected_baseline_index_sha256="f" * 64,
    )

    assert result.captured_object_ids == ("A1",)
    assert result.manifest.removed_object_ids == ("B2",)
    assert result.drawing_mutation_performed is False

    status_path = workspace / "status.json"
    status_path.write_text("{}", encoding="utf-8")
    active = ActiveAutoCADDocument(
        "2026-08-23T12:00:00.000000Z", DOCUMENT_ID, "changed.dwg", "d" * 64,
        0, True, status_path, "1" * 64,
    )
    operator_path = workspace / "operator.json"
    operator_path.write_text("{}", encoding="utf-8")
    operator_sha = hashlib.sha256(operator_path.read_bytes()).hexdigest()
    operator_result = AutoCADOperatorResult(
        1, "autocad.operator_command_result.v1", "1" * 32, "2" * 32,
        "3" * 64, "2026-08-23T12:00:02.000000Z", DOCUMENT_ID,
        "changed.dwg", "d" * 64, "capture_drawing_delta", "completed",
        0, 0, "b" * 32, manifest_sha, 2, 1.0, False, None,
    )
    monkeypatch.setattr(delta_module, "discover_active_documents", lambda root: (active,))
    monkeypatch.setattr(delta_module, "enqueue_operator_command", lambda *a, **k: object())
    monkeypatch.setattr(
        delta_module, "wait_for_operator_result",
        lambda *a, **k: (operator_result, operator_path, operator_sha),
    )
    captured = delta_module.capture_active_drawing_delta(
        workspace,
        baseline_index_id="e" * 32,
        baseline_index_sha256="f" * 64,
    )
    assert captured.delta.manifest.candidate_count == 2
    assert captured.operator_result_sha256 == operator_sha


def test_drawing_delta_rejects_overlap_and_incomplete_journal() -> None:
    raw = _manifest("a" * 32, "f" * 64)
    raw["modified_object_ids"] = ["A1"]
    raw["candidate_count"] = 3
    with pytest.raises(AutoCADContextError, match="overlap"):
        parse_drawing_delta_manifest(raw)
    raw = _manifest("a" * 32, "f" * 64)
    raw["complete_in_session_journal"] = False
    with pytest.raises(AutoCADContextError, match="safety flags"):
        parse_drawing_delta_manifest(raw)
