"""Bounded read-only connector intake for the 16 existing school outlets."""

from __future__ import annotations

import json
import math
import uuid
from pathlib import Path

from agent.exceptions import RevitIntegrationError
from integrations.revit_request_bridge import default_bridge_root, wait_for_response


ACTION = "school_v5_outlet_connector_query"


def submit_outlet_connector_query(
    document_title: str, source_sha256: str, *, bridge_root: Path | None = None
) -> tuple[str, Path]:
    if (not document_title or len(document_title) > 260
            or len(source_sha256) != 64
            or any(c not in "0123456789abcdefABCDEF" for c in source_sha256)):
        raise RevitIntegrationError("School outlet connector query identity is invalid.")
    root = (bridge_root or default_bridge_root()).expanduser().resolve(strict=False)
    requests = root / "requests"
    responses = root / "responses"
    requests.mkdir(parents=True, exist_ok=True)
    responses.mkdir(parents=True, exist_ok=True)
    pending = requests / "pending.json"
    if pending.exists():
        raise RevitIntegrationError("A Revit request is already pending.")
    request_id = str(uuid.uuid4())
    payload = {
        "schema_version": 1, "request_id": request_id, "action": ACTION,
        "expected_document_title": document_title,
        "source_sha256": source_sha256.upper(),
        "provider_egress_permitted": False, "document_mutation_permitted": False,
    }
    temporary = requests / f".{request_id}.tmp"
    temporary.write_text(json.dumps(payload), encoding="utf-8")
    temporary.replace(pending)
    return request_id, responses / f"{request_id}.json"


def load_outlet_connector_query(
    path: Path, *, allowed_root: Path, request_id: str,
    document_title: str, source_sha256: str,
) -> dict[str, object]:
    root = allowed_root.expanduser().resolve(strict=True)
    candidate = path.expanduser().resolve(strict=True)
    if (not candidate.is_file() or candidate.is_symlink()
            or not candidate.is_relative_to(root) or candidate.stat().st_size > 131_072):
        raise RevitIntegrationError("School outlet connector output escaped its bounded root.")
    try:
        report = json.loads(candidate.read_text(encoding="utf-8-sig"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise RevitIntegrationError("School outlet connector output is invalid JSON.") from exc
    if (not isinstance(report, dict) or report.get("schema_version") != 1
            or report.get("request_id") != request_id
            or report.get("document_title") != document_title
            or report.get("document_sha256") != source_sha256.upper()
            or report.get("total_count") != 16
            or report.get("provider_egress_performed") is not False
            or report.get("document_mutation_performed") is not False):
        raise RevitIntegrationError("School outlet connector identity or read-only boundary failed.")
    outlets = report.get("outlets")
    if not isinstance(outlets, list) or len(outlets) != 16:
        raise RevitIntegrationError("School outlet connector count differs from 16.")
    ids: set[int] = set()
    for outlet in outlets:
        if not isinstance(outlet, dict):
            raise RevitIntegrationError("School outlet connector record is invalid.")
        element_id = outlet.get("element_id")
        if (not isinstance(element_id, int) or isinstance(element_id, bool)
                or element_id <= 0 or element_id in ids):
            raise RevitIntegrationError("School outlet connector element identity is invalid.")
        ids.add(element_id)
        if (outlet.get("family_name") not in {"Outlet-Duplex", "Outlet-GFI"}
                or outlet.get("host_category") != "Walls"
                or not isinstance(outlet.get("host_id"), int)
                or outlet["host_id"] <= 0):
            raise RevitIntegrationError("School outlet type or wall host differs.")
        for key in ("voltage_v", "apparent_load_va"):
            value = outlet.get(key)
            if value is not None and (not isinstance(value, (int, float))
                                      or isinstance(value, bool) or not math.isfinite(value)
                                      or value < 0 or value > 1_000_000):
                raise RevitIntegrationError(f"School outlet {key} is invalid.")
        connectors = outlet.get("connectors")
        parameters = outlet.get("electrical_parameters")
        if (not isinstance(connectors, list) or len(connectors) > 8
                or not isinstance(parameters, list) or len(parameters) > 32):
            raise RevitIntegrationError("School outlet electrical detail bounds failed.")
        for connector in connectors:
            if (not isinstance(connector, dict)
                    or not isinstance(connector.get("id"), int)
                    or connector.get("domain") not in {"DomainElectrical", "DomainPiping", "DomainHvac", "DomainUndefined"}
                    or not isinstance(connector.get("electrical_system_type"), str)
                    or not isinstance(connector.get("assigned_system_id"), int)):
                raise RevitIntegrationError("School outlet connector detail is invalid.")
        for parameter in parameters:
            if (not isinstance(parameter, dict)
                    or parameter.get("source") not in {"instance", "type"}
                    or not all(isinstance(parameter.get(key), str) and len(parameter[key]) <= 260
                               for key in ("name", "value"))):
                raise RevitIntegrationError("School outlet parameter detail is invalid.")
    return report


def capture_live_outlet_connectors(
    *, document_title: str, source_sha256: str, output_report: Path,
    bridge_root: Path | None = None,
) -> dict[str, object]:
    root = (bridge_root or default_bridge_root()).expanduser().resolve(strict=False)
    request_id, response_path = submit_outlet_connector_query(
        document_title, source_sha256, bridge_root=root)
    response = wait_for_response(request_id, response_path, timeout_seconds=90,
                                 expected_action=ACTION)
    if response.status != "completed":
        raise RevitIntegrationError(f"School outlet connector query failed: {response.error}")
    report = load_outlet_connector_query(
        root / response.output_file, allowed_root=root, request_id=request_id,
        document_title=document_title, source_sha256=source_sha256)
    output = output_report.expanduser().resolve(strict=False)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return report
