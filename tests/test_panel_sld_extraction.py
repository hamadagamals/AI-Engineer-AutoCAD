"""Tests for bounded existing panel-schedule and SLD marker extraction."""

from __future__ import annotations

import hashlib
from pathlib import Path

from core.panel_schedule import ScheduleFindingOutcome, generate_panel_schedule
from core.panel_sld_extraction import extract_panel_sld_observations
from integrations.autocad_context import map_context_to_electrical_model, parse_autocad_context


CATALOG = Path(__file__).resolve().parents[1] / "plugins" / "electrical_symbols" / "catalog.json"
CONTEXT_ID = "b" * 32
EVIDENCE = hashlib.sha256(b"m21 extraction context").hexdigest()


def _insert(object_id: str, block: str, attributes: dict[str, str]) -> dict[str, object]:
    return {
        "object_id": object_id,
        "entity_type": "INSERT",
        "original_type": "BlockReference",
        "layer": "E-POWR-DEVC",
        "layout": "Model",
        "visible": True,
        "points": [[0, 0, 0]],
        "radius": None,
        "rotation_degrees": 0,
        "scale": [1, 1, 1],
        "block_name": block,
        "text": None,
        "attributes": [{"tag": key, "value": value} for key, value in attributes.items()],
    }


def _raw_context(
    *,
    omit_way_attribute: str | None = None,
    duplicate_circuit: bool = False,
    load_source_object_id: str = "10",
    load_binding: str | None = None,
):
    way_one = {
        "WAY_ID": "way:1", "PANEL_ID": "panel:db1", "CIRCUIT_ID": "circuit:1",
        "CIRCUIT_NAME": "Lighting", "WAY_NUMBER": "1", "WAY_KIND": "load",
        "PHASE": "l1", "POLE_COUNT": "1", "PROTECTION_ID": "protection:1",
        "PROTECTION_KIND": "mcb", "PROTECTION_DESIGNATION": "10 A / 6 kA",
        "RATED_CURRENT_A": "10", "BREAKING_CAPACITY_A": "6000",
        "LOAD_BINDINGS": load_binding or f"load:1@{load_source_object_id}",
        "LOAD_NAMES": "Lighting load",
    }
    if omit_way_attribute is not None:
        del way_one[omit_way_attribute]
    way_two = {
        "WAY_ID": "way:2", "PANEL_ID": "panel:db1",
        "CIRCUIT_ID": "circuit:1" if duplicate_circuit else "circuit:2",
        "CIRCUIT_NAME": "Spare", "WAY_NUMBER": "2", "WAY_KIND": "spare",
        "PHASE": "l2", "POLE_COUNT": "1", "PROTECTION_ID": "protection:2",
        "PROTECTION_KIND": "mcb", "PROTECTION_DESIGNATION": "16 A / 6 kA spare",
        "RATED_CURRENT_A": "16", "BREAKING_CAPACITY_A": "6000",
    }
    entities = [
        _insert("10", "AIENG_E_SOCKET_SINGLE_V1", {}),
        _insert("20", "AIENG_M21_PANEL_V1", {"PANEL_ID": "panel:db1", "PANEL_NAME": "DB-1"}),
        _insert("30", "AIENG_M21_WAY_V1", way_one),
        _insert("31", "AIENG_M21_WAY_V1", way_two),
        _insert("40", "AIENG_M21_SLD_NODE_V1", {
            "NODE_ID": "node:panel", "NODE_KIND": "panel", "REF_ID": "panel:db1", "CIRCUIT_ID": "",
        }),
        _insert("41", "AIENG_M21_SLD_NODE_V1", {
            "NODE_ID": "node:protection-1", "NODE_KIND": "protective_device",
            "REF_ID": "protection:1", "CIRCUIT_ID": "circuit:1",
        }),
        _insert("42", "AIENG_M21_SLD_NODE_V1", {
            "NODE_ID": "node:load-1", "NODE_KIND": "load", "REF_ID": "load:1", "CIRCUIT_ID": "circuit:1",
        }),
        _insert("43", "AIENG_M21_SLD_NODE_V1", {
            "NODE_ID": "node:protection-2", "NODE_KIND": "protective_device",
            "REF_ID": "protection:2", "CIRCUIT_ID": "circuit:2",
        }),
        _insert("50", "AIENG_M21_SLD_EDGE_V1", {
            "EDGE_ID": "edge:1", "FROM_NODE_ID": "node:panel", "TO_NODE_ID": "node:protection-1",
            "CIRCUIT_ID": "circuit:1",
        }),
        _insert("51", "AIENG_M21_SLD_EDGE_V1", {
            "EDGE_ID": "edge:2", "FROM_NODE_ID": "node:protection-1", "TO_NODE_ID": "node:load-1",
            "CIRCUIT_ID": "circuit:1",
        }),
        _insert("52", "AIENG_M21_SLD_EDGE_V1", {
            "EDGE_ID": "edge:3", "FROM_NODE_ID": "node:panel", "TO_NODE_ID": "node:protection-2",
            "CIRCUIT_ID": "circuit:2",
        }),
    ]
    return {
        "schema_version": 1,
        "context_id": CONTEXT_ID,
        "captured_at": "2026-08-11T12:00:00Z",
        "source_mode": "selection",
        "privacy": {"classification": "project_confidential", "provider_egress_allowed": False},
        "document": {
            "document_id": "12345678-1234-1234-1234-1234567890ab",
            "identity_source": "fingerprint_guid", "name": "panel.dwg", "drawing_units": 4,
            "layout": "Model", "database_modified": 0,
        },
        "limits": {
            "maximum_entities": 512, "maximum_layers": 128,
            "maximum_attributes_per_entity": 32, "maximum_points_per_entity": 256,
            "maximum_text_bytes": 4096, "maximum_payload_bytes": 1048576,
            "maximum_nesting": 8, "maximum_duration_seconds": 30,
        },
        "truncated": False, "truncation_reasons": [],
        "layers": [{"name": "E-POWR-DEVC", "color_index": 2, "linetype": "Continuous",
                    "is_off": False, "is_frozen": False, "is_locked": False}],
        "entities": entities,
    }


