"""Pinned isolated 220 V outlet test; never writes the school source RVT."""

from __future__ import annotations

import hashlib
import json
import os
import shutil
import time
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path

from agent.exceptions import RevitIntegrationError
from integrations.revit_request_bridge import default_bridge_root


ACTION = "execute_school_outlet_220v_isolated_test"
SOURCE_TITLE = "School-Electrical-V5-Classroom1501-Marks-Corrected-20261101"
SOURCE_SHA = "DABF745E7E00D9B5526043414F7B7DC3906D6B43CA2310EDFA718D744F80544F"
FAMILY_SHA = "3DF6502CDD532D4A95C57ED564AB748C8F715CEF49F3C1753BC235512443B0D0"
FAMILY_PATH = Path(os.environ.get("LOCALAPPDATA", "")) / "AI-Engineer" / "revit" / "family-intake" / (
    "3DF6502CDD532D4A95C57ED564AB748C8F715CEF49F3C1753BC235512443B0D0.rfa"
)
TEMPLATE_PATH = Path(r"C:\ProgramData\Autodesk\RVT 2024\Templates\English\Electrical-Default_Metric.rte")
TEMPLATE_SHA = "70EBDD11E857D45597E726C83F5C62FF06B9E392FB8AC958C8FCDC0CDF90CA11"
OUTPUT_PATH = Path(r"R:\workspace\revit-school\scratch\School-Outlet-220V-Circuit-Test-20261101.rvt")


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def prepare_preview(*, bridge_root: Path | None = None, evidence_dir: Path) -> tuple[dict, Path, str]:
    root = (bridge_root or default_bridge_root()).expanduser().resolve(strict=False)
    if (file_sha256(FAMILY_PATH) != FAMILY_SHA or file_sha256(TEMPLATE_PATH) != TEMPLATE_SHA
            or OUTPUT_PATH.exists()):
        raise RevitIntegrationError("School outlet family/template changed or isolated output exists.")
    preview = {
        "schema_version": 1, "preview_id": str(uuid.uuid4()),
        "expires_utc": (datetime.now(timezone.utc) + timedelta(hours=12)).isoformat(),
        "action": ACTION, "source_sha256": SOURCE_SHA,
        "family_sha256": FAMILY_SHA, "output_path": str(OUTPUT_PATH),
        "family_path": str(FAMILY_PATH), "type_name": "Standard",
        "template_path": str(TEMPLATE_PATH), "template_sha256": TEMPLATE_SHA,
        "requested_voltage_v": 220, "requested_load_va": 180,
    }
    approved = root / "approved-previews"
    approved.mkdir(parents=True, exist_ok=True)
    path = approved / f"{preview['preview_id']}.json"
    path.write_text(json.dumps(preview, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    sha = file_sha256(path)
    evidence_dir.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(path, evidence_dir / f"School-Outlet-220V-Isolated-Preview-{preview['preview_id']}.json")
    return preview, path, sha


def run_isolated_test(preview: dict, preview_file: Path, preview_file_sha: str,
                      *, bridge_root: Path | None = None, evidence_dir: Path) -> dict:
    root = (bridge_root or default_bridge_root()).expanduser().resolve(strict=False)
    if (file_sha256(preview_file) != preview_file_sha or preview.get("action") != ACTION
            or preview.get("source_sha256") != SOURCE_SHA or OUTPUT_PATH.exists()):
        raise RevitIntegrationError("School outlet isolated preview or output changed.")
    requests = root / "requests"
    responses = root / "responses"
    requests.mkdir(parents=True, exist_ok=True)
    responses.mkdir(parents=True, exist_ok=True)
    if (requests / "pending.json").exists():
        raise RevitIntegrationError("A Revit request is already pending.")
    request_id = str(uuid.uuid4())
    request = {
        "schema_version": 1, "request_id": request_id, "action": ACTION,
        "expected_document_title": SOURCE_TITLE,
        "provider_egress_permitted": False, "document_mutation_permitted": True,
        "approval_granted": True, "preview_id": preview["preview_id"],
        "preview_sha256": preview_file_sha,
        "preview_file_sha256": preview_file_sha, "source_sha256": SOURCE_SHA,
        "save_allowed": True, "output_path": str(OUTPUT_PATH),
    }
    temporary = requests / f".{request_id}.tmp"
    temporary.write_text(json.dumps(request), encoding="utf-8")
    temporary.replace(requests / "pending.json")
    response_path = responses / f"{request_id}.json"
    deadline = time.monotonic() + 180
    while not response_path.exists():
        if time.monotonic() >= deadline:
            raise RevitIntegrationError("Timed out waiting for isolated outlet Revit result.")
        time.sleep(0.1)
    if response_path.stat().st_size > 65536:
        raise RevitIntegrationError("Isolated outlet Revit response is too large.")
    response = json.loads(response_path.read_text(encoding="utf-8-sig"))
    if (response.get("request_id") != request_id or response.get("action") != ACTION
            or response.get("provider_egress_performed") is not False):
        raise RevitIntegrationError("Isolated outlet Revit response identity failed.")
    if response.get("status") != "completed":
        raise RevitIntegrationError(f"School outlet isolated Revit test failed: {response.get('error')}")
    if response.get("document_mutation_performed") is not True:
        raise RevitIntegrationError("Isolated outlet test did not report new-model creation.")
    report_path = (root / response["output_file"]).resolve(strict=True)
    if not report_path.is_relative_to(root.resolve(strict=True)) or report_path.stat().st_size > 65536:
        raise RevitIntegrationError("School outlet isolated test report escaped its root.")
    report = json.loads(report_path.read_text(encoding="utf-8-sig"))
    if (report.get("schema_version") != 1 or report.get("request_id") != request_id
            or report.get("source_sha256") != SOURCE_SHA
            or report.get("family_sha256") != FAMILY_SHA
            or report.get("output_path") != str(OUTPUT_PATH)
            or report.get("electrical_connector_count") != 1
            or report.get("host_face_present") is not True
            or "PowerCircuit" not in report.get("electrical_system_types", [])
            or report.get("new_document_saved") is not True
            or report.get("source_document_mutation_performed") is not False
            or report.get("provider_egress_performed") is not False
            or file_sha256(OUTPUT_PATH) != report.get("output_sha256")):
        raise RevitIntegrationError("School outlet isolated test did not verify its output.")
    evidence_dir.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(report_path, evidence_dir / "School-Outlet-220V-Isolated-Test-Result-20261101.json")
    return report
