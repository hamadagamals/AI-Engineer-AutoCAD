import json
from pathlib import Path

import pytest

from agent.exceptions import RevitIntegrationError
from integrations.revit_school_outlet_connectors import load_outlet_connector_query


def _report() -> dict:
    return {
        "schema_version": 1, "request_id": "query-1", "document_title": "School V5",
        "document_sha256": "A" * 64, "total_count": 16,
        "provider_egress_performed": False, "document_mutation_performed": False,
        "outlets": [
            {
                "element_id": 100 + i, "family_name": "Outlet-Duplex",
                "type_name": "Single", "host_id": 500 + i,
                "host_category": "Walls", "voltage_v": None,
                "apparent_load_va": None,
                "connectors": [{"id": 1, "domain": "DomainElectrical",
                                "electrical_system_type": "PowerCircuit",
                                "assigned_system_id": -1}],
                "electrical_parameters": [],
            }
            for i in range(16)
        ],
    }


def _load(tmp_path: Path, report: dict) -> dict:
    target = tmp_path / "result.json"
    target.write_text(json.dumps(report), encoding="utf-8")
    return load_outlet_connector_query(
        target, allowed_root=tmp_path, request_id="query-1",
        document_title="School V5", source_sha256="A" * 64)


def test_complete_read_only_outlet_connector_intake(tmp_path: Path) -> None:
    assert len(_load(tmp_path, _report())["outlets"]) == 16


@pytest.mark.parametrize("change", ["duplicate", "mutation", "host", "voltage"])
def test_bad_outlet_connector_intake_is_rejected(tmp_path: Path, change: str) -> None:
    report = _report()
    if change == "duplicate":
        report["outlets"][1]["element_id"] = 100
    elif change == "mutation":
        report["document_mutation_performed"] = True
    elif change == "host":
        report["outlets"][0]["host_category"] = "Floors"
    else:
        report["outlets"][0]["voltage_v"] = -220
    with pytest.raises(RevitIntegrationError):
        _load(tmp_path, report)
