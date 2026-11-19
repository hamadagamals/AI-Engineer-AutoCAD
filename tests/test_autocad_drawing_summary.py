"""Tests for bounded, approval-gated drawing-index provider summaries."""

from __future__ import annotations

import hashlib
import json
from pathlib import Path

from agent.command_engine import CommandRegistry
from agent.core_commands import CoreServices, register_core_commands
from agent.permissions import RiskLevel
from integrations.autocad_context import ContextLimits
from integrations.autocad_drawing_summary import summarize_drawing_index_for_ai


DOCUMENT_ID = "12345678-1234-1234-1234-1234567890ab"
SOURCE_SHA = "d" * 64


def _write_index(workspace: Path) -> tuple[str, str]:
    context_id = "1" * 32
    context = {
        "schema_version": 1,
        "context_id": context_id,
        "captured_at": "2026-09-19T10:00:00.000000Z",
        "source_mode": "drawing",
        "privacy": {"classification": "project_confidential", "provider_egress_allowed": False},
        "document": {
            "document_id": DOCUMENT_ID,
            "identity_source": "fingerprint_guid",
            "name": "panel.dwg",
            "drawing_units": 4,
            "layout": "Model",
            "database_modified": 0,
        },
        "limits": ContextLimits().to_dict(),
        "truncated": False,
        "truncation_reasons": [],
        "layers": [
            {"name": "POWER", "color_index": 1, "linetype": "Continuous", "is_off": False, "is_frozen": False, "is_locked": False}
        ],
        "entities": [
            {"object_id": "A1", "entity_type": "LINE", "original_type": "Autodesk.AutoCAD.DatabaseServices.Line", "layer": "POWER", "layout": "Model", "visible": True, "points": [[0, 0, 0], [1600, 2000, 0]], "radius": None, "rotation_degrees": None, "scale": None, "block_name": None, "text": None, "attributes": []},
            {"object_id": "B2", "entity_type": "INSERT", "original_type": "Autodesk.AutoCAD.DatabaseServices.BlockReference", "layer": "POWER", "layout": "Model", "visible": True, "points": [[800, 1000, 0]], "radius": None, "rotation_degrees": 0, "scale": [1, 1, 1], "block_name": "MAIN BREAKER", "text": None, "attributes": []},
            {"object_id": "C3", "entity_type": "TEXT", "original_type": "Autodesk.AutoCAD.DatabaseServices.DBText", "layer": "POWER", "layout": "Model", "visible": True, "points": [[10, 20, 0]], "radius": None, "rotation_degrees": None, "scale": None, "block_name": None, "text": "1600 x 2000 PANEL", "attributes": []},
        ],
    }
    context_bytes = json.dumps(context, separators=(",", ":")).encode()
    context_root = workspace / ".ai-engineer" / "autocad" / "context" / "inbox"
    index_root = workspace / ".ai-engineer" / "autocad" / "drawing-index" / "inbox"
    context_root.mkdir(parents=True)
    index_root.mkdir(parents=True)
    (context_root / f"{context_id}.json").write_bytes(context_bytes)
    index_id = "a" * 32
    manifest = {
        "schema_version": 1,
        "index_version": "autocad.drawing_index.v1",
        "index_id": index_id,
        "captured_at": "2026-09-19T10:00:01.000000Z",
        "document": {"document_id": DOCUMENT_ID, "identity_source": "fingerprint_guid", "name": "panel.dwg", "source_drawing_sha256": SOURCE_SHA, "drawing_units": 4, "database_modified_before": 0, "database_modified_after": 0},
        "scope": {"model_space": True, "paper_space": True, "nested_block_definitions": False, "external_references": False},
        "limits": {"maximum_entities_per_chunk": 64, "maximum_total_entities": 200000, "maximum_chunks": 4096, "maximum_duration_seconds": 120},
        "complete": True,
        "chunk_count": 1,
        "entity_count": 3,
        "chunks": [{"ordinal": 0, "context_id": context_id, "context_sha256": hashlib.sha256(context_bytes).hexdigest(), "entity_count": 3}],
        "drawing_mutation_performed": False,
    }
    manifest_bytes = json.dumps(manifest, separators=(",", ":")).encode()
    (index_root / f"{index_id}.json").write_bytes(manifest_bytes)
    return index_id, hashlib.sha256(manifest_bytes).hexdigest()


def test_summary_is_bounded_and_exposes_engineering_inventory(tmp_path: Path) -> None:
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    index_id, digest = _write_index(workspace)

    result = summarize_drawing_index_for_ai(workspace, index_id=index_id, evidence_sha256=digest)

    assert result["entity_count"] == 3
    assert result["point_bounds"]["span"] == [1600.0, 2000.0, 0.0]
    assert result["block_references"] == [{"name": "MAIN BREAKER", "count": 1}]
    assert result["notable_text"] == ["1600 x 2000 PANEL"]
    assert result["provider_egress_performed"] is True
    assert result["drawing_mutation_performed"] is False
    assert "path" not in json.dumps(result).casefold()
    assert len(json.dumps(result, ensure_ascii=False).encode("utf-8")) < 4096


def test_summary_command_is_high_risk_and_hash_bound(tmp_path: Path) -> None:
    services = CoreServices(
        files=None, scanner=None, system=None, processes=None, vscode=None,
        autocad=None, terminal=None, operator_workspace_root=tmp_path,
    )
    registry = CommandRegistry()
    register_core_commands(registry, services)
    definition = registry.get("autocad.summarize_drawing_index_for_ai")

    assert definition.risk is RiskLevel.HIGH
    assert definition.permission == "autocad.context.read"
    assert definition.validator({"index_id": "a" * 32, "evidence_sha256": "b" * 64}) == {
        "index_id": "a" * 32,
        "evidence_sha256": "b" * 64,
    }