def _inputs(**changes: object):
    context = parse_autocad_context(_raw_context(**changes))
    model = map_context_to_electrical_model(context, evidence_sha256=EVIDENCE,
                                            symbol_catalog_path=CATALOG)
    return context, model


def test_supported_markers_create_semantic_model_contract_and_schedule() -> None:
    context, model = _inputs()
    result = extract_panel_sld_observations(
        context, model, context_evidence_sha256=EVIDENCE,
        reviewed_by="engineer:gamal", assumptions=("Supported marker profile reviewed.",),
    )
    assert result.complete is True
    assert result.supported_marker_count == 10
    assert result.reviewed_by == "engineer:gamal"
    assert result.findings == ()
    assert result.semantic_model is not None
    assert result.contract is not None
    assert [item.circuit_id for item in result.semantic_model.circuits] == ["circuit:1", "circuit:2"]
    assert result.contract.sld_nodes[0].source.object_id == "40"
    assert result.contract.sld_edges[0].source.object_id == "50"
    schedule = generate_panel_schedule(result.semantic_model, result.contract)
    assert schedule.outgoing_way_count == 2
    assert schedule.assigned_load_count == 1
    assert result.existing_schedule_extraction_performed is True
    assert result.provider_egress_performed is False
    assert result.drawing_mutation_performed is False


def test_incomplete_marker_emits_unknown_without_partial_semantics() -> None:
    context, model = _inputs(omit_way_attribute="PHASE")
    result = extract_panel_sld_observations(
        context, model, context_evidence_sha256=EVIDENCE,
        reviewed_by="engineer:gamal", assumptions=("Reviewed.",),
    )
    assert result.complete is False
    assert any(item.outcome is ScheduleFindingOutcome.UNKNOWN for item in result.findings)
    assert result.semantic_model is None
    assert result.contract is None


def test_conflicting_supported_identities_emit_conflict_without_guessing() -> None:
    context, model = _inputs(duplicate_circuit=True)
    result = extract_panel_sld_observations(
        context, model, context_evidence_sha256=EVIDENCE,
        reviewed_by="engineer:gamal", assumptions=("Reviewed.",),
    )
    assert result.complete is False
    assert result.findings[0].outcome is ScheduleFindingOutcome.CONFLICT
    assert result.semantic_model is None
    assert result.drawing_mutation_performed is False


def test_load_handle_must_resolve_in_the_exact_recognized_device_context() -> None:
    context, model = _inputs(load_source_object_id="ABC")
    result = extract_panel_sld_observations(
        context, model, context_evidence_sha256=EVIDENCE,
        reviewed_by="engineer:gamal", assumptions=("Reviewed.",),
    )
    assert result.complete is False
    assert result.semantic_model is None
    assert any(item.outcome is ScheduleFindingOutcome.UNKNOWN for item in result.findings)


def test_load_binding_must_be_an_atomic_id_and_handle_pair() -> None:
    context, model = _inputs(load_binding="load:1")
    result = extract_panel_sld_observations(
        context, model, context_evidence_sha256=EVIDENCE,
        reviewed_by="engineer:gamal", assumptions=("Reviewed.",),
    )
    assert result.complete is False
    assert result.semantic_model is None
    assert any(item.outcome is ScheduleFindingOutcome.UNKNOWN for item in result.findings)
