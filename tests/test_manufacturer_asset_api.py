"""API tests for reviewed manufacturer asset discovery and collection."""

from __future__ import annotations

import json
from types import SimpleNamespace

import pytest
from fastapi.testclient import TestClient

import api.routes as api_routes
from agent.manufacturer_provider import ManufacturerProviderAdapter
from api.server import create_app
from core.manufacturer_asset_collection import (
    ManufacturerAssetCandidate,
    ManufacturerAssetKind,
    create_asset_search_request,
)
from tests.test_api import TOKEN, authorization, create_project


def test_asset_discovery_and_separate_download_approval_api(
    tmp_path, monkeypatch: pytest.MonkeyPatch,
) -> None:
    root = create_project(tmp_path)
    request = create_asset_search_request(
        request_id="asset-search:api", project_id="project:api",
        manufacturer_name="Schneider Electric", part_number="C25B32TM250",
        product_page_url="https://www.se.com/product/C25B32TM250",
        product_evidence_sha256="a" * 64, official_domains=("www.se.com",),
        requested_kinds=(ManufacturerAssetKind.DATA_SHEET, ManufacturerAssetKind.CAD_3D),
        approved_by="engineer:gamal", approved_on="2026-08-26", egress_approved=True,
    )
    candidate = ManufacturerAssetCandidate(
        "asset-candidate:api-step", ManufacturerAssetKind.CAD_3D, "C25B32TM250",
        "Official STEP", "https://www.se.com/files/C25B32TM250.stp", "www.se.com",
        "model/step", "https://www.se.com/product/C25B32TM250", "2026-08-26",
    )
    output = json.dumps({
        "candidates": [candidate.to_dict()], "automatic_selection_performed": False,
        "automatic_download_performed": False, "catalog_registration_performed": False,
        "drawing_mutation_performed": False,
    })
    provider = ManufacturerProviderAdapter(
        "official-asset-search", lambda _request: "{}", asset_search=lambda _request: output
    )
    app = create_app(token=TOKEN, project_root=root, manufacturer_provider=provider)
    with TestClient(app, base_url="http://127.0.0.1") as client:
        discovery = client.post(
            "/v1/manufacturers/assets/discover", headers=authorization(),
            json={"request": request.to_dict(), "result_id": "asset-result:api"},
        )
        assert discovery.status_code == 201
        body = discovery.json()
        assert body["result"]["candidates"][0]["kind"] == "cad_3d"
        assert body["result"]["automatic_download_performed"] is False

        rejected = client.post(
            "/v1/manufacturers/assets/collect", headers=authorization(),
            json={
                "request": body["request"], "result": body["result"],
                "approval_id": "asset-approval:api",
                "selected_candidate_ids": [candidate.candidate_id],
                "approved_by": "engineer:gamal", "approved_on": "2026-08-26",
                "maximum_bytes_per_asset": 4096, "download_approved": False,
            },
        )
        assert rejected.status_code == 400
        assert "explicit approval" in rejected.json()["detail"]

        fake = SimpleNamespace(
            collection_id="collection:asset-approval:api", part_number="C25B32TM250",
            records=(object(),),
            to_dict=lambda: {
                "collection_id": "collection:asset-approval:api", "complete": True,
                "retrieval_performed": True, "quarantine_write_performed": True,
                "catalog_registration_performed": False, "drawing_mutation_performed": False,
            },
        )
        monkeypatch.setattr(api_routes, "collect_approved_assets", lambda *args, **kwargs: fake)
        accepted = client.post(
            "/v1/manufacturers/assets/collect", headers=authorization(),
            json={
                "request": body["request"], "result": body["result"],
                "approval_id": "asset-approval:api",
                "selected_candidate_ids": [candidate.candidate_id],
                "approved_by": "engineer:gamal", "approved_on": "2026-08-26",
                "maximum_bytes_per_asset": 4096, "download_approved": True,
            },
        )
    assert accepted.status_code == 201
    assert accepted.json()["collection"]["retrieval_performed"] is True
