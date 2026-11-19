from __future__ import annotations

import json
from pathlib import Path

import pytest

from agent.exceptions import RevitIntegrationError
from integrations.revit_circuit_assignment_preview import create_circuit_assignment_preview


def _query(request_id: str, element_id: int, *, connected: bool) -> dict[str, object]:
    circuits = [{
        "system_id": 1622590,
        "system_type": "PowerCircuit",
        "circuit_number": "5",
        "panel_name": "P109",
        "load_name": "Lighting",
        "voltage_v": 120.0,
        "apparent_load_va": 80.0,
        "true_load_w": 76.0,
        "power_factor": 0.95,
        "poles": 1,
    }] if connected else []
    return {
        "schema_version": 1,
        "request_id": request_id,
        "document_title": "Fixture",
        "requested_element_id": element_id,
        "total_fixture_count": 1,
        "returned_fixture_count": 1,
        "truncated": False,
        "fixtures": [{
            "element_id": element_id,
            "family_name": "Recessed Lamp - Round - LED",
            "type_name": "4 inch",
            "mark": "647" if connected else "712",
            "circuit_count": len(circuits),
            "circuits": circuits,
            "connector_count": 1,
            "connectors": [{
                "connector_id": 1,
                "domain": "DomainElectrical",
                "connector_type": "End",
                "electrical_system_type": "PowerCircuit",
                "mep_system_id": -1,
            }],
            "voltage_v": None,
            "apparent_load_va": None,
        }],
        "provider_egress_performed": False,
        "document_mutation_performed": False,
    }


def test_circuit_assignment_preview_is_hash_bound_and_non_executable(tmp_path: Path) -> None:
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    source = workspace / "fixture.rvt"
    source.write_bytes(b"rvt fixture")
    bridge = tmp_path / "bridge"
    query_dir = bridge / "lighting-circuits"
    query_dir.mkdir(parents=True)
    fixture_query = query_dir / "fixture.json"
    reference_query = query_dir / "reference.json"
    fixture_query.write_text(json.dumps(_query("fixture", 1735503, connected=False)), encoding="utf-8")
    reference_query.write_text(json.dumps(_query("reference", 1622255, connected=True)), encoding="utf-8")
    output = workspace / "preview.json"

    preview = create_circuit_assignment_preview(
        workspace_root=workspace,
        bridge_root=bridge,
        source_model=source,
        fixture_query_path=fixture_query,
        reference_query_path=reference_query,
        target_system_id=1622590,
        output_path=output,
    )

    assert preview["fixture_element_id"] == 1735503
    assert preview["target_system_id"] == 1622590
    assert preview["execution_allowed"] is False
    assert preview["status"] == "review_required"
    assert len(preview["preview_sha256"]) == 64


def test_circuit_assignment_preview_rejects_connector_mismatch(tmp_path: Path) -> None:
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    source = workspace / "fixture.rvt"
    source.write_bytes(b"rvt fixture")
    bridge = tmp_path / "bridge"
    query_dir = bridge / "lighting-circuits"
    query_dir.mkdir(parents=True)
    fixture = _query("fixture", 1735503, connected=False)
    fixture["fixtures"][0]["connectors"][0]["electrical_system_type"] = "Data"
    fixture_query = query_dir / "fixture.json"
    reference_query = query_dir / "reference.json"
    fixture_query.write_text(json.dumps(fixture), encoding="utf-8")
    reference_query.write_text(json.dumps(_query("reference", 1622255, connected=True)), encoding="utf-8")

    with pytest.raises(RevitIntegrationError, match="compatibility"):
        create_circuit_assignment_preview(
            workspace_root=workspace,
            bridge_root=bridge,
            source_model=source,
            fixture_query_path=fixture_query,
            reference_query_path=reference_query,
            target_system_id=1622590,
            output_path=workspace / "preview.json",
        )
