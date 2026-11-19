"""Security and integration tests for the protected localhost API."""

from __future__ import annotations

import json
import hashlib
import shutil
import socket
import threading
import time
from datetime import UTC, datetime
from collections import deque
from pathlib import Path
from types import SimpleNamespace

import httpx
import pytest
import uvicorn
import api.routes as api_routes
from fastapi.testclient import TestClient

from agent.app import AppStatus
from agent.exceptions import ApiConfigurationError
from agent.provider import ProviderAdapter, ProviderRequest
from agent.copilot_provider import CopilotProviderAdapter, CopilotProviderRequest
from core.electrical_drafting import (
    ProposedDevice,
    ProposedCircuit,
    ProposedLayer,
    ProposedRoute,
    ProposedTerminal,
    create_drafting_proposal,
    create_source_model_binding,
)
from integrations.electrical_symbols import ElectricalSymbolCatalog
from api.server import ApiServerConfig, create_app, main, run_server
from integrations.autocad_context import ContextLimits
from integrations.autocad import CircuitFragmentArtifact, DraftingRecoveryEvidence
from tests.test_panel_sld_extraction import _raw_context as panel_sld_context
from tests.test_compliance_evidence import (
    _clause as compliance_clause,
    _fact as compliance_fact,
    _profile as compliance_profile,
    _rule as compliance_rule,
)
from tests.test_manufacturer_mapping import (
    CATALOG as MANUFACTURER_TEST_CATALOG,
    _cad_bundle as manufacturer_cad_bundle,
    _model as manufacturer_model,
    _proposal as manufacturer_symbol_proposal,
    _quarantine as manufacturer_quarantine,
)
from core.electrical_model import PrivacyClassification
from integrations.manufacturer_mapping import (
    ManufacturerTargetKind,
    create_model_binding as create_manufacturer_model_binding,
    create_symbol_candidate as create_manufacturer_symbol_candidate,
    register_manufacturer_symbol_candidate,
)
from agent.manufacturer_provider import ManufacturerProviderAdapter
from tests.test_manufacturer_discovery import (
    _candidate as manufacturer_discovery_candidate,
    _request as manufacturer_discovery_request,
)

TOKEN = "0123456789abcdefABCDEFGHIJKLMNOPQRSTUVWXYZ-_secure"


def create_project(tmp_path: Path) -> Path:
    data = tmp_path / "data"
    data.mkdir()
    (data / "settings.json").write_text(
        json.dumps({"schema_version": 1, "log_level": "INFO"}), encoding="utf-8"
    )
    (data / "agent_state.json").write_text(
        json.dumps(
            {
                "schema_version": 1,
                "application_version": "0.18.0",
                "status": "stopped",
                "start_count": 0,
                "last_started_at": None,
                "last_stopped_at": None,
            }
        ),
        encoding="utf-8",
    )
    (data / "drafting_runs.json").write_text(
        json.dumps({"schema_version": 1, "runs": []}), encoding="utf-8"
    )
    catalog = tmp_path / "plugins" / "electrical_symbols"
    catalog.mkdir(parents=True)
    shutil.copyfile(
        Path(__file__).resolve().parents[1]
        / "plugins"
        / "electrical_symbols"
        / "catalog.json",
        catalog / "catalog.json",
    )
    return tmp_path


def create_context_evidence(root: Path) -> tuple[str, str]:
    context_id = "a" * 32
    value = {
        "schema_version": 1,
        "context_id": context_id,
        "captured_at": "2026-08-06T12:00:00Z",
        "source_mode": "selection",
        "privacy": {
            "classification": "project_confidential",
            "provider_egress_allowed": False,
        },
        "document": {
            "document_id": "12345678-1234-1234-1234-1234567890ab",
            "identity_source": "fingerprint_guid",
            "name": "sample.dwg",
            "drawing_units": 4,
            "layout": "Model",
            "database_modified": 0,
        },
        "limits": ContextLimits().to_dict(),
        "truncated": False,
        "truncation_reasons": [],
        "layers": [
            {
                "name": "E-WIRE",
                "color_index": 3,
                "linetype": "Continuous",
                "is_off": False,
                "is_frozen": False,
                "is_locked": False,
            }
        ],
        "entities": [
            {
                "object_id": "A1",
                "entity_type": "LINE",
                "original_type": "Line",
                "layer": "E-WIRE",
                "layout": "Model",
                "visible": True,
                "points": [[0, 0, 0], [1000, 0, 0]],
                "radius": None,
                "rotation_degrees": None,
                "scale": None,
                "block_name": None,
                "text": None,
                "attributes": [],
            }
        ],
    }
    content = json.dumps(value, separators=(",", ":")).encode("utf-8")
    inbox = root / "workspace" / ".ai-engineer" / "autocad" / "context" / "inbox"
    inbox.mkdir(parents=True)
    (inbox / f"{context_id}.json").write_bytes(content)
    return context_id, hashlib.sha256(content).hexdigest()


def create_calculation_context_evidence(root: Path) -> tuple[str, str]:
    context_id, _ = create_context_evidence(root)
    path = (
        root
        / "workspace"
        / ".ai-engineer"
        / "autocad"
        / "context"
        / "inbox"
        / f"{context_id}.json"
    )
    value = json.loads(path.read_text(encoding="utf-8"))
    value["layers"].append(
        {
            "name": "E-POWR-DEVC",
            "color_index": 2,
            "linetype": "Continuous",
            "is_off": False,
            "is_frozen": False,
            "is_locked": False,
        }
    )
    value["entities"].append(
        {
            "object_id": "A2",
            "entity_type": "INSERT",
            "original_type": "BlockReference",
            "layer": "E-POWR-DEVC",
            "layout": "Model",
            "visible": True,
            "points": [[0, 0, 0]],
            "radius": None,
            "rotation_degrees": 0,
            "scale": [1, 1, 1],
            "block_name": "AIENG_E_SOCKET_SINGLE_V1",
            "text": None,
            "attributes": [],
        }
    )
    content = json.dumps(value, separators=(",", ":")).encode("utf-8")
    path.write_bytes(content)
    return context_id, hashlib.sha256(content).hexdigest()


def create_panel_sld_context_evidence(
    root: Path, *, incomplete: bool = False
) -> tuple[str, str]:
    value = panel_sld_context(omit_way_attribute="PHASE" if incomplete else None)
    content = json.dumps(value, separators=(",", ":")).encode("utf-8")
    context_id = value["context_id"]
    inbox = root / "workspace" / ".ai-engineer" / "autocad" / "context" / "inbox"
    inbox.mkdir(parents=True)
    (inbox / f"{context_id}.json").write_bytes(content)
    return str(context_id), hashlib.sha256(content).hexdigest()


@pytest.fixture
def api_client(tmp_path: Path):
    app = create_app(token=TOKEN, project_root=create_project(tmp_path))
    with TestClient(app, base_url="http://127.0.0.1") as client:
        yield client, app


def authorization(token: str = TOKEN) -> dict[str, str]:
    return {"Authorization": f"Bearer {token}"}


def test_health_is_minimal_and_only_unauthenticated_route(api_client: tuple) -> None:
    client, _ = api_client

    health = client.get("/health")
    protected = client.get("/v1/status")

    assert health.status_code == 200
    assert health.json() == {"status": "ok", "version": "0.18.0"}
    assert protected.status_code == 401
    assert protected.headers["www-authenticate"] == "Bearer"


@pytest.mark.parametrize(
    "header",
    [None, "Basic abc", "Bearer", "Bearer wrong-token-value-0123456789abcdef"],
)
def test_authentication_rejects_missing_or_invalid_credentials(
    api_client: tuple, header: str | None
) -> None:
    client, _ = api_client
    headers = {} if header is None else {"Authorization": header}

    response = client.get("/v1/commands", headers=headers)

    assert response.status_code == 401
    assert response.json() == {"detail": "Authentication required."}


def test_docs_and_schema_are_disabled(api_client: tuple) -> None:
    client, _ = api_client

    assert client.get("/docs").status_code == 401
    assert client.get("/docs", headers=authorization()).status_code == 404
    assert client.get("/redoc", headers=authorization()).status_code == 404
    assert client.get("/openapi.json", headers=authorization()).status_code == 404


def test_invalid_host_header_is_rejected(api_client: tuple) -> None:
    client, _ = api_client

    response = client.get("/health", headers={"Host": "attacker.example"})

    assert response.status_code == 400


def test_security_headers_disable_caching_and_embedding(api_client: tuple) -> None:
    client, _ = api_client

    response = client.get("/health")

    assert response.headers["cache-control"] == "no-store"
    assert response.headers["x-content-type-options"] == "nosniff"
    assert response.headers["x-frame-options"] == "DENY"
    assert response.headers["referrer-policy"] == "no-referrer"


def test_status_and_command_catalog_require_authentication(api_client: tuple) -> None:
    client, _ = api_client

    status = client.get("/v1/status", headers=authorization())
    commands = client.get("/v1/commands", headers=authorization())

    assert status.status_code == 200
    assert status.json()["status"] == "running"
    catalog = commands.json()["commands"]
    assert status.json()["registered_commands"] == len(catalog)
    assert len({item["name"] for item in catalog}) == len(catalog)
    relocation = next(item for item in catalog if item["name"] == "autocad.preview_component_moves")
    assert relocation["risk"] == "high"
    drawing_summary = next(item for item in catalog if item["name"] == "autocad.summarize_drawing_index_for_ai")
    assert drawing_summary["risk"] == "high"
    assert all("handler" not in item for item in catalog)


def test_component_move_preview_retains_durable_approval_gate(api_client: tuple) -> None:
    client, _ = api_client
    submitted = client.post(
        "/v1/tasks", headers=authorization(),
        json={"command_name": "autocad.preview_component_moves", "payload": {
            "path": "missing.dwg", "expected_source_sha256": "c" * 64,
            "context_id": "a" * 32, "evidence_sha256": "b" * 64,
            "moves": [{"object_id": "A1", "target_mm": [100, 200, 0]}],
        }},
    )
    assert submitted.status_code == 201
    task_id = submitted.json()["task_id"]
    awaiting = client.post(f"/v1/tasks/{task_id}/run", headers=authorization(), json={"approval_id": None})
    assert awaiting.json()["status"] == "awaiting_approval"
    approval_id = awaiting.json()["approval_id"]
    denied = client.post(f"/v1/approvals/{approval_id}/decision", headers=authorization(), json={"approved": False})
    assert denied.json()["status"] == "denied"
    cancelled = client.post(f"/v1/tasks/{task_id}/run", headers=authorization(), json={"approval_id": approval_id})
    assert cancelled.json()["status"] == "cancelled"


