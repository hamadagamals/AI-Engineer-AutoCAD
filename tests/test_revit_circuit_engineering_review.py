from __future__ import annotations

import hashlib
import json
import uuid
from datetime import UTC, datetime, timedelta
from pathlib import Path

import pytest

from agent.exceptions import RevitIntegrationError
from integrations.revit_circuit_assignment_preview import _payload_hash as preview_hash
from integrations.revit_circuit_engineering_review import create_circuit_engineering_review


def _evidence(tmp_path: Path) -> tuple[Path, Path, Path, Path, Path]:
    root = tmp_path / "project"
    evidence = root / "workspace" / "evidence"
    evidence.mkdir(parents=True)
    bridge = tmp_path / "bridge"
    bridge.mkdir()
    source = evidence / "result.rvt"
    source.write_bytes(b"result-rvt")
    connector = {"connector_id": 1, "domain": "DomainElectrical", "connector_type": "End",
                 "electrical_system_type": "PowerCircuit", "mep_system_id": -1}
    preview: dict[str, object] = {
        "schema_version": 1, "preview_id": str(uuid.uuid4()),
        "created_utc": datetime.now(UTC).isoformat(),
        "expires_utc": (datetime.now(UTC) + timedelta(hours=1)).isoformat(),
        "action": "add_existing_fixture_to_existing_electrical_system",
        "document_title": "Result", "source_sha256": hashlib.sha256(source.read_bytes()).hexdigest().upper(),
        "fixture_element_id": 10, "reference_element_id": 11, "target_system_id": 20,
        "target_system": {"system_id": 20, "system_type": "PowerCircuit", "circuit_number": "5",
                          "panel_name": "P109", "load_name": "Lighting", "voltage_v": 120,
                          "apparent_load_va": 80, "true_load_w": 76, "power_factor": .95, "poles": 1},
        "fixture": {"family_name": "Light", "type_name": "Downlight", "mark": "2", "connectors": [connector]},
        "reference": {"family_name": "Light", "type_name": "Downlight", "mark": "1", "connectors": [connector]},
        "checks": {"fixture_unassigned": True, "same_family_and_type": True,
                   "connector_signature_match": True, "power_circuit_connector_present": True,
                   "target_membership_confirmed_on_reference": True},
        "unresolved_reviews": ["Engineering review required."], "status": "review_required",
        "execution_allowed": False, "provider_egress_performed": False, "document_mutation_performed": False,
    }
    preview["preview_sha256"] = preview_hash(preview)
    preview_path = evidence / "preview.json"
    preview_path.write_text(json.dumps(preview), encoding="utf-8")
    circuit = {"system_id": 20, "system_type": "PowerCircuit", "circuit_number": "5",
               "panel_name": "P109", "load_name": "Lighting", "voltage_v": 120,
               "apparent_load_va": 100, "true_load_w": 95, "power_factor": .95, "poles": 1,
               "rating_a": 20, "frame_a": 400, "apparent_current_a": .833333,
               "true_current_a": .791667, "voltage_drop_percent": 1.473381, "phase_label": "C",
               "balanced_load": False, "start_slot": 5, "wire_size": "#12", "hot_conductors": 1,
               "neutral_conductors": 1, "ground_conductors": 1, "apparent_current_phase_a": 0,
               "apparent_current_phase_b": 0, "apparent_current_phase_c": .833333,
               "protection_parameters": [{"source": "instance", "name": "Rating",
                   "parameter_id": -1140137, "storage_type": "Double", "value_display": "20 A"}],
               "panel": {"element_id": 30, "family_name": "Panel", "type_name": "225 A",
                         "mcb_rating_a": 225, "voltage_v": None, "number_of_phases": 3,
                         "number_of_wires": 4, "total_connected_current_a": 67.769263,
                         "total_demand_current_a": 67.769263, "supply_from": "MB13", "mains_type": "",
                         "current_phase_a": 85.491306, "current_phase_b": 62.241306,
                         "current_phase_c": 57.09776, "short_circuit_rating_display": "",
                         "protection_parameters": [], "incoming_systems": [{
                             "system_id": 40, "system_type": "PowerCircuit",
                             "circuit_number": "3", "panel_name": "MB13", "load_name": "P109",
                             "voltage_v": 208, "apparent_load_va": 24415, "true_load_w": 24414.98,
                             "power_factor": .999999, "poles": 3, "rating_a": 20,
                             "frame_a": 400, "apparent_current_a": 67.769263,
                             "voltage_drop_percent": 45.438594, "length_mm": 26129.17061,
                             "wire_size": "3-#4, 1-#4, 1-#4", "protection_parameters": [],
                             "upstream_panel": {"element_id": 50, "family_name": "Meter Bank",
                                 "type_name": "225A - 4 Socket", "mcb_rating_a": None,
                                 "short_circuit_rating_display": "", "protection_parameters": []},
                         }]}}
    query = {"schema_version": 1, "request_id": str(uuid.uuid4()), "document_title": "Result",
             "requested_element_id": 10, "total_fixture_count": 1, "returned_fixture_count": 1,
             "truncated": False, "fixtures": [{"element_id": 10, "family_name": "Light",
             "type_name": "Downlight", "mark": "2", "circuit_count": 1, "circuits": [circuit],
             "connector_count": 1, "connectors": [connector], "voltage_v": None,
             "apparent_load_va": None}], "provider_egress_performed": False,
             "document_mutation_performed": False}
    query_path = bridge / "query.json"
    query_path.write_text(json.dumps(query), encoding="utf-8")
    return root, bridge, source, preview_path, query_path


def test_engineering_review_is_deterministic_and_does_not_claim_compliance(tmp_path: Path) -> None:
    root, bridge, source, preview, query = _evidence(tmp_path)
    output = root / "workspace" / "evidence" / "review.json"
    value = create_circuit_engineering_review(
        workspace_root=root, bridge_root=bridge, source_model=source,
        approved_preview_path=preview, post_assignment_query_path=query, output_path=output,
    )
    calculated = value["calculated"]
    assert calculated["added_apparent_load_va"] == 20
    assert calculated["added_true_load_w"] == 19
    assert calculated["branch_apparent_current_utilization_percent"] == 4.166665
    assert calculated["panel_demand_current_utilization_percent"] == 30.119672
    assert value["determinations"]["code_compliance_evaluated"] is False
    assert value["determinations"]["circuit_protection_parameter_count"] == 1
    assert value["determinations"]["upstream_feeder_count"] == 1
    assert value["determinations"]["all_upstream_feeders_have_positive_rating_margin"] is False
    feeder = calculated["upstream_feeders"][0]
    assert feeder["rating_utilization_percent"] == 338.846315
    assert feeder["rating_headroom_a"] == -47.769263
    assert value["document_mutation_performed"] is False
    assert len(value["review_sha256"]) == 64


def test_engineering_review_rejects_wrong_membership(tmp_path: Path) -> None:
    root, bridge, source, preview, query = _evidence(tmp_path)
    value = json.loads(query.read_text(encoding="utf-8"))
    value["fixtures"][0]["circuits"][0]["system_id"] = 21
    query.write_text(json.dumps(value), encoding="utf-8")
    with pytest.raises(RevitIntegrationError, match="membership"):
        create_circuit_engineering_review(
            workspace_root=root, bridge_root=bridge, source_model=source,
            approved_preview_path=preview, post_assignment_query_path=query,
            output_path=root / "workspace" / "evidence" / "review.json",
        )
