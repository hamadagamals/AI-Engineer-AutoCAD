"""Interactive reviewed console for official manufacturer asset collection."""

from __future__ import annotations

import hashlib
import json
import os
import tempfile
import uuid
from datetime import date
from pathlib import Path

from agent.exceptions import AIEngineerError, ManufacturerEvidenceError
from core.manufacturer_asset_collection import (
    ManufacturerAssetKind,
    approve_asset_collection,
    collect_approved_assets,
    create_asset_search_request,
    create_asset_search_result,
)
from core.manufacturer_discovery import (
    ManufacturerRequirement,
    RequirementOperator,
    compare_candidates,
    create_discovery_request,
)
from integrations.openai_manufacturer_search import create_openai_manufacturer_provider
from integrations.openai_provider import OpenAIProviderConfig


_KIND_ALIASES = {
    "pdf": ManufacturerAssetKind.DATA_SHEET,
    "datasheet": ManufacturerAssetKind.DATA_SHEET,
    "dwg": ManufacturerAssetKind.CAD_2D,
    "dxf": ManufacturerAssetKind.CAD_2D,
    "2d": ManufacturerAssetKind.CAD_2D,
    "step": ManufacturerAssetKind.CAD_3D,
    "stp": ManufacturerAssetKind.CAD_3D,
    "iges": ManufacturerAssetKind.CAD_3D,
    "3d": ManufacturerAssetKind.CAD_3D,
    "image": ManufacturerAssetKind.IMAGE,
    "photo": ManufacturerAssetKind.IMAGE,
}


def main() -> int:
    """Run a two-review search/download flow without storing provider credentials."""
    root = Path(__file__).resolve().parents[1]
    try:
        manufacturer = _prompt("Manufacturer / الشركة المصنعة: ")
        part_number = _prompt("Exact part number / البارت نمبر: ")
        domains = _domains(_prompt(
            "Official domains, comma separated / الدومينات الرسمية: "
        ))
        kinds = _kinds(_prompt(
            "Required order (pdf,2d,3d,image) / الترتيب المطلوب: "
        ))
        operator = _prompt("Engineer name / اسم المراجع: ")
        if input("Type SEARCH to approve official web search: ").strip() != "SEARCH":
            raise ManufacturerEvidenceError("Official manufacturer search was not approved.")

        today = date.today().isoformat()
        run_id = uuid.uuid4().hex[:12]
        provider_environment = dict(os.environ)
        provider_environment["AI_ENGINEER_OPENAI_ENABLE"] = "1"
        provider_environment["AI_ENGINEER_OPENAI_EGRESS_CONFIRMED"] = "1"
        provider = create_openai_manufacturer_provider(
            OpenAIProviderConfig.from_environment(provider_environment)
        )
        input_hash = hashlib.sha256(
            f"{manufacturer}\n{part_number}\n{','.join(domains)}".encode("utf-8")
        ).hexdigest()
        requirement = ManufacturerRequirement(
            f"requirement:part-number:{run_id}", "part_number", RequirementOperator.EXACT,
            part_number, None, "engineer_input", f"input:part-number:{run_id}",
            input_hash, True,
        )
        product_request = create_discovery_request(
            request_id=f"manufacturer-search:{run_id}", project_id="project:asset-collector",
            model_sha256=input_hash, product_category="exact manufacturer part number",
            search_text=f"{manufacturer} exact part number {part_number}",
            official_domains=domains, requirements=(requirement,), approved_by=operator,
            approved_on=today,
        )
        products = provider.discover(product_request)
        product_result = compare_candidates(
            product_request, products, discovery_id=f"manufacturer-discovery:{run_id}",
            provider_name=provider.name, reviewed_by=operator, reviewed_on=today,
            assumptions=("Exact part-number official-domain product discovery.",),
        )
        print("\nPRODUCT CANDIDATES")
        for index, candidate in enumerate(product_result.candidates, start=1):
            print(f"{index}. {candidate.manufacturer_name} | {candidate.part_number} | {candidate.product_url}")
        selected_index = _index(
            input("Choose product number / اختر رقم المنتج: "),
            len(product_result.candidates),
        )
        product = product_result.candidates[selected_index]

        asset_request = create_asset_search_request(
            request_id=f"asset-search:{run_id}", project_id="project:asset-collector",
            manufacturer_name=product.manufacturer_name, part_number=product.part_number,
            product_page_url=product.product_url,
            product_evidence_sha256=product.source_content_sha256,
            official_domains=domains, requested_kinds=kinds, approved_by=operator,
            approved_on=today, egress_approved=True,
        )
        assets = provider.discover_assets(asset_request)
        asset_result = create_asset_search_result(
            asset_request, assets, result_id=f"asset-result:{run_id}",
            provider_name=provider.name,
        )
        print("\nASSET CANDIDATES (no files downloaded yet)")
        for index, candidate in enumerate(asset_result.candidates, start=1):
            print(f"{index}. {candidate.kind.value} | {candidate.media_type} | {candidate.locator}")
        choices = _indices(
            input("Choose ordered asset numbers, e.g. 1,3,2 / اختر بالترتيب: "),
            len(asset_result.candidates),
        )
        if input("Type DOWNLOAD to approve these exact URLs: ").strip() != "DOWNLOAD":
            raise ManufacturerEvidenceError("Manufacturer asset download was not approved.")
        selected_ids = tuple(asset_result.candidates[index].candidate_id for index in choices)
        approval = approve_asset_collection(
            asset_result, approval_id=f"asset-approval:{run_id}",
            selected_candidate_ids=selected_ids, approved_by=operator, approved_on=today,
        )
        collection = collect_approved_assets(
            asset_request, asset_result, approval, root / "workspace" / "manufacturer-assets",
            retrieved_on=today,
        )
        evidence_root = (
            root / "workspace" / "manufacturer-assets" / collection.library_relative_root
        )
        evidence = evidence_root / "collection-evidence.json"
        _write_json(evidence, {
            "schema_version": 1, "product_request": product_request.to_dict(),
            "product_result": product_result.to_dict(), "asset_request": asset_request.to_dict(),
            "asset_result": asset_result.to_dict(), "approval": approval.to_dict(),
            "collection": collection.to_dict(),
        })
        print(f"\nComplete: {len(collection.records)} file(s)")
        print(f"Library: {evidence_root}")
        print(f"Evidence: {evidence}")
        print("Catalog registration=false | drawing mutation=false")
        return 0
    except (AIEngineerError, OSError, ValueError) as exc:
        print(f"ERROR: {exc}")
        return 1