def test_developer_status_is_safe_when_provider_is_not_configured(
    api_client: tuple,
) -> None:
    client, _ = api_client

    status = client.get("/v1/developer/status", headers=authorization())
    create = client.post(
        "/v1/developer/runs",
        headers=authorization(),
        json={"goal": "Inspect the project safely.", "maximum_actions": 1},
    )

    assert status.status_code == 200
    assert status.json() == {"configured": False, "provider_name": None}
    assert create.status_code == 503


def test_takeoff_api_reimports_exact_context_and_never_mutates_drawing(
    tmp_path: Path,
) -> None:
    root = create_project(tmp_path)
    context_id, digest = create_context_evidence(root)
    app = create_app(token=TOKEN, project_root=root)
    request = {
        "context_id": context_id,
        "context_evidence_sha256": digest,
        "policy": {
            "calculation_version": "m19.takeoff.v1",
            "vertical_allowance_per_route_meters": 0.5,
            "termination_allowance_per_end_meters": 0.25,
            "waste_percent": 10.0,
            "route_overrides": [],
        },
    }
    with TestClient(app, base_url="http://127.0.0.1") as client:
        unauthenticated = client.post("/v1/takeoff/reports", json=request)
        created = client.post(
            "/v1/takeoff/reports", headers=authorization(), json=request
        )
        reviewed_hash = created.json()["report"]["report_sha256"]
        exported = client.post(
            "/v1/takeoff/exports/csv",
            headers=authorization(),
            json={**request, "reviewed_report_sha256": reviewed_hash},
        )
        unreviewed_export = client.post(
            "/v1/takeoff/exports/csv",
            headers=authorization(),
            json={**request, "reviewed_report_sha256": "f" * 64},
        )
        changed_policy_export = client.post(
            "/v1/takeoff/exports/csv",
            headers=authorization(),
            json={
                **request,
                "reviewed_report_sha256": reviewed_hash,
                "policy": {**request["policy"], "waste_percent": 11.0},
            },
        )
        changed_evidence = client.post(
            "/v1/takeoff/reports",
            headers=authorization(),
            json={**request, "context_evidence_sha256": "f" * 64},
        )
        unknown_override = client.post(
            "/v1/takeoff/reports",
            headers=authorization(),
            json={
                **request,
                "policy": {
                    **request["policy"],
                    "route_overrides": [
                        {
                            "route_id": "route:missing",
                            "final_total_meters": 3.0,
                            "reason": "Reviewed field condition.",
                            "reviewed_by": "engineer:gamal",
                        }
                    ],
                },
            },
        )

    assert unauthenticated.status_code == 401
    assert created.status_code == 201
    report = created.json()["report"]
    assert report["context_id"] == context_id
    assert report["context_evidence_sha256"] == digest
    assert report["device_quantities"] == []
    assert report["route_quantities"][0]["route_id"] == "route:1"
    assert report["route_quantities"][0]["geometric_length_meters"] == 1.0
    assert report["route_quantities"][0]["final_total_meters"] == 2.2
    assert report["drawing_mutation_performed"] is False
    assert report["is_complete"] is True
    assert exported.status_code == 201
    csv_export = exported.json()["export"]
    assert csv_export["report_sha256"] == report["report_sha256"]
    assert csv_export["row_count"] == 1
    assert csv_export["content"].startswith("\ufeffrecord_type,")
    assert csv_export["drawing_mutation_performed"] is False
    assert csv_export["file_write_performed"] is False
    assert unreviewed_export.status_code == 400
    assert changed_policy_export.status_code == 400
    assert changed_evidence.status_code == 400
    assert unknown_override.status_code == 400


def test_low_voltage_api_reimports_sources_calculates_and_rejects_unsafe_input(
    tmp_path: Path,
) -> None:
    root = create_project(tmp_path)
    context_id, digest = create_calculation_context_evidence(root)
    app = create_app(token=TOKEN, project_root=root)
    calculation = {
        "calculation_id": "calculation:api_test",
        "calculation_version": "m20.lv_current_voltage_drop.v1",
        "phase_system": "single_phase",
        "load_output_power_watts": 1000.0,
        "demand_factor": 1.0,
        "nominal_voltage_volts": 230.0,
        "power_factor": 0.9,
        "efficiency": 0.95,
        "conductor_ac_resistance_ohm_per_km": 7.41,
        "conductor_reactance_ohm_per_km": 0.08,
        "load_object_ids": ["device:2"],
        "route_ids": ["route:1"],
        "reviewed_by": "engineer:gamal",
        "assumptions": ["Balanced sinusoidal steady-state load with lagging power factor."],
    }
    request = {
        "context_id": context_id,
        "context_evidence_sha256": digest,
        "calculation": calculation,
    }
    with TestClient(app, base_url="http://127.0.0.1") as client:
        unauthenticated = client.post("/v1/calculations/low-voltage", json=request)
        created = client.post(
            "/v1/calculations/low-voltage", headers=authorization(), json=request
        )
        unknown_load = client.post(
            "/v1/calculations/low-voltage",
            headers=authorization(),
            json={
                **request,
                "calculation": {
                    **calculation,
                    "load_object_ids": ["device:missing"],
                },
            },
        )
        coerced_number = client.post(
            "/v1/calculations/low-voltage",
            headers=authorization(),
            json={
                **request,
                "calculation": {
                    **calculation,
                    "load_output_power_watts": "1000.0",
                },
            },
        )

    assert unauthenticated.status_code == 401
    assert created.status_code == 201
    result = created.json()["result"]
    assert result["context_evidence_sha256"] == digest
    assert result["route_length_meters"] == 1.0
    assert result["calculated_current_amperes"] == 5.085176709891
    assert result["voltage_drop_volts"] == 0.068180739299
    assert result["voltage_drop_percent"] == 0.029643799695
    assert result["load_sources"][0]["object_id"] == "A2"
    assert result["route_sources"][0]["object_id"] == "A1"
    assert result["compliance_evaluation_performed"] is False
    assert result["drawing_mutation_performed"] is False
    assert len(result["result_sha256"]) == 64
    assert unknown_load.status_code == 400
    assert coerced_number.status_code == 422


def test_load_aggregation_api_is_authenticated_traceable_and_strict(tmp_path: Path) -> None:
    root = create_project(tmp_path)
    context_id, digest = create_calculation_context_evidence(root)
    app = create_app(token=TOKEN, project_root=root)
    rule_set = {
        "rule_set_id": "ruleset:api_test",
        "rule_set_version": "m20.load_aggregation.v1",
        "allocations": [{
            "load_object_id": "device:2",
            "connected_output_power_watts": 1200.0,
            "demand_factor": 0.8,
            "phase": "l1",
        }],
        "diversity_groups": [{
            "group_id": "group:general",
            "member_load_object_ids": ["device:2"],
            "diversity_factor": 0.75,
        }],
        "reviewed_by": "engineer:gamal",
        "assumptions": ["Reviewed project load schedule."],
    }
    request = {
        "context_id": context_id,
        "context_evidence_sha256": digest,
        "rule_set": rule_set,
    }
    with TestClient(app, base_url="http://127.0.0.1") as client:
        unauthenticated = client.post("/v1/calculations/load-aggregation", json=request)
        created = client.post(
            "/v1/calculations/load-aggregation", headers=authorization(), json=request
        )
        unknown = client.post(
            "/v1/calculations/load-aggregation",
            headers=authorization(),
            json={
                **request,
                "rule_set": {
                    **rule_set,
                    "allocations": [{
                        **rule_set["allocations"][0],
                        "load_object_id": "device:missing",
                    }],
                    "diversity_groups": [{
                        **rule_set["diversity_groups"][0],
                        "member_load_object_ids": ["device:missing"],
                    }],
                },
            },
        )
        coerced = client.post(
            "/v1/calculations/load-aggregation",
            headers=authorization(),
            json={
                **request,
                "rule_set": {
                    **rule_set,
                    "allocations": [{
                        **rule_set["allocations"][0],
                        "connected_output_power_watts": "1200.0",
                    }],
                },
            },
        )

    assert unauthenticated.status_code == 401
    assert created.status_code == 201
    result = created.json()["result"]
    assert result["context_evidence_sha256"] == digest
    assert result["connected_output_power_watts"] == 1200.0
    assert result["demand_before_diversity_watts"] == 960.0
    assert result["demand_after_diversity_watts"] == 720.0
    assert [item["demand_output_power_watts"] for item in result["phase_results"]] == [720.0, 0.0, 0.0]
    assert result["load_sources"][0]["object_id"] == "A2"
    assert result["compliance_evaluation_performed"] is False
    assert result["drawing_mutation_performed"] is False
    assert len(result["result_sha256"]) == 64
    assert unknown.status_code == 400
    assert coerced.status_code == 422


def test_panel_sld_extraction_api_is_authenticated_read_only_and_explicit(
    tmp_path: Path,
) -> None:
    root = create_project(tmp_path)
    context_id, digest = create_panel_sld_context_evidence(root)
    app = create_app(token=TOKEN, project_root=root)
    request = {
        "context_id": context_id,
        "context_evidence_sha256": digest,
        "reviewed_by": "engineer:gamal",
        "assumptions": ["Supported M21 marker profile reviewed."],
    }
    with TestClient(app, base_url="http://127.0.0.1") as client:
        unauthenticated = client.post("/v1/panels/extract-schedule", json=request)
        created = client.post(
            "/v1/panels/extract-schedule", headers=authorization(), json=request
        )
        coerced = client.post(
            "/v1/panels/extract-schedule",
            headers=authorization(),
            json={**request, "reviewed_by": 7},
        )

    assert unauthenticated.status_code == 401
    assert created.status_code == 201
    body = created.json()
    extraction = body["extraction"]
    schedule = body["schedule"]
    assert extraction["complete"] is True
    assert extraction["supported_marker_count"] == 10
    assert extraction["provider_egress_performed"] is False
    assert extraction["drawing_mutation_performed"] is False
    assert schedule["outgoing_way_count"] == 2
    assert schedule["assigned_load_count"] == 1
    assert schedule["existing_schedule_extraction_performed"] is False
    assert schedule["drawing_mutation_performed"] is False
    assert coerced.status_code == 422


