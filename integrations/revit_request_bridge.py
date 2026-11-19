"""Strict local request/response boundary for chat-driven read-only Revit actions."""

from __future__ import annotations

import json
import os
import time
import uuid
from dataclasses import dataclass
from pathlib import Path

from agent.exceptions import RevitIntegrationError


@dataclass(frozen=True, slots=True)
class RevitBridgeResponse:
    request_id: str
    action: str
    status: str
    document_title: str
    output_file: str
    error: str


def default_bridge_root() -> Path:
    value = os.environ.get("LOCALAPPDATA")
    if not value:
        raise RevitIntegrationError("LOCALAPPDATA is required for the Revit bridge.")
    return Path(value) / "AI-Engineer" / "revit"


def submit_inventory_request(
    expected_document_title: str, *, bridge_root: Path | None = None
) -> tuple[str, Path]:
    """Atomically submit one fixed inventory request for the exact active document."""
    title = expected_document_title.strip()
    if not title or len(title) > 260:
        raise RevitIntegrationError("Expected Revit document title is invalid.")
    root = (bridge_root or default_bridge_root()).expanduser().resolve(strict=False)
    requests = root / "requests"
    responses = root / "responses"
    requests.mkdir(parents=True, exist_ok=True)
    responses.mkdir(parents=True, exist_ok=True)
    pending = requests / "pending.json"
    if pending.exists():
        raise RevitIntegrationError("A Revit read-only request is already pending.")
    request_id = str(uuid.uuid4())
    payload = {
        "schema_version": 1,
        "request_id": request_id,
        "action": "inventory",
        "expected_document_title": title,
        "provider_egress_permitted": False,
        "document_mutation_permitted": False,
    }
    temporary = requests / f".{request_id}.tmp"
    temporary.write_text(json.dumps(payload, ensure_ascii=False), encoding="utf-8")
    temporary.replace(pending)
    return request_id, responses / f"{request_id}.json"


def submit_category_request(
    expected_document_title: str,
    category_key: str,
    *,
    maximum_elements: int = 32,
    hosted_only: bool = False,
    ceiling_hosted_only: bool = False,
    start_after_element_id: int = 0,
    bridge_root: Path | None = None,
) -> tuple[str, Path]:
    from integrations.revit_category_query import ALLOWED_CATEGORIES

    title = expected_document_title.strip()
    if not title or len(title) > 260 or category_key not in ALLOWED_CATEGORIES:
        raise RevitIntegrationError("Revit category request is invalid.")
    if not isinstance(maximum_elements, int) or not 1 <= maximum_elements <= 32:
        raise RevitIntegrationError("Revit category limit must be within 1..32.")
    if (
        not isinstance(start_after_element_id, int)
        or isinstance(start_after_element_id, bool)
        or start_after_element_id < 0
    ):
        raise RevitIntegrationError("Revit category cursor is invalid.")
    if not isinstance(hosted_only, bool) or not isinstance(ceiling_hosted_only, bool):
        raise RevitIntegrationError("Revit category host filter is invalid.")
    if hosted_only and ceiling_hosted_only:
        raise RevitIntegrationError("Revit category host filters are mutually exclusive.")
    root = (bridge_root or default_bridge_root()).expanduser().resolve(strict=False)
    requests = root / "requests"
    responses = root / "responses"
    requests.mkdir(parents=True, exist_ok=True)
    responses.mkdir(parents=True, exist_ok=True)
    pending = requests / "pending.json"
    if pending.exists():
        raise RevitIntegrationError("A Revit read-only request is already pending.")
    request_id = str(uuid.uuid4())
    payload = {
        "schema_version": 1,
        "request_id": request_id,
        "action": "category_query",
        "expected_document_title": title,
        "provider_egress_permitted": False,
        "document_mutation_permitted": False,
        "category_key": category_key,
        "max_elements": maximum_elements,
        "start_after_element_id": start_after_element_id,
        "host_filter": (
            "ceiling_hosted" if ceiling_hosted_only else "hosted" if hosted_only else "all"
        ),
    }
    temporary = requests / f".{request_id}.tmp"
    temporary.write_text(json.dumps(payload, ensure_ascii=False), encoding="utf-8")
    temporary.replace(pending)
    return request_id, responses / f"{request_id}.json"