def _prompt(message: str) -> str:
    value = input(message).strip()
    if not value:
        raise ManufacturerEvidenceError("A required manufacturer asset field is empty.")
    return value


def _domains(value: str) -> tuple[str, ...]:
    result = tuple(item.strip().lower() for item in value.split(",") if item.strip())
    if not result:
        raise ManufacturerEvidenceError("At least one official domain is required.")
    return result


def _kinds(value: str) -> tuple[ManufacturerAssetKind, ...]:
    result: list[ManufacturerAssetKind] = []
    for raw in value.split(","):
        kind = _KIND_ALIASES.get(raw.strip().casefold())
        if kind is None:
            raise ManufacturerEvidenceError(f"Unsupported requested asset kind: {raw.strip()}.")
        if kind not in result:
            result.append(kind)
    if not result:
        raise ManufacturerEvidenceError("At least one asset kind is required.")
    return tuple(result)


def _index(value: str, count: int) -> int:
    index = int(value.strip()) - 1
    if index < 0 or index >= count:
        raise ManufacturerEvidenceError("Selected product number is outside the candidate list.")
    return index


def _indices(value: str, count: int) -> tuple[int, ...]:
    indices = tuple(_index(item, count) for item in value.split(","))
    if not indices or len(indices) != len(set(indices)):
        raise ManufacturerEvidenceError("Selected asset numbers must be a unique ordered list.")
    return indices


def _write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.exists():
        raise ManufacturerEvidenceError("Collection evidence already exists and cannot be overwritten.")
    encoded = json.dumps(value, ensure_ascii=False, sort_keys=True, indent=2).encode("utf-8") + b"\n"
    descriptor, temporary_name = tempfile.mkstemp(dir=path.parent, prefix=f".{path.name}.")
    temporary = Path(temporary_name)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(encoded)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        if temporary.exists():
            temporary.unlink()


if __name__ == "__main__":
    raise SystemExit(main())