def test_panel_sld_extraction_api_returns_unknown_without_partial_model(
    tmp_path: Path,
) -> None:
    root = create_project(tmp_path)
    context_id, digest = create_panel_sld_context_evidence(root, incomplete=True)
    app = create_app(token=TOKEN, project_root=root)
    with TestClient(app, base_url="http://127.0.0.1") as client:
        response = client.post(
            "/v1/panels/extract-schedule",
            headers=authorization(),
            json={
                "context_id": context_id,
                "context_evidence_sha256": digest,
                "reviewed_by": "engineer:gamal",
                "assumptions": ["Marker profile reviewed."],
            },
        )
    assert response.status_code == 201
    extraction = response.json()["extraction"]
    assert extraction["complete"] is False
    assert extraction["semantic_model"] is None
    assert extraction["contract"] is None
    assert any(item["outcome"] == "unknown" for item in extraction["findings"])
    assert response.json()["schedule"] is None


def test_compliance_api_is_authenticated_hash_bound_and_read_only(
    api_client: tuple,
) -> None:
    client, _ = api_client
    profile = compliance_profile()
    clause = compliance_clause(profile)
    rule = compliance_rule(clause)
    request = {
        "evaluation_id": "compliance:api-test",
        "profile": profile.to_dict(),
        "clauses": [clause.to_dict()],
        "rules": [rule.to_dict()],
        "facts": [compliance_fact().to_dict()],
        "evaluated_by": "engineer:gamal",
        "evaluated_on": "2026-08-12",
        "assumptions": ["Authenticated local read-only API fixture."],
    }
    unauthenticated = client.post("/v1/compliance/evaluate", json=request)
    accepted = client.post(
        "/v1/compliance/evaluate", headers=authorization(), json=request
    )

    assert unauthenticated.status_code == 401
    assert accepted.status_code == 201
    response = accepted.json()
    assert response["profile"]["profile_sha256"] == profile.profile_sha256
    assert response["clauses"][0]["clause_locator"] == "reviewed-test-clause-1"
    assert response["clauses"][0]["evidence_sha256"] == clause.evidence_sha256
    assert response["result"]["findings"][0]["outcome"] == "pass"
    assert response["result"]["complete"] is True
    assert response["result"]["provider_retrieval_performed"] is False
    assert response["result"]["regulatory_approval_performed"] is False
    assert response["result"]["drawing_mutation_performed"] is False


def test_compliance_api_displays_unknown_and_rejects_tampered_profile(
    api_client: tuple,
) -> None:
    client, _ = api_client
    profile = compliance_profile()
    clause = compliance_clause(profile)
    rule = compliance_rule(clause)
    request = {
        "evaluation_id": "compliance:api-unknown",
        "profile": profile.to_dict(),
        "clauses": [clause.to_dict()],
        "rules": [rule.to_dict()],
        "facts": [],
        "evaluated_by": "engineer:gamal",
        "evaluated_on": "2026-08-12",
        "assumptions": ["Missing facts must remain visible."],
    }
    unknown = client.post(
        "/v1/compliance/evaluate", headers=authorization(), json=request
    )
    tampered = {**request, "profile": {**request["profile"], "jurisdiction": "Changed"}}
    rejected = client.post(
        "/v1/compliance/evaluate", headers=authorization(), json=tampered
    )

    assert unknown.status_code == 201
    assert unknown.json()["result"]["findings"][0]["outcome"] == "unknown"
    assert unknown.json()["result"]["complete"] is False
    assert unknown.json()["result"]["all_requirements_satisfied"] is False
    assert rejected.status_code == 400
    assert "SHA-256" in rejected.json()["detail"]


def _manufacturer_review_request(tmp_path: Path) -> dict[str, object]:
    bundle = manufacturer_cad_bundle()
    retrieval, quarantine = manufacturer_quarantine(tmp_path)
    candidate = create_manufacturer_symbol_candidate(
        bundle, retrieval, quarantine,
        candidate_id="manufacturer-symbol:api",
        artifact_id="artifact:breaker-cad",
        symbol_proposal=manufacturer_symbol_proposal(),
        reviewed_by="engineer:gamal", reviewed_on="2026-08-12",
        assumptions=("Reviewed API fixture geometry.",),
    )
    binding = create_manufacturer_model_binding(
        manufacturer_model(), bundle, binding_id="manufacturer-binding:api",
        target_kind=ManufacturerTargetKind.DEVICE, target_id="device:breaker-1",
        claim_ids=("claim:rated-current",), captured_at="2026-08-12T12:00:00Z",
        privacy=PrivacyClassification.INTERNAL, reviewed_by="engineer:gamal",
        reviewed_on="2026-08-12", assumptions=("Reviewed API fixture applicability.",),
    )
    catalog = tmp_path / "manufacturer-catalog.json"
    catalog.write_bytes(MANUFACTURER_TEST_CATALOG.read_bytes())
    registration = register_manufacturer_symbol_candidate(
        candidate, catalog, tmp_path / "manufacturer-backups",
        tmp_path / "manufacturer-provenance", confirmed=True,
        registered_by="engineer:gamal",
        clock=lambda: datetime(2026, 8, 12, 13, 0, tzinfo=UTC),
    )
    return {
        "review_id": "manufacturer-review:api",
        "scope": "catalog_registration",
        "bundle": bundle.to_dict(),
        "retrieval_request": retrieval.to_dict(),
        "quarantine": quarantine.to_dict(),
        "model_binding": binding.to_dict(),
        "symbol_candidate": candidate.to_dict(),
        "catalog_registration": registration.to_dict(),
        "require_model_binding": True,
        "reviewed_by": "engineer:gamal",
        "reviewed_on": "2026-08-12",
        "assumptions": ["Authenticated read-only manufacturer review fixture."],
    }


def test_manufacturer_review_api_is_authenticated_complete_and_read_only(
    tmp_path: Path,
) -> None:
    project_directory = tmp_path / "project"
    project_directory.mkdir()
    root = create_project(project_directory)
    app = create_app(token=TOKEN, project_root=root)
    request = _manufacturer_review_request(tmp_path / "evidence")
    with TestClient(app, base_url="http://127.0.0.1") as client:
        unauthenticated = client.post("/v1/manufacturers/review", json=request)
        accepted = client.post(
            "/v1/manufacturers/review", headers=authorization(), json=request
        )
    assert unauthenticated.status_code == 401
    assert accepted.status_code == 201
    body = accepted.json()
    assert body["bundle"]["product"]["part_number"] == "ABC-16-6K"
    assert body["quarantine"]["quarantine_write_performed"] is True
    assert body["symbol_candidate"]["catalog_registration_performed"] is False
    assert body["catalog_registration"]["catalog_registration_performed"] is True
    assert body["result"]["complete"] is True
    assert {item["outcome"] for item in body["result"]["findings"]} == {"pass"}
    assert body["result"]["provider_egress_performed"] is False
    assert body["result"]["retrieval_performed_by_review"] is False
    assert body["result"]["catalog_registration_performed_by_review"] is False
    assert body["result"]["drawing_mutation_performed"] is False


def test_manufacturer_review_api_returns_unknown_and_rejects_tampering(
    api_client: tuple,
) -> None:
    client, _ = api_client
    bundle = manufacturer_cad_bundle()
    request = {
        "review_id": "manufacturer-review:unknown",
        "scope": "symbol_candidate",
        "bundle": bundle.to_dict(),
        "retrieval_request": None,
        "quarantine": None,
        "model_binding": None,
        "symbol_candidate": None,
        "catalog_registration": None,
        "require_model_binding": True,
        "reviewed_by": "engineer:gamal",
        "reviewed_on": "2026-08-12",
        "assumptions": ["Missing evidence remains visible."],
    }
    unknown = client.post(
        "/v1/manufacturers/review", headers=authorization(), json=request
    )
    tampered = {
        **request,
        "bundle": {**request["bundle"], "project_id": "project:changed"},
    }
    rejected = client.post(
        "/v1/manufacturers/review", headers=authorization(), json=tampered
    )
    assert unknown.status_code == 201
    assert unknown.json()["result"]["complete"] is False
    assert "unknown" in {
        item["outcome"] for item in unknown.json()["result"]["findings"]
    }
    assert rejected.status_code == 400
    assert "SHA-256" in rejected.json()["detail"]


