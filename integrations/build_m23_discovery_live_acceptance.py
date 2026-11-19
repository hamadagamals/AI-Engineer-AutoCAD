"""Build the fixed, non-authoritative M23 multi-candidate discovery acceptance request."""

from __future__ import annotations

import hashlib
import json
import os
import tempfile
from datetime import date
from pathlib import Path
from typing import Any

from core.manufacturer_discovery import (
    ManufacturerRequirement,
    RequirementOperator,
    create_discovery_request,
)


def build(workspace_root: Path, *, reviewed_on: str | None = None) -> Path:
    """Create or exactly reuse one self-hashed functional acceptance package."""
    root = workspace_root.resolve(strict=True)
    acceptance_date = reviewed_on or date.today().isoformat()
    try:
        parsed_date = date.fromisoformat(acceptance_date)
    except (TypeError, ValueError) as exc:
        raise RuntimeError("Acceptance reviewed_on must be an ISO calendar date.") from exc
    if parsed_date.isoformat() != acceptance_date:
        raise RuntimeError("Acceptance reviewed_on must be a canonical ISO calendar date.")
    output_root = (
        root
        / ".ai-engineer"
        / "manufacturer"
        / "live-acceptance"
        / "discovery"
        / acceptance_date
    )
    source_path = output_root / "source-evidence.json"
    request_path = output_root / "manufacturer-discovery-request.json"
    manifest_path = output_root / "manifest.json"
    template_source = (
        Path(__file__).resolve().parent
        / "manufacturer_templates"
        / "free-candidates.template.json"
    )
    template_path = (
        root / "workspace" / "manufacturer-evidence" / "free-candidates.template.json"
    )
    if not template_source.is_file() or template_source.is_symlink():
        raise RuntimeError("Packaged free candidate template is missing or unsafe.")
    template_bytes = template_source.read_bytes()
    if not 2 <= len(template_bytes) <= 1_048_576:
        raise RuntimeError("Packaged free candidate template exceeds its safe size.")

    model = {
        "fixture_id": "m23-discovery:modular-socket",
        "project_id": "project:m23-discovery-live-acceptance",
        "scope": "Functional search acceptance only; not an active project design.",
        "load": {"nominal_voltage": "230", "unit": "V", "phase": "single_phase"},
        "circuit": {"design_current": "16", "unit": "A"},
        "device": {"pole_configuration": "2P+E"},
    }
    sources = {
        "model": model,
        "rated_current": {
            "source_id": "calculation:m23-socket-design-current",
            "value": "16", "unit": "A",
            "basis": "Fixed functional-acceptance calculation input; not project authority.",
        },
        "rated_voltage": {
            "source_id": "load:m23-socket",
            "value": "230", "unit": "V",
            "basis": "Fixed functional-acceptance shared-model input.",
        },
        "pole_configuration": {
            "source_id": "input:m23-socket-poles",
            "value": "2P+E", "unit": None,
            "basis": "Fixed engineer-input acceptance requirement.",
        },
    }
    requirements = (
        ManufacturerRequirement(
            "requirement:rated-current", "rated_current", RequirementOperator.MINIMUM,
            "16", "A", "calculation", sources["rated_current"]["source_id"],
            _sha256(sources["rated_current"]), True,
        ),
        ManufacturerRequirement(
            "requirement:rated-voltage", "rated_voltage", RequirementOperator.MINIMUM,
            "230", "V", "shared_model", sources["rated_voltage"]["source_id"],
            _sha256(sources["rated_voltage"]), True,
        ),
        ManufacturerRequirement(
            "requirement:pole-configuration", "pole_configuration", RequirementOperator.EXACT,
            "2P+E", None, "engineer_input", sources["pole_configuration"]["source_id"],
            _sha256(sources["pole_configuration"]), True,
        ),
    )
    request = create_discovery_request(
        request_id="manufacturer-search:m23-modular-socket-live",
        project_id=model["project_id"],
        model_sha256=_sha256(model),
        product_category="DIN-rail modular socket outlet",
        search_text=(
            "Find at least two distinct official manufacturer product candidates for a DIN-rail "
            "modular socket outlet rated at least 16 A and 230 V with 2P+E configuration. "
            "Use exact official product pages and exact stated property values only."
        ),
        official_domains=("www.legrand.com", "www.se.com"),
        requirements=requirements,
        approved_by="engineer:functional-acceptance",
        approved_on=acceptance_date,
    )
    api_request: dict[str, Any] = {
        "request": request.to_dict(),
        "discovery_id": "manufacturer-discovery:m23-modular-socket-live",
        "reviewed_by": "engineer:functional-acceptance",
        "reviewed_on": acceptance_date,
        "assumptions": [
            "Functional multi-candidate acceptance only; no project or procurement approval.",
            "Only independently re-fetched official-page evidence may produce candidate properties.",
        ],
    }
    source_document = {
        "schema_version": 1,
        "fixture_version": "m23.discovery_live_acceptance.v1",
        "reviewed_on": acceptance_date,
        "sources": sources,
    }
    source_bytes = _json_bytes(source_document)
    request_bytes = _json_bytes(api_request)
    manifest = {
        "schema_version": 1,
        "acceptance_version": "m23.discovery_live_acceptance_manifest.v1",
        "source_evidence_sha256": hashlib.sha256(source_bytes).hexdigest(),
        "request_file_sha256": hashlib.sha256(request_bytes).hexdigest(),
        "discovery_request_sha256": request.request_sha256,
        "official_domains": list(request.official_domains),
        "minimum_expected_candidates": 2,
        "provider_egress_approved": True,
        "product_approval_performed": False,
        "automatic_download_performed": False,
        "catalog_registration_performed": False,
        "drawing_mutation_performed": False,
    }
    manifest["manifest_sha256"] = _sha256(manifest)
    _write_or_verify(source_path, source_bytes)
    _write_or_verify(request_path, request_bytes)
    _write_or_verify(manifest_path, _json_bytes(manifest))
    _write_or_verify(template_path, template_bytes)
    return request_path


def _sha256(value: object) -> str:
    return hashlib.sha256(_json_bytes(value, newline=False)).hexdigest()


def _json_bytes(value: object, *, newline: bool = True) -> bytes:
    suffix = "\n" if newline else ""
    return (
        json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")) + suffix
    ).encode("utf-8")


def _write_or_verify(path: Path, content: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.exists():
        if path.is_symlink() or not path.is_file() or path.read_bytes() != content:
            raise RuntimeError(f"Existing acceptance artifact differs and was not overwritten: {path}")
        return
    descriptor, temporary_name = tempfile.mkstemp(prefix=f".{path.name}.", dir=path.parent)
    temporary = Path(temporary_name)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
        try:
            os.link(temporary, path)
        except FileExistsError as exc:
            raise RuntimeError(f"Acceptance artifact appeared concurrently: {path}") from exc
        temporary.unlink()
    finally:
        if temporary.exists():
            temporary.unlink()


if __name__ == "__main__":
    import argparse

    parser = argparse.ArgumentParser()
    parser.add_argument("workspace_root", type=Path)
    arguments = parser.parse_args()
    print(build(arguments.workspace_root))
