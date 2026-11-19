"""Build a source-bound MEP quantity inventory from paged Revit category evidence."""

from __future__ import annotations

import hashlib
import json
import uuid
from collections import defaultdict
from datetime import UTC, datetime
from pathlib import Path
from typing import Sequence

from agent.exceptions import RevitIntegrationError
from integrations.revit_category_query import RevitCategoryQuery, load_category_query


MEP_QUANTITY_CATEGORIES = (
    "electrical_fixtures",
    "lighting_devices",
    "electrical_equipment",
    "conduit",
    "conduit_fittings",
    "cable_tray",
    "cable_tray_fittings",
)


def create_mep_project_inventory(
    *,
    workspace_root: Path,
    bridge_root: Path,
    source_model: Path,
    page_paths: Sequence[Path],
    output_path: Path,
) -> dict[str, object]:
    """Validate category page chains and atomically write one MEP inventory."""
    workspace = workspace_root.expanduser().resolve(strict=True)
    bridge = bridge_root.expanduser().resolve(strict=True)
    source = source_model.expanduser().resolve(strict=True)
    output = output_path.expanduser().resolve(strict=False)
    if (
        not source.is_file()
        or source.is_symlink()
        or not source.is_relative_to(workspace)
        or source.suffix.lower() != ".rvt"
        or output.exists()
        or output.suffix.lower() != ".json"
        or not output.parent.resolve(strict=True).is_relative_to(workspace)
        or not 1 <= len(page_paths) <= 4096
    ):
        raise RevitIntegrationError("Revit MEP project inventory paths are invalid.")
    grouped: dict[str, list[RevitCategoryQuery]] = defaultdict(list)
    for path in page_paths:
        page = load_category_query(path, allowed_root=bridge)
        grouped[page.category_key].append(page)
    if set(grouped) != set(MEP_QUANTITY_CATEGORIES):
        raise RevitIntegrationError("Revit MEP inventory category coverage is incomplete.")
    report = merge_mep_category_pages(grouped)
    if report["document_title"] != source.stem:
        raise RevitIntegrationError("Revit MEP pages do not match the source model title.")
    report.update({
        "inventory_id": str(uuid.uuid4()),
        "created_utc": datetime.now(UTC).isoformat().replace("+00:00", "Z"),
        "source_model": source.name,
        "source_rvt_sha256": _sha256_file(source),
    })
    report["inventory_sha256"] = _self_hash(report)
    temporary = output.with_name(f".{output.name}.{uuid.uuid4().hex}.tmp")
    try:
        temporary.write_text(
            json.dumps(report, ensure_ascii=False, sort_keys=True, indent=2) + "\n",
            encoding="utf-8",
        )
        temporary.replace(output)
    finally:
        if temporary.exists():
            temporary.unlink()
    return report