def test_manufacturer_discovery_api_requires_auth_and_configured_provider(
    tmp_path: Path,
) -> None:
    project_directory = tmp_path / "project"
    project_directory.mkdir()
    root = create_project(project_directory)
    discovery = manufacturer_discovery_request()
    candidate = manufacturer_discovery_candidate()
    provider_output = json.dumps(
        {
            "candidates": [candidate.to_dict()],
            "automatic_selection_performed": False,
            "automatic_download_performed": False,
            "catalog_registration_performed": False,
            "drawing_mutation_performed": False,
        }
    )
    payload = {
        "request": discovery.to_dict(),
        "discovery_id": "manufacturer-discovery:api",
        "reviewed_by": "engineer:gamal",
        "reviewed_on": "2026-08-12",
        "assumptions": ["Authenticated provider fixture."],
    }
    configured = create_app(
        token=TOKEN,
        project_root=root,
        manufacturer_provider=ManufacturerProviderAdapter(
            "official-search-fixture", lambda request: provider_output
        ),
    )
    with TestClient(configured, base_url="http://127.0.0.1") as client:
        unauthenticated = client.post("/v1/manufacturers/discover", json=payload)
        accepted = client.post(
            "/v1/manufacturers/discover", headers=authorization(), json=payload
        )
        selection_payload = {
            "result": accepted.json()["result"],
            "selection_id": "manufacturer-selection:api",
            "candidate_id": candidate.candidate_id,
            "selected_by": "engineer:gamal",
            "selected_on": "2026-08-12",
            "rationale": "Continue this candidate to separate datasheet review.",
        }
        selection_unauthenticated = client.post(
            "/v1/manufacturers/discover/select", json=selection_payload
        )
        selection_accepted = client.post(
            "/v1/manufacturers/discover/select",
            headers=authorization(), json=selection_payload,
        )
    assert unauthenticated.status_code == 401
    assert accepted.status_code == 201
    result = accepted.json()["result"]
    assert result["candidates"][0]["part_number"] == "004280"
    assert result["comparisons"][0]["mandatory_requirements_satisfied"] is True
    assert {item["outcome"] for item in result["comparisons"][0]["findings"]} == {"pass"}
    assert result["provider_egress_performed"] is True
    assert result["automatic_selection_performed"] is False
    assert result["automatic_download_performed"] is False
    assert result["catalog_registration_performed"] is False
    assert result["drawing_mutation_performed"] is False
    assert selection_unauthenticated.status_code == 401
    assert selection_accepted.status_code == 201
    selection = selection_accepted.json()["selection"]
    assert selection["discovery_result_sha256"] == result["result_sha256"]
    assert selection["candidate_id"] == candidate.candidate_id
    assert selection["product_approval_performed"] is False
    assert selection["automatic_download_performed"] is False
    assert selection["catalog_registration_performed"] is False
    assert selection["drawing_mutation_performed"] is False

    unconfigured = create_app(token=TOKEN, project_root=root)
    with TestClient(unconfigured, base_url="http://127.0.0.1") as client:
        unavailable = client.post(
            "/v1/manufacturers/discover", headers=authorization(), json=payload
        )
    assert unavailable.status_code == 503
    assert unavailable.json()["detail"] == "Service is not initialized."


def test_free_manufacturer_verification_api_needs_no_ai_provider(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch,
) -> None:
    root = create_project(tmp_path)
    discovery = manufacturer_discovery_request()
    candidate = manufacturer_discovery_candidate()
    monkeypatch.setattr(
        api_routes, "verify_official_candidate_draft",
        lambda draft, request: (candidate,),
    )
    payload = {
        "request": discovery.to_dict(),
        "candidate_draft": {"candidates": [{"fixture": "locally verified"}]},
        "discovery_id": "manufacturer-discovery:free-api",
        "reviewed_by": "engineer:gamal",
        "reviewed_on": "2026-08-12",
        "assumptions": ["User-supplied candidate draft fixture."],
    }
    app = create_app(token=TOKEN, project_root=root)
    with TestClient(app, base_url="http://127.0.0.1") as client:
        unauthenticated = client.post("/v1/manufacturers/discover/manual", json=payload)
        accepted = client.post(
            "/v1/manufacturers/discover/manual", headers=authorization(), json=payload
        )
    assert unauthenticated.status_code == 401
    assert accepted.status_code == 201
    result = accepted.json()["result"]
    assert result["provider_name"] == "manual-official-verification"
    assert result["provider_egress_performed"] is False
    assert result["automatic_download_performed"] is False
    assert result["drawing_mutation_performed"] is False


def test_saved_html_manufacturer_verification_uses_workspace_service(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch,
) -> None:
    root = create_project(tmp_path)
    discovery = manufacturer_discovery_request()
    candidate = manufacturer_discovery_candidate()
    seen: dict[str, object] = {}

    def verify(draft, request, *, evidence_read):
        seen["bytes"] = evidence_read("evidence/product.html")
        return (candidate,)

    evidence = root / "evidence"
    evidence.mkdir()
    (evidence / "product.html").write_text("<html>official</html>", encoding="utf-8")
    monkeypatch.setattr(api_routes, "verify_saved_candidate_draft", verify)
    payload = {
        "request": discovery.to_dict(),
        "candidate_draft": {"candidates": [{"evidence_path": "evidence/product.html"}]},
        "discovery_id": "manufacturer-discovery:saved-api",
        "reviewed_by": "engineer:gamal", "reviewed_on": "2026-08-12",
        "assumptions": ["Engineer-saved official HTML fixture."],
    }
    app = create_app(
        token=TOKEN,
        project_root=root,
        workspace_roots=(root,),
        backup_root=tmp_path.parent / f"{tmp_path.name}-external-backup",
    )
    with TestClient(app, base_url="http://127.0.0.1") as client:
        response = client.post(
            "/v1/manufacturers/discover/manual", headers=authorization(), json=payload
        )
    assert response.status_code == 201
    assert seen["bytes"] == b"<html>official</html>"
    assert response.json()["result"]["provider_name"] == "engineer-saved-official-evidence"
    assert response.json()["result"]["provider_egress_performed"] is False


def test_cable_ampacity_api_is_authenticated_traceable_and_strict(tmp_path: Path) -> None:
    root = create_project(tmp_path)
    context_id, digest = create_calculation_context_evidence(root)
    app = create_app(token=TOKEN, project_root=root)
    calculation = {
        "calculation_id": "calculation:ampacity_api",
        "calculation_version": "m20.cable_ampacity_derating.v1",
        "source_route_id": "route:1",
        "cable_designation": "Reviewed cable candidate",
        "base_ampacity_amperes": 20.0,
        "base_ampacity_basis_reference": "Engineer-reviewed project schedule row C-01.",
        "derating_factors": [
            {
                "factor_id": "factor:ambient",
                "label": "Ambient temperature",
                "factor": 0.94,
                "basis_reference": "Reviewed ambient factor input.",
            },
            {
                "factor_id": "factor:grouping",
                "label": "Circuit grouping",
                "factor": 0.8,
                "basis_reference": "Reviewed grouping factor input.",
            },
        ],
        "reviewed_by": "engineer:gamal",
        "assumptions": ["Factors apply to the identified route and cable basis."],
    }
    request = {
        "context_id": context_id,
        "context_evidence_sha256": digest,
        "calculation": calculation,
    }
    with TestClient(app, base_url="http://127.0.0.1") as client:
        unauthenticated = client.post("/v1/calculations/cable-ampacity", json=request)
        created = client.post(
            "/v1/calculations/cable-ampacity", headers=authorization(), json=request
        )
        unknown_route = client.post(
            "/v1/calculations/cable-ampacity",
            headers=authorization(),
            json={
                **request,
                "calculation": {**calculation, "source_route_id": "route:missing"},
            },
        )
        coerced = client.post(
            "/v1/calculations/cable-ampacity",
            headers=authorization(),
            json={
                **request,
                "calculation": {**calculation, "base_ampacity_amperes": "20.0"},
            },
        )

    assert unauthenticated.status_code == 401
    assert created.status_code == 201
    result = created.json()["result"]
    assert result["context_evidence_sha256"] == digest
    assert result["route_source"]["object_id"] == "A1"
    assert result["combined_derating_factor"] == 0.752
    assert result["derated_ampacity_amperes"] == 15.04
    assert [item["output_ampacity_amperes"] for item in result["stages"]] == [18.8, 15.04]
    assert result["cable_selection_performed"] is False
    assert result["protective_device_selection_performed"] is False
    assert result["compliance_evaluation_performed"] is False
    assert result["drawing_mutation_performed"] is False
    assert len(result["result_sha256"]) == 64
    assert unknown_route.status_code == 400
    assert coerced.status_code == 422


def test_short_circuit_api_is_authenticated_traceable_and_strict(tmp_path: Path) -> None:
    root = create_project(tmp_path)
    context_id, digest = create_calculation_context_evidence(root)
    app = create_app(token=TOKEN, project_root=root)
    calculation = {
        "calculation_id": "calculation:short_circuit_api",
        "calculation_version": "m20.short_circuit_source_equivalent.v1",
        "source_route_id": "route:1",
        "fault_arrangement": "balanced_three_phase",
        "nominal_voltage_volts": 400.0,
        "voltage_multiplier": 1.0,
        "source_resistance_ohms": 0.01,
        "source_reactance_ohms": 0.05,
        "path_resistance_ohms": 0.02,
        "path_reactance_ohms": 0.04,
        "impedance_basis_reference": "Engineer-reviewed source/path impedance schedule.",
        "reviewed_by": "engineer:gamal",
        "assumptions": ["Bolted symmetrical fault at the identified route."],
    }
    request = {
        "context_id": context_id,
        "context_evidence_sha256": digest,
        "calculation": calculation,
    }
    with TestClient(app, base_url="http://127.0.0.1") as client:
        unauthenticated = client.post("/v1/calculations/short-circuit", json=request)
        created = client.post(
            "/v1/calculations/short-circuit", headers=authorization(), json=request
        )
        unknown_route = client.post(
            "/v1/calculations/short-circuit",
            headers=authorization(),
            json={
                **request,
                "calculation": {**calculation, "source_route_id": "route:missing"},
            },
        )
        coerced = client.post(
            "/v1/calculations/short-circuit",
            headers=authorization(),
            json={
                **request,
                "calculation": {**calculation, "nominal_voltage_volts": "400.0"},
            },
        )

    assert unauthenticated.status_code == 401
    assert created.status_code == 201
    result = created.json()["result"]
    assert result["context_evidence_sha256"] == digest
    assert result["route_source"]["object_id"] == "A1"
    assert result["total_impedance_magnitude_ohms"] == 0.094868329805
    assert result["symmetrical_short_circuit_current_amperes"] == 2434.322477802057
    assert result["protective_device_adequacy_evaluated"] is False
    assert result["peak_current_calculated"] is False
    assert result["arc_flash_calculated"] is False
    assert result["compliance_evaluation_performed"] is False
    assert result["drawing_mutation_performed"] is False
    assert len(result["result_sha256"]) == 64
    assert unknown_route.status_code == 400
    assert coerced.status_code == 422


