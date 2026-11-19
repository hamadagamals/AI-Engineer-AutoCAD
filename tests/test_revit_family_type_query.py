from __future__ import annotations

import json
from pathlib import Path

import pytest

from agent.exceptions import RevitIntegrationError
from integrations.revit_family_type_query import (
    load_family_type_query,
    submit_family_type_request,
)


def test_family_type_request_is_bounded_and_read_only(tmp_path: Path) -> None:
    request_id, response_path = submit_family_type_request(
        "Electrical Sample",
        "lighting_fixtures",
        maximum_elements=40,
        bridge_root=tmp_path,
    )
    value = json.loads(
        (tmp_path / "requests" / "pending.json").read_text(encoding="utf-8")
    )
    assert value["request_id"] == request_id
    assert value["action"] == "family_type_query"
    assert value["max_elements"] == 40
    assert value["provider_egress_permitted"] is False
    assert value["document_mutation_permitted"] is False
    assert response_path == tmp_path / "responses" / f"{request_id}.json"


def test_family_type_request_rejects_unknown_category_and_limit(tmp_path: Path) -> None:
    with pytest.raises(RevitIntegrationError, match="allowlisted"):
        submit_family_type_request("Fixture", "walls", bridge_root=tmp_path)
    with pytest.raises(RevitIntegrationError, match="1..64"):
        submit_family_type_request(
            "Fixture", "lighting_fixtures", maximum_elements=65, bridge_root=tmp_path,
        )


def test_family_type_loader_validates_identity_and_placement(tmp_path: Path) -> None:
    root = tmp_path / "bridge"
    path = root / "family-types" / "result.json"
    path.parent.mkdir(parents=True)
    path.write_text(json.dumps({
        "schema_version": 1,
        "request_id": "request",
        "document_title": "Electrical Sample",
        "category_key": "lighting_fixtures",
        "total_count": 1,
        "returned_count": 1,
        "truncated": False,
        "types": [{
            "symbol_id": 10,
            "family_id": 9,
            "category": "Lighting Fixtures",
            "family_name": "Downlight",
            "type_name": "18 W",
            "family_placement_type": "WorkPlaneBased",
            "host_requirement": "work_plane_or_face",
            "is_active": True,
        }],
        "provider_egress_performed": False,
        "document_mutation_performed": False,
    }), encoding="utf-8")
    result = load_family_type_query(path, allowed_root=root)
    assert result.total_count == 1
    assert result.types[0]["family_placement_type"] == "WorkPlaneBased"
    assert result.types[0]["host_requirement"] == "work_plane_or_face"


def test_family_type_loader_rejects_unknown_host_requirement(tmp_path: Path) -> None:
    root = tmp_path / "bridge"
    path = root / "family-types" / "unsafe.json"
    path.parent.mkdir(parents=True)
    path.write_text(json.dumps({
        "schema_version": 1, "request_id": "request", "document_title": "Sample",
        "category_key": "electrical_equipment", "total_count": 1,
        "returned_count": 1, "truncated": False,
        "types": [{"symbol_id": 1, "family_id": 2, "category": "Equipment",
                   "family_name": "Panel", "type_name": "A",
                   "family_placement_type": "Unknown", "host_requirement": "anything",
                   "is_active": False}],
        "provider_egress_performed": False, "document_mutation_performed": False,
    }), encoding="utf-8")
    with pytest.raises(RevitIntegrationError, match="placement metadata"):
        load_family_type_query(path, allowed_root=root)
