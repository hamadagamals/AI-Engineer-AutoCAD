from __future__ import annotations

import hashlib
import json
from pathlib import Path

import pytest

from agent.exceptions import RevitIntegrationError
from integrations.revit_family_file_query import (
    load_family_file_query,
    submit_family_file_request,
)


def test_family_file_request_stages_hash_bound_read_only_files(tmp_path: Path) -> None:
    source = tmp_path / "allowed"
    source.mkdir()
    family = source / "fixture.rfa"
    family.write_bytes(b"bounded family fixture")
    bridge = tmp_path / "bridge"
    request_id, response_path = submit_family_file_request(
        "School-Architectural-electrical-working",
        [family],
        allowed_root=source,
        bridge_root=bridge,
    )
    value = json.loads((bridge / "requests" / "pending.json").read_text(encoding="utf-8"))
    digest = hashlib.sha256(family.read_bytes()).hexdigest().upper()
    assert value["request_id"] == request_id
    assert value["action"] == "family_file_query"
    assert value["family_files"] == [f"{digest}.rfa"]
    assert value["provider_egress_permitted"] is False
    assert value["document_mutation_permitted"] is False
    assert (bridge / "family-intake" / f"{digest}.rfa").read_bytes() == family.read_bytes()
    assert response_path == bridge / "responses" / f"{request_id}.json"


def test_family_file_request_rejects_escape_and_count(tmp_path: Path) -> None:
    root = tmp_path / "allowed"
    root.mkdir()
    escaped = tmp_path / "escaped.rfa"
    escaped.write_bytes(b"escape")
    with pytest.raises(RevitIntegrationError, match="escaped"):
        submit_family_file_request("Fixture", [escaped], allowed_root=root, bridge_root=tmp_path / "bridge")
    with pytest.raises(RevitIntegrationError, match="1..8"):
        submit_family_file_request("Fixture", [], allowed_root=root, bridge_root=tmp_path / "bridge")


def test_family_file_loader_validates_read_only_result(tmp_path: Path) -> None:
    root = tmp_path / "bridge"
    result = root / "family-files" / "result.json"
    result.parent.mkdir(parents=True)
    result.write_text(json.dumps({
        "schema_version": 1,
        "request_id": "request",
        "document_title": "School",
        "files": [{
            "file_sha256": "A" * 64,
            "format": "2019",
            "in_memory_upgrade": True,
            "family_name": "Panel",
            "category": "Lighting Fixtures",
            "family_placement_type": "WorkPlaneBased",
            "host_requirement": "work_plane_or_face",
            "connectors": [{"domain": "DomainElectrical", "system_classification": "PowerCircuit",
                            "shape": "Round", "is_primary": True}],
            "types": [{"name": "31 W", "parameters": []}],
        }],
        "provider_egress_performed": False,
        "document_mutation_performed": False,
    }), encoding="utf-8")
    loaded = load_family_file_query(result, allowed_root=root)
    assert loaded.files[0]["family_name"] == "Panel"
    assert loaded.files[0]["connectors"][0]["domain"] == "DomainElectrical"


def test_family_file_loader_rejects_mutation_and_bad_hash(tmp_path: Path) -> None:
    root = tmp_path / "bridge"
    result = root / "family-files" / "result.json"
    result.parent.mkdir(parents=True)
    base = {
        "schema_version": 1, "request_id": "request", "document_title": "School",
        "files": [{"file_sha256": "bad", "format": "2019", "in_memory_upgrade": True,
                   "family_name": "Panel", "category": "Lighting Fixtures",
                   "family_placement_type": "WorkPlaneBased", "host_requirement": "work_plane_or_face",
                   "connectors": [], "types": []}],
        "provider_egress_performed": False, "document_mutation_performed": False,
    }
    result.write_text(json.dumps(base), encoding="utf-8")
    with pytest.raises(RevitIntegrationError, match="hash"):
        load_family_file_query(result, allowed_root=root)
    base["files"][0]["file_sha256"] = "A" * 64
    base["document_mutation_performed"] = True
    result.write_text(json.dumps(base), encoding="utf-8")
    with pytest.raises(RevitIntegrationError, match="read-only"):
        load_family_file_query(result, allowed_root=root)


def test_family_file_loader_rejects_unbounded_nested_details(tmp_path: Path) -> None:
    root = tmp_path / "bridge"
    result = root / "family-files" / "result.json"
    result.parent.mkdir(parents=True)
    value = {
        "schema_version": 1, "request_id": "request", "document_title": "School",
        "files": [{"file_sha256": "A" * 64, "format": "2019", "in_memory_upgrade": True,
                   "family_name": "Panel", "category": "Lighting Fixtures",
                   "family_placement_type": "WorkPlaneBased", "host_requirement": "work_plane_or_face",
                   "connectors": [{"domain": "DomainElectrical", "system_classification": "PowerCircuit",
                                   "shape": "Round", "is_primary": "yes"}],
                   "types": []}],
        "provider_egress_performed": False, "document_mutation_performed": False,
    }
    result.write_text(json.dumps(value), encoding="utf-8")
    with pytest.raises(RevitIntegrationError, match="connector"):
        load_family_file_query(result, allowed_root=root)
    value["files"][0]["connectors"] = []
    value["files"][0]["types"] = [{"name": "31 W", "parameters": [{}]}]
    result.write_text(json.dumps(value), encoding="utf-8")
    with pytest.raises(RevitIntegrationError, match="parameter"):
        load_family_file_query(result, allowed_root=root)