def test_candidate_adequacy_api_recalculates_inputs_and_rejects_client_results(
    tmp_path: Path,
) -> None:
    root = create_project(tmp_path)
    context_id, digest = create_calculation_context_evidence(root)
    app = create_app(token=TOKEN, project_root=root)
    low_voltage = {
        "calculation_id": "calculation:adequacy_lv",
        "calculation_version": "m20.lv_current_voltage_drop.v1",
        "phase_system": "single_phase",
        "load_output_power_watts": 1000.0,
        "demand_factor": 1.0,
        "nominal_voltage_volts": 230.0,
        "power_factor": 0.9,
        "efficiency": 0.95,
        "conductor_ac_resistance_ohm_per_km": 7.41,
        "conductor_reactance_ohm_per_km": 0.08,
        "load_object_ids": ["device:2"],
        "route_ids": ["route:1"],
        "reviewed_by": "engineer:gamal",
        "assumptions": ["Reviewed load basis."],
    }
    ampacity = {
        "calculation_id": "calculation:adequacy_ampacity",
        "calculation_version": "m20.cable_ampacity_derating.v1",
        "source_route_id": "route:1",
        "cable_designation": "Reviewed cable candidate",
        "base_ampacity_amperes": 20.0,
        "base_ampacity_basis_reference": "Reviewed project ampacity schedule.",
        "derating_factors": [{
            "factor_id": "factor:ambient",
            "label": "Ambient temperature",
            "factor": 0.94,
            "basis_reference": "Reviewed ambient input.",
        }, {
            "factor_id": "factor:grouping",
            "label": "Circuit grouping",
            "factor": 0.8,
            "basis_reference": "Reviewed grouping input.",
        }],
        "reviewed_by": "engineer:gamal",
        "assumptions": ["Factors apply to the route."],
    }
    short_circuit = {
        "calculation_id": "calculation:adequacy_fault",
        "calculation_version": "m20.short_circuit_source_equivalent.v1",
        "source_route_id": "route:1",
        "fault_arrangement": "balanced_three_phase",
        "nominal_voltage_volts": 400.0,
        "voltage_multiplier": 1.0,
        "source_resistance_ohms": 0.01,
        "source_reactance_ohms": 0.05,
        "path_resistance_ohms": 0.02,
        "path_reactance_ohms": 0.04,
        "impedance_basis_reference": "Reviewed source/path impedance schedule.",
        "reviewed_by": "engineer:gamal",
        "assumptions": ["Bolted symmetrical fault."],
    }
    rule_set = {
        "rule_set_id": "ruleset:adequacy_api",
        "rule_set_version": "m20.candidate_adequacy.v1",
        "candidate_id": "candidate:api_test",
        "protective_device_designation": "10 A / 6 kA candidate",
        "protective_device_rated_current_amperes": 10.0,
        "protective_device_breaking_capacity_amperes": 6000.0,
        "required_ampacity_multiplier": 1.25,
        "maximum_voltage_drop_percent": 5.0,
        "basis_reference": "Reviewed project candidate rules.",
        "reviewed_by": "engineer:gamal",
        "assumptions": ["Candidate values are reviewed inputs."],
    }
    request = {
        "context_id": context_id,
        "context_evidence_sha256": digest,
        "low_voltage": low_voltage,
        "ampacity": ampacity,
        "short_circuit": short_circuit,
        "rule_set": rule_set,
    }
    with TestClient(app, base_url="http://127.0.0.1") as client:
        unauthenticated = client.post(
            "/v1/calculations/candidate-adequacy", json=request
        )
        created = client.post(
            "/v1/calculations/candidate-adequacy",
            headers=authorization(),
            json=request,
        )
        client_result = client.post(
            "/v1/calculations/candidate-adequacy",
            headers=authorization(),
            json={**request, "low_voltage_result": {"calculated_current_amperes": 0.1}},
        )
        coerced = client.post(
            "/v1/calculations/candidate-adequacy",
            headers=authorization(),
            json={
                **request,
                "rule_set": {
                    **rule_set,
                    "protective_device_rated_current_amperes": "10.0",
                },
            },
        )
        inconsistent_route = client.post(
            "/v1/calculations/candidate-adequacy",
            headers=authorization(),
            json={
                **request,
                "short_circuit": {
                    **short_circuit,
                    "source_route_id": "route:missing",
                },
            },
        )

    assert unauthenticated.status_code == 401
    assert created.status_code == 201
    result = created.json()["result"]
    assert result["candidate_adequate_under_reviewed_rules"] is True
    assert [item["outcome"] for item in result["findings"]] == ["pass"] * 5
    assert result["low_voltage_result_sha256"]
    assert result["ampacity_result_sha256"]
    assert result["short_circuit_result_sha256"]
    assert result["automatic_selection_performed"] is False
    assert result["compliance_evaluation_performed"] is False
    assert result["drawing_mutation_performed"] is False
    assert len(result["result_sha256"]) == 64
    assert client_result.status_code == 422
    assert coerced.status_code == 422
    assert inconsistent_route.status_code == 400


def test_protective_conductor_api_recalculates_fault_and_preserves_unknown_outcome(
    tmp_path: Path,
) -> None:
    root = create_project(tmp_path)
    context_id, digest = create_calculation_context_evidence(root)
    app = create_app(token=TOKEN, project_root=root)
    short_circuit = {
        "calculation_id": "calculation:pe_fault_api",
        "calculation_version": "m20.short_circuit_source_equivalent.v1",
        "source_route_id": "route:1",
        "fault_arrangement": "balanced_three_phase",
        "nominal_voltage_volts": 400.0,
        "voltage_multiplier": 1.0,
        "source_resistance_ohms": 0.01,
        "source_reactance_ohms": 0.05,
        "path_resistance_ohms": 0.02,
        "path_reactance_ohms": 0.04,
        "impedance_basis_reference": "Reviewed source/path impedance schedule.",
        "reviewed_by": "engineer:gamal",
        "assumptions": ["Bolted symmetrical fault."],
    }
    rule_input = {
        "calculation_id": "calculation:pe_api",
        "calculation_version": "m20.protective_conductor_adiabatic.v1",
        "source_route_id": "route:1",
        "applicability": "applicable",
        "applicability_basis_reference": "Reviewed project applicability decision.",
        "disconnection_basis": "reviewed_protective_device_time",
        "disconnection_basis_reference": "Reviewed protective-device clearing time.",
        "disconnection_time_seconds": 0.2,
        "conductor_factor_amperes_sqrt_second_per_square_millimeter": 115.0,
        "rule_source_id": "source:pe_rule_api",
        "rule_source_title": "Reviewed PE adiabatic rule source",
        "rule_source_version": "project-reviewed-2026-08-11",
        "rule_source_locator": "project://electrical/rules/pe-adiabatic",
        "rule_source_sha256": hashlib.sha256(b"reviewed PE rule").hexdigest(),
        "reviewed_by": "engineer:gamal",
        "assumptions": ["Fault duration and conductor factor apply to this route."],
    }
    request = {
        "context_id": context_id,
        "context_evidence_sha256": digest,
        "rule_input": rule_input,
        "short_circuit": short_circuit,
    }
    unknown_rule = {
        **rule_input,
        "applicability": "unknown",
        "disconnection_basis": "unknown",
        "disconnection_time_seconds": None,
        "conductor_factor_amperes_sqrt_second_per_square_millimeter": None,
    }
    with TestClient(app, base_url="http://127.0.0.1") as client:
        unauthenticated = client.post(
            "/v1/calculations/protective-conductor", json=request
        )
        calculated = client.post(
            "/v1/calculations/protective-conductor",
            headers=authorization(),
            json=request,
        )
        unknown = client.post(
            "/v1/calculations/protective-conductor",
            headers=authorization(),
            json={
                "context_id": context_id,
                "context_evidence_sha256": digest,
                "rule_input": unknown_rule,
                "short_circuit": None,
            },
        )
        client_result = client.post(
            "/v1/calculations/protective-conductor",
            headers=authorization(),
            json={**request, "short_circuit_result": {"fault_current_amperes": 1.0}},
        )
        coerced = client.post(
            "/v1/calculations/protective-conductor",
            headers=authorization(),
            json={
                **request,
                "rule_input": {**rule_input, "disconnection_time_seconds": "0.2"},
            },
        )
        unknown_with_fault = client.post(
            "/v1/calculations/protective-conductor",
            headers=authorization(),
            json={**request, "rule_input": unknown_rule},
        )

    assert unauthenticated.status_code == 401
    assert calculated.status_code == 201
    result = calculated.json()["result"]
    assert result["outcome"] == "calculated"
    assert result["fault_current_amperes"] == 2434.322477802057
    assert result["required_cross_section_square_millimeters"] == 9.466627025254
    assert result["short_circuit_result_sha256"]
    assert result["conductor_selection_performed"] is False
    assert result["compliance_evaluation_performed"] is False
    assert result["drawing_mutation_performed"] is False
    assert unknown.status_code == 201
    assert unknown.json()["result"]["outcome"] == "unknown"
    assert unknown.json()["result"]["required_cross_section_square_millimeters"] is None
    assert client_result.status_code == 422
    assert coerced.status_code == 422
    assert unknown_with_fault.status_code == 400


def test_copilot_api_binds_context_owner_messages_egress_and_cancellation(
    tmp_path: Path,
) -> None:
    root = create_project(tmp_path)
    context_id, digest = create_calculation_context_evidence(root)
    app = create_app(token=TOKEN, project_root=root)
    with TestClient(app, base_url="http://127.0.0.1") as client:
        created = client.post(
            "/v1/copilot/sessions",
            headers=authorization(),
            json={
                "context_id": context_id,
                "evidence_sha256": digest,
                "language": "ar",
                "ttl_minutes": 60,
            },
        )
        assert created.status_code == 201
        session = created.json()["session"]
        session_id = session["session_id"]
        owner_key = created.json()["owner_key"]
        owned = {**authorization(), "X-AI-Engineer-Session-Key": owner_key}

        missing_owner = client.get(
            f"/v1/copilot/sessions/{session_id}", headers=authorization()
        )
        wrong_owner = client.get(
            f"/v1/copilot/sessions/{session_id}",
            headers={
                **authorization(),
                "X-AI-Engineer-Session-Key": "wrong-owner-key-" + "x" * 40,
            },
        )
        messaged = client.post(
            f"/v1/copilot/sessions/{session_id}/messages",
            headers=owned,
            json={"language": "ar", "content": "راجع الاختيار الحالي فقط."},
        )
        review = client.post(
            f"/v1/copilot/sessions/{session_id}/egress-reviews",
            headers=owned,
            json={
                "destination": "provider:configured",
                "privacy": "project_confidential",
                "fields": [
                    {"field_name": "question.text", "value": "كم طول المسار؟"}
                ],
                "ttl_minutes": 10,
            },
        )
        review_id = review.json()["review"]["review_id"]
        decided = client.post(
            f"/v1/copilot/sessions/{session_id}/egress-reviews/{review_id}/decision",
            headers=owned,
            json={"approved": True},
        )
        cancelled = client.post(
            f"/v1/copilot/sessions/{session_id}/cancel", headers=owned
        )
        rejected_message = client.post(
            f"/v1/copilot/sessions/{session_id}/messages",
            headers=owned,
            json={"language": "en", "content": "Continue."},
        )

    assert session["context"]["context_id"] == context_id
    assert session["context_summary"]["device_count"] == 1
    assert session["context_summary"]["block_reference_count"] == 1
    assert session["context_summary"]["route_primitive_count"] == 1
    assert session["context_summary"]["route_count"] == 1
    assert session["context_summary"]["unknown_count"] == 0
    assert session["drawing_write_allowed"] is False
    assert missing_owner.status_code == 422
    assert wrong_owner.status_code == 404
    assert messaged.json()["session"]["messages"][0]["role"] == "engineer"
    assert review.json()["review"]["transmission_performed"] is False
    assert decided.json()["review"]["status"] == "approved"
    assert cancelled.json()["session"]["status"] == "cancelled"
    assert rejected_message.status_code == 400