def merge_mep_category_pages(
    grouped_pages: dict[str, Sequence[RevitCategoryQuery]],
) -> dict[str, object]:
    """Validate and aggregate complete ordered page chains for each MEP category."""
    if set(grouped_pages) != set(MEP_QUANTITY_CATEGORIES):
        raise RevitIntegrationError("Revit MEP category set is invalid.")
    title = ""
    categories: list[dict[str, object]] = []
    quantity_rows: list[dict[str, object]] = []
    all_elements: dict[str, list[dict[str, object]]] = {}
    for category in MEP_QUANTITY_CATEGORIES:
        pages = sorted(grouped_pages[category], key=lambda item: item.start_after_element_id)
        elements, page_ids = _merge_category_chain(category, pages)
        page_title = pages[0].document_title
        if title and page_title != title:
            raise RevitIntegrationError("Revit MEP category pages use different documents.")
        title = page_title
        all_elements[category] = elements
        unresolved_hosts = sum(
            1 for element in elements
            if isinstance(element.get("host"), dict)
            and element["host"].get("is_linked") is True
            and not element["host"].get("linked_document_title")
        )
        missing_lengths = sum(
            1 for element in elements
            if category in {"conduit", "cable_tray"}
            and element.get("curve_length_mm") is None
        )
        categories.append({
            "category_key": category,
            "page_count": len(pages),
            "request_ids": page_ids,
            "element_count": len(elements),
            "unresolved_linked_host_count": unresolved_hosts,
            "missing_curve_length_count": missing_lengths,
            "modelled_curve_length_m": round(sum(
                float(element.get("curve_length_mm") or 0.0) for element in elements
            ) / 1000.0, 6),
        })
        groups: dict[tuple[object, ...], dict[str, object]] = {}
        for element in elements:
            key = (
                str(element.get("family_name", "")),
                str(element.get("type_name", "")),
                element.get("diameter_mm"),
                element.get("width_mm"),
                element.get("height_mm"),
            )
            row = groups.setdefault(key, {
                "category_key": category,
                "family_name": key[0],
                "type_name": key[1],
                "diameter_mm": key[2],
                "width_mm": key[3],
                "height_mm": key[4],
                "quantity": 0,
                "modelled_curve_length_m": 0.0,
            })
            row["quantity"] = int(row["quantity"]) + 1
            row["modelled_curve_length_m"] = float(row["modelled_curve_length_m"]) + (
                float(element.get("curve_length_mm") or 0.0) / 1000.0
            )
        for row in groups.values():
            row["modelled_curve_length_m"] = round(
                float(row["modelled_curve_length_m"]), 6
            )
            quantity_rows.append(row)
    return {
        "schema_version": 1,
        "status": "complete_read_only_mep_inventory",
        "document_title": title,
        "categories": categories,
        "quantity_rows": sorted(
            quantity_rows,
            key=lambda row: (
                MEP_QUANTITY_CATEGORIES.index(str(row["category_key"])),
                str(row["family_name"]),
                str(row["type_name"]),
                float(row["diameter_mm"] or 0.0),
            ),
        ),
        "elements": all_elements,
        "quantity_limitations": [
            "Conduit and cable-tray lengths are modelled Revit curve lengths, not verified installed quantities.",
            "Fitting quantities are Revit category instances; accessory and box classification remains family/type evidence.",
            "Linked hosts that are unavailable remain counted and explicitly unresolved.",
            "Allowances, bends represented only by geometry, supports, terminations, slack, and waste require reviewed rules.",
        ],
        "provider_egress_performed": False,
        "document_mutation_performed": False,
    }


def _merge_category_chain(
    category: str, pages: Sequence[RevitCategoryQuery]
) -> tuple[list[dict[str, object]], list[str]]:
    if not pages or pages[0].start_after_element_id != 0:
        raise RevitIntegrationError(f"Revit {category} page chain has no first page.")
    expected_cursor = 0
    expected_total = pages[0].total_count
    title = pages[0].document_title
    elements: list[dict[str, object]] = []
    identities: set[int] = set()
    requests: list[str] = []
    for index, page in enumerate(pages):
        if (
            page.category_key != category
            or page.document_title != title
            or page.host_filter != "all"
            or page.total_count != expected_total
            or page.start_after_element_id != expected_cursor
        ):
            raise RevitIntegrationError(f"Revit {category} page chain is inconsistent.")
        if index < len(pages) - 1 and not page.truncated:
            raise RevitIntegrationError(f"Revit {category} page chain ended early.")
        if index == len(pages) - 1 and page.truncated:
            raise RevitIntegrationError(f"Revit {category} page chain is incomplete.")
        for element in page.elements:
            identity = int(element["element_id"])
            if identity in identities:
                raise RevitIntegrationError(f"Revit {category} pages overlap.")
            identities.add(identity)
            elements.append(element)
        requests.append(page.request_id)
        expected_cursor = page.next_start_after_element_id
    if len(elements) != expected_total or expected_cursor != 0:
        raise RevitIntegrationError(f"Revit {category} page chain count is incomplete.")
    return elements, requests


def _canonical(value: object) -> str:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"))


def _self_hash(value: dict[str, object]) -> str:
    return hashlib.sha256(_canonical(value).encode("utf-8")).hexdigest().upper()


def _sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while block := stream.read(65_536):
            digest.update(block)
    return digest.hexdigest().upper()
