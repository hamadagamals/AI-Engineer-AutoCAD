"""Tests for the DB3-GA2 right-occurrence highlight preview."""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from integrations.build_db3_ga2_right_group_preview import build_preview
from integrations.build_lighting_panel_schedule_evidence import load_index_entities


AUTOCAD_ROOT = Path(r"D:\AI-Engineer\workspace\.ai-engineer\autocad")
EVIDENCE = Path(r"D:\AI-Engineer\workspace\reports\lighting-panel-schedule-candidate-evidence-20260824.json")
INDEX_ID = "bbc940194f1f407ab8231386d9319029"


@pytest.mark.skipif(not AUTOCAD_ROOT.exists() or not EVIDENCE.exists(), reason="Live project evidence is not available")
def test_right_group_is_exact_and_keeps_5_7_ambiguity() -> None:
    manifest, entities = load_index_entities(AUTOCAD_ROOT, INDEX_ID)
    evidence = json.loads(EVIDENCE.read_text(encoding="utf-8"))
    result = build_preview(manifest=manifest, entities=entities, evidence=evidence)

    assert result["selected_circuits"] == [2, 4, 6, 7]
    assert result["absent_circuits"] == [1, 3, 5]
    assert result["engineer_reclassification"]["assigned_circuit"] == 7
    assert result["selected_route_count"] == 30
    assert len(set(result["selected_route_handles"])) == 30
    assert result["selected_block_count"] == 28
    assert "1A1C3459" in result["selected_route_handles"]
    assert "1A1C33E4" in result["selected_block_handles"]
    assert result["drawing_mutation_performed"] is False
    assert len(result["preview_sha256"]) == 64

    changed = [dict(entity) for entity in entities]
    next(entity for entity in changed if entity["object_id"] == "1A1C308E")["layer"] = "CHANGED"
    with pytest.raises(ValueError, match="missing or changed"):
        build_preview(manifest=manifest, entities=changed, evidence=evidence)