def test_general_copilot_session_needs_no_drawing_context_and_has_zero_summary(
    tmp_path: Path,
) -> None:
    root = create_project(tmp_path)
    app = create_app(token=TOKEN, project_root=root)
    with TestClient(app, base_url="http://127.0.0.1") as client:
        created = client.post(
            "/v1/copilot/general-sessions",
            headers=authorization(),
            json={"language": "ar", "ttl_minutes": 60},
        )

    assert created.status_code == 201
    session = created.json()["session"]
    assert session["project_id"] == "autocad.general"
    assert session["context"]["scope"] == "general"
    assert session["context_summary"]["entity_count"] == 0
    assert session["drawing_write_allowed"] is False


def test_general_copilot_session_accepts_reviewed_multiturn_fields(
    tmp_path: Path,
) -> None:
    root = create_project(tmp_path)
    app = create_app(token=TOKEN, project_root=root)
    with TestClient(app, base_url="http://127.0.0.1") as client:
        created = client.post(
            "/v1/copilot/general-sessions",
            headers=authorization(),
            json={"language": "ar", "ttl_minutes": 60},
        ).json()
        session_id = created["session"]["session_id"]
        owned = {
            **authorization(),
            "X-AI-Engineer-Session-Key": created["owner_key"],
        }
        message = client.post(
            f"/v1/copilot/sessions/{session_id}/messages",
            headers=owned,
            json={"language": "ar", "content": "السلام عليكم ورحمة الله وبركاته"},
        )
        review = client.post(
            f"/v1/copilot/sessions/{session_id}/egress-reviews",
            headers=owned,
            json={
                "destination": "gemini",
                "privacy": "project_confidential",
                "fields": [
                    {"field_name": "question.text", "value": "السلام عليكم ورحمة الله وبركاته"},
                    {"field_name": "conversation.history", "value": "engineer: السلام عليكم ورحمة الله وبركاته"},
                    {
                        "field_name": "context.summary",
                        "value": "mode=general;drawing_context=none;drawing_write_allowed=false",
                    },
                ],
                "ttl_minutes": 10,
            },
        )

    assert message.status_code == 200
    assert review.status_code == 201, review.text
    assert review.json()["review"]["transmission_performed"] is False


def test_drafting_api_reimports_validates_reviews_and_rejects_replay(
    tmp_path: Path,
) -> None:
    root = create_project(tmp_path)
    context_id, digest = create_context_evidence(root)
    app = create_app(token=TOKEN, project_root=root)
    with TestClient(app, base_url="http://127.0.0.1") as client:
        application = app.state.application
        imported = application.autocad_manager.import_drawing_context(
            context_id=context_id,
            evidence_sha256=digest,
            confirmed=True,
        )
        catalog_path = root / "plugins" / "electrical_symbols" / "catalog.json"
        symbol = ElectricalSymbolCatalog.load(catalog_path).get("socket_single")
        binding = create_source_model_binding(
            imported.model,
            context_evidence_sha256=digest,
            symbol_catalog_sha256=hashlib.sha256(catalog_path.read_bytes()).hexdigest(),
        )
        proposal = create_drafting_proposal(
            proposal_id="b" * 32,
            binding=binding,
            layers=(
                ProposedLayer(
                    "layer:proposed-power",
                    symbol.insertion_layer,
                    symbol.layer_color,
                    symbol.linetype,
                    True,
                ),
            ),
            devices=(
                ProposedDevice(
                    "device:socket-1",
                    symbol.symbol_id,
                    symbol.definition_sha256,
                    symbol.block_name,
                    "layer:proposed-power",
                    (100.0, 200.0, 0.0),
                    0.0,
                    (1.0, 1.0, 1.0),
                    (),
                ),
            ),
            terminals=(),
            circuits=(),
            routes=(),
            input_issues=(),
            created_at="2026-08-06T12:00:00Z",
            expires_at="2030-08-08T12:00:00Z",
        )
        malformed_proposal = proposal.to_dict()
        malformed_proposal["arbitrary_command"] = "_.ERASE ALL"
        malformed = client.post(
            "/v1/drafting/runs",
            headers=authorization(),
            json={"proposal": malformed_proposal, "ttl_minutes": 60},
        )
        assert json.loads(
            (root / "data" / "drafting_runs.json").read_text(encoding="utf-8")
        )["runs"] == []
        unauthenticated = client.post(
            "/v1/drafting/runs",
            json={"proposal": proposal.to_dict(), "ttl_minutes": 60},
        )
        created = client.post(
            "/v1/drafting/runs",
            headers=authorization(),
            json={"proposal": proposal.to_dict(), "ttl_minutes": 60},
        )
        assert created.status_code == 201
        body = created.json()
        run_id = body["run"]["run_id"]
        owner_key = body["owner_key"]
        owned = {**authorization(), "X-AI-Engineer-Drafting-Key": owner_key}
        wrong_owner = client.get(
            f"/v1/drafting/runs/{run_id}",
            headers={
                **authorization(),
                "X-AI-Engineer-Drafting-Key": "wrong-drafting-owner-key-" + "x" * 32,
            },
        )
        review_without_preview = client.post(
            f"/v1/drafting/runs/{run_id}/preview-review", headers=owned
        )
        previewed = client.post(
            f"/v1/drafting/runs/{run_id}/preview",
            headers=owned,
            json={"proposal": proposal.to_dict(), "ttl_minutes": 30},
        )
        preview_wrong_owner = client.post(
            f"/v1/drafting/runs/{run_id}/preview",
            headers={
                **authorization(),
                "X-AI-Engineer-Drafting-Key": "wrong-drafting-owner-key-" + "x" * 32,
            },
            json={"proposal": proposal.to_dict(), "ttl_minutes": 30},
        )
        pending = client.post(
            f"/v1/drafting/runs/{run_id}/preview-review", headers=owned
        )
        approved = client.post(
            f"/v1/drafting/runs/{run_id}/preview-review/decision",
            headers=owned,
            json={"approved": True},
        )
        replay = client.post(
            f"/v1/drafting/runs/{run_id}/preview-review/decision",
            headers=owned,
            json={"approved": True},
        )

    restarted_app = create_app(token=TOKEN, project_root=root)
    with TestClient(restarted_app, base_url="http://127.0.0.1") as restarted_client:
        restored = restarted_client.get(
            f"/v1/drafting/runs/{run_id}", headers=owned
        )

    stored = (root / "data" / "drafting_runs.json").read_text(encoding="utf-8")
    assert malformed.status_code == 400
    assert unauthenticated.status_code == 401
    assert body["validation"]["is_valid"] is True
    assert body["run"]["drawing_mutation_performed"] is False
    assert wrong_owner.status_code == 404
    assert review_without_preview.status_code == 400
    assert previewed.status_code == 200
    assert preview_wrong_owner.status_code == 404
    assert previewed.json()["run"]["status"] == "preview_ready"
    assert previewed.json()["run"]["preview_sha256"] == previewed.json()["preview"]["preview_sha256"]
    assert previewed.json()["preview"]["drawing_mutation_performed"] is False
    assert previewed.json()["preview"]["execution_approved"] is False
    assert pending.json()["run"]["status"] == "review_pending"
    assert approved.json()["run"]["status"] == "approved"
    assert replay.status_code == 400
    assert restored.status_code == 200
    assert restored.json()["run"]["status"] == "approved"
    assert owner_key not in stored
    assert symbol.block_name not in stored