def wait_for_response(
    request_id: str,
    response_path: Path,
    *,
    timeout_seconds: float = 30.0,
    expected_action: str = "inventory",
) -> RevitBridgeResponse:
    """Wait for and strictly validate one correlated local Revit response."""
    try:
        uuid.UUID(request_id)
    except ValueError as exc:
        raise RevitIntegrationError("Revit request id is invalid.") from exc
    if timeout_seconds <= 0 or timeout_seconds > 300:
        raise RevitIntegrationError("Revit response timeout must be within 0..300 seconds.")
    deadline = time.monotonic() + timeout_seconds
    while not response_path.is_file():
        if time.monotonic() >= deadline:
            raise RevitIntegrationError("Timed out waiting for the Revit read-only response.")
        time.sleep(0.1)
    if response_path.stat().st_size > 65_536:
        raise RevitIntegrationError("Revit response exceeds 64 KiB.")
    try:
        value = json.loads(response_path.read_text(encoding="utf-8-sig"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise RevitIntegrationError(f"Revit response is invalid JSON: {exc}") from exc
    if not isinstance(value, dict) or value.get("schema_version") != 1:
        raise RevitIntegrationError("Revit response schema is invalid.")
    if value.get("request_id") != request_id or value.get("action") != expected_action:
        raise RevitIntegrationError("Revit response correlation is invalid.")
    if value.get("provider_egress_performed") is not False:
        raise RevitIntegrationError("Revit response unexpectedly reports provider egress.")
    if value.get("document_mutation_performed") is not False:
        raise RevitIntegrationError("Revit response unexpectedly reports model mutation.")

    def text(name: str, maximum: int) -> str:
        item = value.get(name)
        if not isinstance(item, str) or len(item) > maximum:
            raise RevitIntegrationError(f"Revit response field {name} is invalid.")
        return item

    status = text("status", 32)
    if status not in {"completed", "failed"}:
        raise RevitIntegrationError("Revit response status is invalid.")
    return RevitBridgeResponse(
        request_id=request_id,
        action=expected_action,
        status=status,
        document_title=text("document_title", 260),
        output_file=text("output_file", 96),
        error=text("error", 500),
    )


def submit_host_face_request(
    expected_document_title: str,
    reference_element_id: int,
    face_stable_reference: str,
    *,
    maximum_candidates: int = 8,
    bridge_root: Path | None = None,
) -> tuple[str, Path]:
    """Submit one bounded read-only query for an exact fixture host face."""
    title = expected_document_title.strip()
    stable = face_stable_reference.strip()
    if (
        not title or len(title) > 260
        or not isinstance(reference_element_id, int)
        or isinstance(reference_element_id, bool)
        or reference_element_id <= 0
        or not stable or len(stable) > 4096
        or not isinstance(maximum_candidates, int)
        or isinstance(maximum_candidates, bool)
        or not 1 <= maximum_candidates <= 8
    ):
        raise RevitIntegrationError("Revit host-face request is invalid.")
    root = (bridge_root or default_bridge_root()).expanduser().resolve(strict=False)
    requests = root / "requests"
    responses = root / "responses"
    requests.mkdir(parents=True, exist_ok=True)
    responses.mkdir(parents=True, exist_ok=True)
    pending = requests / "pending.json"
    if pending.exists():
        raise RevitIntegrationError("A Revit read-only request is already pending.")
    request_id = str(uuid.uuid4())
    payload = {
        "schema_version": 1,
        "request_id": request_id,
        "action": "host_face_query",
        "expected_document_title": title,
        "provider_egress_permitted": False,
        "document_mutation_permitted": False,
        "reference_element_id": reference_element_id,
        "face_stable_reference": stable,
        "max_elements": maximum_candidates,
    }
    temporary = requests / f".{request_id}.tmp"
    temporary.write_text(json.dumps(payload, ensure_ascii=False), encoding="utf-8")
    temporary.replace(pending)
    return request_id, responses / f"{request_id}.json"


def submit_element_visual_request(
    expected_document_title: str,
    element_id: int,
    *,
    bridge_root: Path | None = None,
) -> tuple[str, Path]:
    """Focus and inspect one exact lighting fixture without model mutation."""
    title = expected_document_title.strip()
    if (
        not title
        or len(title) > 260
        or not isinstance(element_id, int)
        or isinstance(element_id, bool)
        or element_id <= 0
    ):
        raise RevitIntegrationError("Revit element visual request is invalid.")
    root = (bridge_root or default_bridge_root()).expanduser().resolve(strict=False)
    requests = root / "requests"
    responses = root / "responses"
    requests.mkdir(parents=True, exist_ok=True)
    responses.mkdir(parents=True, exist_ok=True)
    pending = requests / "pending.json"
    if pending.exists():
        raise RevitIntegrationError("A Revit read-only request is already pending.")
    request_id = str(uuid.uuid4())
    payload = {
        "schema_version": 1,
        "request_id": request_id,
        "action": "element_visual_query",
        "expected_document_title": title,
        "provider_egress_permitted": False,
        "document_mutation_permitted": False,
        "reference_element_id": element_id,
    }
    temporary = requests / f".{request_id}.tmp"
    temporary.write_text(json.dumps(payload, ensure_ascii=False), encoding="utf-8")
    temporary.replace(pending)
    return request_id, responses / f"{request_id}.json"


def submit_lighting_circuit_request(
    expected_document_title: str,
    *,
    maximum_elements: int = 32,
    element_id: int = 0,
    start_after_element_id: int = 0,
    bridge_root: Path | None = None,
) -> tuple[str, Path]:
    """Submit a bounded read-only lighting-fixture electrical-system query."""
    title = expected_document_title.strip()
    if (
        not title
        or len(title) > 260
        or not isinstance(maximum_elements, int)
        or isinstance(maximum_elements, bool)
        or not 1 <= maximum_elements <= 32
        or not isinstance(element_id, int)
        or isinstance(element_id, bool)
        or element_id < 0
        or not isinstance(start_after_element_id, int)
        or isinstance(start_after_element_id, bool)
        or start_after_element_id < 0
        or (element_id > 0 and start_after_element_id > 0)
    ):
        raise RevitIntegrationError("Revit lighting circuit request is invalid.")
    root = (bridge_root or default_bridge_root()).expanduser().resolve(strict=False)
    requests = root / "requests"
    responses = root / "responses"
    requests.mkdir(parents=True, exist_ok=True)
    responses.mkdir(parents=True, exist_ok=True)
    pending = requests / "pending.json"
    if pending.exists():
        raise RevitIntegrationError("A Revit read-only request is already pending.")
    request_id = str(uuid.uuid4())
    payload = {
        "schema_version": 1,
        "request_id": request_id,
        "action": "lighting_circuit_query",
        "expected_document_title": title,
        "provider_egress_permitted": False,
        "document_mutation_permitted": False,
        "max_elements": maximum_elements,
        "reference_element_id": element_id,
        "start_after_element_id": start_after_element_id,
    }
    temporary = requests / f".{request_id}.tmp"
    temporary.write_text(json.dumps(payload, ensure_ascii=False), encoding="utf-8")
    temporary.replace(pending)
    return request_id, responses / f"{request_id}.json"


def submit_electrical_system_request(
    expected_document_title: str,
    *,
    maximum_elements: int = 32,
    start_after_element_id: int = 0,
    bridge_root: Path | None = None,
) -> tuple[str, Path]:
    """Submit one bounded read-only page of Revit electrical systems."""
    title = expected_document_title.strip()
    if (
        not title
        or len(title) > 260
        or not isinstance(maximum_elements, int)
        or isinstance(maximum_elements, bool)
        or not 1 <= maximum_elements <= 32
        or not isinstance(start_after_element_id, int)
        or isinstance(start_after_element_id, bool)
        or start_after_element_id < 0
    ):
        raise RevitIntegrationError("Revit electrical-system request is invalid.")
    root = (bridge_root or default_bridge_root()).expanduser().resolve(strict=False)
    requests = root / "requests"
    responses = root / "responses"
    requests.mkdir(parents=True, exist_ok=True)
    responses.mkdir(parents=True, exist_ok=True)
    pending = requests / "pending.json"
    if pending.exists():
        raise RevitIntegrationError("A Revit read-only request is already pending.")
    request_id = str(uuid.uuid4())
    payload = {
        "schema_version": 1,
        "request_id": request_id,
        "action": "electrical_system_query",
        "expected_document_title": title,
        "provider_egress_permitted": False,
        "document_mutation_permitted": False,
        "max_elements": maximum_elements,
        "start_after_element_id": start_after_element_id,
    }
    temporary = requests / f".{request_id}.tmp"
    temporary.write_text(json.dumps(payload, ensure_ascii=False), encoding="utf-8")
    temporary.replace(pending)
    return request_id, responses / f"{request_id}.json"