def test_drafting_execution_api_binds_source_approval_and_blocks_replay(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    root = create_project(tmp_path)
    context_id, digest = create_context_evidence(root)
    source = root / "workspace" / "sample.dwg"
    source.write_bytes(b"reviewed API drawing source")
    source_sha256 = hashlib.sha256(source.read_bytes()).hexdigest()
    output = root / "workspace" / "execution-output.dwg"
    output.write_bytes(b"verified API scratch output")
    output_sha256 = hashlib.sha256(output.read_bytes()).hexdigest()
    app = create_app(token=TOKEN, project_root=root)
    with TestClient(app, base_url="http://127.0.0.1") as client:
        application = app.state.application
        imported = application.autocad_manager.import_drawing_context(
            context_id=context_id, evidence_sha256=digest, confirmed=True
        )
        catalog_path = root / "plugins" / "electrical_symbols" / "catalog.json"
        symbol = ElectricalSymbolCatalog.load(catalog_path).get("socket_single")
        wire_layer = next(
            item for item in imported.model.layers if item.name.casefold() == "e-wire"
        )
        binding = create_source_model_binding(
            imported.model,
            context_evidence_sha256=digest,
            symbol_catalog_sha256=hashlib.sha256(catalog_path.read_bytes()).hexdigest(),
        )
        devices = (
            ProposedDevice(
                "device:source", symbol.symbol_id, symbol.definition_sha256,
                symbol.block_name, "layer:power", (0.0, 0.0, 0.0), 0.0,
                (1.0, 1.0, 1.0), (),
            ),
            ProposedDevice(
                "device:load", symbol.symbol_id, symbol.definition_sha256,
                symbol.block_name, "layer:power", (1000.0, 0.0, 0.0), 90.0,
                (1.0, 1.0, 1.0), (),
            ),
        )
        proposal = create_drafting_proposal(
            proposal_id="e" * 32,
            binding=binding,
            layers=(
                ProposedLayer(
                    "layer:power", symbol.insertion_layer, symbol.layer_color,
                    symbol.linetype, True,
                ),
                ProposedLayer(
                    wire_layer.layer_id,
                    wire_layer.name,
                    wire_layer.color_index,
                    wire_layer.linetype,
                    False,
                ),
            ),
            devices=devices,
            terminals=(
                ProposedTerminal("terminal:source", "device:source", "L", (0.0, 0.0, 0.0)),
                ProposedTerminal("terminal:load", "device:load", "L", (1000.0, 0.0, 0.0)),
            ),
            circuits=(
                ProposedCircuit(
                    "circuit:power", "Power circuit",
                    ("terminal:source", "terminal:load"), ("route:power",),
                ),
            ),
            routes=(
                ProposedRoute(
                    "route:power", "circuit:power", wire_layer.layer_id,
                    "terminal:source", "terminal:load",
                    ((0.0, 0.0, 0.0), (500.0, 0.0, 0.0), (1000.0, 0.0, 0.0)),
                ),
            ),
            input_issues=(),
            created_at="2026-08-06T12:00:00Z",
            expires_at="2030-08-08T12:00:00Z",
        )

        def fake_execute(value, **kwargs):
            return CircuitFragmentArtifact(
                "1" * 32, source, output, source_sha256, output_sha256, "24.0",
                symbol.symbol_id, symbol.block_name, symbol.version,
                symbol.definition_sha256, symbol.insertion_layer, "E-WIRE",
                kwargs["placements"], kwargs["wire_points"], False, 2, 2, 1, 5,
                0, True, True, 0.1, False, False,
            )

        def fake_recovery(artifact, **kwargs):
            return DraftingRecoveryEvidence(
                output.parent / "drafting-recovery.json", "2" * 64,
                "2026-08-10T08:30:00Z", source, source_sha256, output,
                output_sha256, artifact.run_id,
            )

        monkeypatch.setattr(
            application.autocad_manager,
            "create_electrical_circuit_fragment_scratch",
            fake_execute,
        )
        monkeypatch.setattr(
            application.autocad_manager,
            "record_drafting_recovery_evidence",
            fake_recovery,
        )
        created = client.post(
            "/v1/drafting/runs", headers=authorization(),
            json={"proposal": proposal.to_dict(), "ttl_minutes": 60},
        ).json()
        run_id = created["run"]["run_id"]
        owned = {
            **authorization(),
            "X-AI-Engineer-Drafting-Key": created["owner_key"],
        }
        client.post(
            f"/v1/drafting/runs/{run_id}/preview", headers=owned,
            json={"proposal": proposal.to_dict(), "ttl_minutes": 30},
        )
        execution_request = {
            "proposal": proposal.to_dict(),
            "source_path": str(source),
            "source_sha256": source_sha256,
        }
        before_approval = client.post(
            f"/v1/drafting/runs/{run_id}/execute",
            headers=owned,
            json=execution_request,
        )
        pending = client.post(
            f"/v1/drafting/runs/{run_id}/execution-review",
            headers=owned,
            json=execution_request,
        )
        wrong_decision_route = client.post(
            f"/v1/drafting/runs/{run_id}/preview-review/decision",
            headers=owned,
            json={"approved": True},
        )
        approved = client.post(
            f"/v1/drafting/runs/{run_id}/execution-review/decision",
            headers=owned,
            json={"approved": True},
        )
        mismatched = client.post(
            f"/v1/drafting/runs/{run_id}/execute",
            headers=owned,
            json={**execution_request, "source_sha256": "f" * 64},
        )
        executed = client.post(
            f"/v1/drafting/runs/{run_id}/execute",
            headers=owned,
            json=execution_request,
        )
        replay = client.post(
            f"/v1/drafting/runs/{run_id}/execute",
            headers=owned,
            json=execution_request,
        )

    assert before_approval.status_code == 400
    assert pending.status_code == 200, pending.text
    assert pending.json()["run"]["status"] == "execution_review_pending"
    assert pending.json()["execution"]["source_will_be_modified"] is False
    assert wrong_decision_route.status_code == 400
    assert approved.json()["run"]["status"] == "approved"
    assert mismatched.status_code == 400
    assert executed.status_code == 200
    assert executed.json()["run"]["status"] == "executed"
    assert executed.json()["run"]["drawing_mutation_performed"] is True
    assert executed.json()["recovery"]["evidence_sha256"] == "2" * 64
    assert replay.status_code == 400


def test_copilot_provider_api_requires_approved_exact_egress_review(
    tmp_path: Path,
) -> None:
    requests: list[CopilotProviderRequest] = []

    def generate(request: CopilotProviderRequest) -> str:
        requests.append(request)
        return json.dumps(
            {
                "language": "en",
                "answer": "The approved summary contains one route.",
                "citations": [
                    {"source_field": "context.summary", "title": "Context summary"}
                ],
            }
        )

    root = create_project(tmp_path)
    context_id, digest = create_context_evidence(root)
    app = create_app(
        token=TOKEN,
        project_root=root,
        copilot_provider=CopilotProviderAdapter("provider:test", generate),
    )
    with TestClient(app, base_url="http://127.0.0.1") as client:
        status = client.get("/v1/copilot/provider-status", headers=authorization())
        created = client.post(
            "/v1/copilot/sessions",
            headers=authorization(),
            json={
                "context_id": context_id,
                "evidence_sha256": digest,
                "language": "en",
                "ttl_minutes": 60,
            },
        ).json()
        session_id = created["session"]["session_id"]
        owned = {
            **authorization(),
            "X-AI-Engineer-Session-Key": created["owner_key"],
        }
        review = client.post(
            f"/v1/copilot/sessions/{session_id}/egress-reviews",
            headers=owned,
            json={
                "destination": "provider:test",
                "privacy": "internal",
                "fields": [
                    {"field_name": "context.summary", "value": "route_count=1"}
                ],
                "ttl_minutes": 10,
            },
        ).json()["review"]
        blocked = client.post(
            f"/v1/copilot/sessions/{session_id}/answers",
            headers=owned,
            json={"review_id": review["review_id"]},
        )
        calls_before_approval = len(requests)
        client.post(
            f"/v1/copilot/sessions/{session_id}/egress-reviews/{review['review_id']}/decision",
            headers=owned,
            json={"approved": True},
        )
        answered = client.post(
            f"/v1/copilot/sessions/{session_id}/answers",
            headers=owned,
            json={"review_id": review["review_id"]},
        )
        answer_id = answered.json()["answer"]["answer_id"]
        fetched = client.get(
            f"/v1/copilot/sessions/{session_id}/answers/{answer_id}", headers=owned
        )
        repeated = client.post(
            f"/v1/copilot/sessions/{session_id}/answers",
            headers=owned,
            json={"review_id": review["review_id"]},
        )

    assert status.json() == {"configured": True, "provider_name": "provider:test"}
    assert blocked.status_code == 400
    assert calls_before_approval == 0
    assert answered.status_code == 201
    assert answered.json()["answer"]["data_egress_performed"] is True
    assert answered.json()["answer"]["drawing_write_performed"] is False
    assert answered.json()["session"]["messages"][-1]["role"] == "copilot"
    assert fetched.json()["answer"]["answer_id"] == answer_id
    assert repeated.status_code == 400
    assert len(requests) == 1


def test_copilot_provider_status_and_answer_are_disabled_by_default(
    api_client: tuple,
) -> None:
    client, _ = api_client

    status = client.get("/v1/copilot/provider-status", headers=authorization())

    assert status.json() == {"configured": False, "provider_name": None}


def test_developer_api_requires_plan_and_action_review(tmp_path: Path) -> None:
    responses = deque(
        [
            json.dumps({"summary": "Read a safe file.", "steps": ["Read the file."]}),
            json.dumps(
                {
                    "kind": "command",
                    "command_name": "file.read",
                    "payload": {"path": "sample.txt"},
                    "rationale": "Inspect the requested bounded file.",
                }
            ),
            json.dumps({"accepted": True, "reason": "The file was read successfully."}),
            json.dumps({"kind": "complete", "summary": "The read was verified."}),
        ]
    )
    requests: list[ProviderRequest] = []

    def generate(request: ProviderRequest) -> str:
        requests.append(request)
        return responses.popleft()

    root = create_project(tmp_path)
    (root / "workspace").mkdir()
    (root / "workspace" / "sample.txt").write_text("bounded", encoding="utf-8")
    app = create_app(
        token=TOKEN,
        project_root=root,
        developer_provider=ProviderAdapter("api-provider", generate),
    )
    with TestClient(app, base_url="http://127.0.0.1") as client:
        headers = authorization()
        status = client.get("/v1/developer/status", headers=headers)
        planned = client.post(
            "/v1/developer/runs",
            headers=headers,
            json={"goal": "Read and verify sample.txt.", "maximum_actions": 1},
        )
        run_id = planned.json()["run_id"]
        ready = client.post(
            f"/v1/developer/runs/{run_id}/plan-review",
            headers=headers,
            json={"approved": True},
        )
        proposed = client.post(
            f"/v1/developer/runs/{run_id}/advance", headers=headers
        )
        verified = client.post(
            f"/v1/developer/runs/{run_id}/action-review",
            headers=headers,
            json={"approved": True},
        )
        completed = client.post(
            f"/v1/developer/runs/{run_id}/advance", headers=headers
        )
        listed = client.get("/v1/developer/runs", headers=headers)

    assert status.json() == {"configured": True, "provider_name": "api-provider"}
    assert planned.status_code == 201
    assert planned.json()["status"] == "awaiting_plan_review"
    assert ready.json()["status"] == "ready"
    assert proposed.json()["status"] == "awaiting_action_review"
    assert proposed.json()["pending_action"]["payload"] == {"path": "sample.txt"}
    assert verified.json()["last_verification_accepted"] is True
    assert completed.json()["status"] == "succeeded"
    assert listed.json()["runs"][0]["run_id"] == run_id
    assert len(requests) == 4


def test_strict_validation_forbids_coercion_extra_fields_and_input_echo(
    api_client: tuple,
) -> None:
    client, _ = api_client
    secret_value = "do-not-echo-this-value"

    response = client.post(
        "/v1/tasks",
        headers=authorization(),
        json={
            "command_name": "system.snapshot",
            "payload": {},
            "max_attempts": 1.0,
            "unexpected": secret_value,
        },
    )

    assert response.status_code == 422
    assert secret_value not in response.text
    assert '"input":' not in response.text


def test_generic_task_submission_and_execution(api_client: tuple) -> None:
    client, _ = api_client
    submitted = client.post(
        "/v1/tasks",
        headers=authorization(),
        json={"command_name": "system.snapshot", "payload": {}, "max_attempts": 1},
    )

    assert submitted.status_code == 201
    task_id = submitted.json()["task_id"]
    assert "payload" not in submitted.json()
    completed = client.post(
        f"/v1/tasks/{task_id}/run",
        headers=authorization(),
        json={"approval_id": None},
    )

    assert completed.status_code == 200
    assert completed.json()["status"] == "succeeded"
    assert completed.json()["result"]["operating_system"]


def test_high_risk_file_write_requires_explicit_approval(api_client: tuple) -> None:
    client, app = api_client
    awaiting = client.post(
        "/v1/files/write",
        headers=authorization(),
        json={"path": "api/generated.txt", "content": "approved", "create_parents": True},
    )

    assert awaiting.status_code == 200
    body = awaiting.json()
    assert body["status"] == "awaiting_approval"
    assert "payload" not in body
    approval_id = body["approval_id"]
    task_id = body["task_id"]
    approval = client.get(
        f"/v1/approvals/{approval_id}", headers=authorization()
    )
    assert approval.json()["status"] == "pending"

    decision = client.post(
        f"/v1/approvals/{approval_id}/decision",
        headers=authorization(),
        json={"approved": True},
    )
    assert decision.json()["status"] == "approved"
    completed = client.post(
        f"/v1/tasks/{task_id}/run",
        headers=authorization(),
        json={"approval_id": approval_id},
    )

    assert completed.json()["status"] == "succeeded"
    root = app.state.application.config.workspace_dir
    assert (root / "api" / "generated.txt").read_text(encoding="utf-8") == "approved"
    consumed = client.get(
        f"/v1/approvals/{approval_id}", headers=authorization()
    )
    assert consumed.json()["status"] == "consumed"


def test_file_read_and_project_scan_routes_use_task_engine(api_client: tuple) -> None:
    client, app = api_client
    workspace = app.state.application.config.workspace_dir
    (workspace / "readme.txt").write_text("visible", encoding="utf-8")

    read = client.post(
        "/v1/files/read", headers=authorization(), json={"path": "readme.txt"}
    )
    scan = client.post(
        "/v1/scans", headers=authorization(), json={"path": "."}
    )

    assert read.json()["result"]["content"] == "visible"
    assert scan.json()["status"] == "succeeded"
    assert any(
        item["relative_path"] == "readme.txt"
        for item in scan.json()["result"]["files"]
    )


def test_missing_resources_return_404(api_client: tuple) -> None:
    client, _ = api_client

    task = client.get("/v1/tasks/missing", headers=authorization())
    approval = client.get("/v1/approvals/missing", headers=authorization())

    assert task.status_code == 404
    assert approval.status_code == 404


def test_unknown_command_returns_404_without_creating_task(api_client: tuple) -> None:
    client, _ = api_client

    response = client.post(
        "/v1/tasks",
        headers=authorization(),
        json={"command_name": "unknown.command", "payload": {}, "max_attempts": 1},
    )

    assert response.status_code == 404
    assert client.get("/v1/tasks", headers=authorization()).json()["tasks"] == []


def test_task_cancel_route_and_conflict_are_durable(api_client: tuple) -> None:
    client, _ = api_client
    submitted = client.post(
        "/v1/tasks",
        headers=authorization(),
        json={"command_name": "system.snapshot", "payload": {}, "max_attempts": 1},
    ).json()

    cancelled = client.post(
        f"/v1/tasks/{submitted['task_id']}/cancel", headers=authorization()
    )
    repeated = client.post(
        f"/v1/tasks/{submitted['task_id']}/cancel", headers=authorization()
    )

    assert cancelled.json()["status"] == "cancelled"
    assert repeated.status_code == 409
    listed = client.get("/v1/tasks", headers=authorization()).json()["tasks"]
    assert listed[0]["status"] == "cancelled"


def test_denied_file_delete_keeps_original_file(api_client: tuple) -> None:
    client, app = api_client
    target = app.state.application.config.workspace_dir / "keep.txt"
    target.write_text("keep", encoding="utf-8")
    awaiting = client.post(
        "/v1/files/delete",
        headers=authorization(),
        json={"path": "keep.txt"},
    ).json()
    approval_id = awaiting["approval_id"]
    client.post(
        f"/v1/approvals/{approval_id}/decision",
        headers=authorization(),
        json={"approved": False},
    )

    denied = client.post(
        f"/v1/tasks/{awaiting['task_id']}/run",
        headers=authorization(),
        json={"approval_id": approval_id},
    )

    assert denied.json()["status"] == "cancelled"
    assert target.read_text(encoding="utf-8") == "keep"


def test_oversized_streamed_body_is_rejected_before_validation(tmp_path: Path) -> None:
    app = create_app(
        token=TOKEN,
        project_root=create_project(tmp_path),
        max_request_body_bytes=1024,
    )
    with TestClient(app, base_url="http://127.0.0.1") as client:
        response = client.post(
            "/v1/files/write",
            headers={**authorization(), "Content-Type": "application/json"},
            content=json.dumps({"path": "large.txt", "content": "x" * 2000}),
        )

    assert response.status_code == 413


def test_unauthenticated_body_is_rejected_before_size_or_validation(
    tmp_path: Path,
) -> None:
    app = create_app(
        token=TOKEN,
        project_root=create_project(tmp_path),
        max_request_body_bytes=1024,
    )
    with TestClient(app, base_url="http://127.0.0.1") as client:
        response = client.post(
            "/v1/files/write",
            headers={"Content-Type": "application/json"},
            content=json.dumps({"path": "large.txt", "content": "x" * 2000}),
        )

    assert response.status_code == 401


def test_application_lifecycle_is_owned_by_api_lifespan(tmp_path: Path) -> None:
    app = create_app(token=TOKEN, project_root=create_project(tmp_path))
    application = app.state.application
    assert application.status is AppStatus.CREATED

    with TestClient(app, base_url="http://127.0.0.1") as client:
        assert application.status is AppStatus.RUNNING
        assert client.get("/health").status_code == 200

    assert application.status is AppStatus.STOPPED


@pytest.mark.parametrize("host", ["0.0.0.0", "localhost", "::1", "192.168.1.20"])
def test_server_configuration_rejects_non_literal_loopback_host(host: str) -> None:
    with pytest.raises(ApiConfigurationError, match="127.0.0.1"):
        ApiServerConfig(token=TOKEN, host=host)


@pytest.mark.parametrize("token", ["short", "a" * 40, "contains whitespace token value 123456"])
def test_server_configuration_rejects_weak_tokens(token: str) -> None:
    with pytest.raises(ApiConfigurationError, match="token"):
        ApiServerConfig(token=token)


def test_environment_configuration_requires_token_and_hides_repr() -> None:
    with pytest.raises(ApiConfigurationError, match="AI_ENGINEER_API_TOKEN"):
        ApiServerConfig.from_environment({})

    config = ApiServerConfig.from_environment({"AI_ENGINEER_API_TOKEN": TOKEN})
    assert config.host == "127.0.0.1"
    assert TOKEN not in repr(config)


def test_application_middleware_repr_does_not_expose_token(tmp_path: Path) -> None:
    app = create_app(token=TOKEN, project_root=create_project(tmp_path))

    assert TOKEN not in repr(app.user_middleware)


def test_run_server_enforces_single_local_worker(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    captured: dict[str, object] = {}

    def fake_run(app: object, **kwargs: object) -> None:
        captured.update(kwargs)

    monkeypatch.setattr("api.server.uvicorn.run", fake_run)
    run_server(ApiServerConfig(token=TOKEN), project_root=create_project(tmp_path))

    assert captured["host"] == "127.0.0.1"
    assert captured["workers"] == 1
    assert captured["proxy_headers"] is False
    assert captured["access_log"] is False


def test_cli_check_validates_without_starting_server(
    monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]
) -> None:
    monkeypatch.setenv("AI_ENGINEER_API_TOKEN", TOKEN)

    exit_code = main(["--check"])

    assert exit_code == 0
    assert "configuration is valid" in capsys.readouterr().out


def test_real_loopback_socket_health_and_authentication(tmp_path: Path) -> None:
    app = create_app(token=TOKEN, project_root=create_project(tmp_path))
    listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    listener.bind(("127.0.0.1", 0))
    listener.listen()
    port = listener.getsockname()[1]
    config = uvicorn.Config(
        app,
        host="127.0.0.1",
        port=port,
        access_log=False,
        log_level="critical",
        proxy_headers=False,
    )
    server = uvicorn.Server(config)
    worker = threading.Thread(
        target=server.run,
        kwargs={"sockets": [listener]},
        daemon=True,
    )
    worker.start()
    deadline = time.monotonic() + 5
    while not server.started and time.monotonic() < deadline:
        time.sleep(0.01)
    assert server.started

    try:
        with httpx.Client(base_url=f"http://127.0.0.1:{port}", timeout=2) as client:
            health = client.get("/health")
            unauthenticated = client.get("/v1/status")
            authenticated = client.get("/v1/status", headers=authorization())
        assert health.status_code == 200
        assert unauthenticated.status_code == 401
        assert authenticated.status_code == 200
    finally:
        server.should_exit = True
        worker.join(timeout=5)
        listener.close()

    assert not worker.is_alive()
    assert app.state.application.status is AppStatus.STOPPED
