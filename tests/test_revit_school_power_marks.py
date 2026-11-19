"""Reject incomplete or unsafe V5 power marker extraction pages."""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from agent.exceptions import RevitIntegrationError
from integrations.revit_school_power_marks import (
    EXPECTED,
    audit_existing_outlet_proximity,
    load_power_mark_page,
    reconcile_power_mark_pages,
)


SOURCE_HASH = "A" * 64


def _page(first: int, kinds: list[str], *, truncated: bool) -> dict[str, object]:
    marks = []
    for offset, kind in enumerate(kinds):
        mark_id = first + offset * 20
        segment_count = {"DUPLEX": 10, "GFI": 6, "DEDICATED": 4, "PANEL": 6}[kind]
        marks.append({
            "first_curve_id": mark_id, "curve_ids": list(range(mark_id, mark_id + segment_count)),
            "kind": kind, "view_id": 101, "level_id": 30, "x_mm": 1000.0 + offset,
            "y_mm": 2000.0, "room_ids": [] if kind == "PANEL" else [221095],
        })
    return {
        "schema_version": 1, "document_sha256": SOURCE_HASH, "total_count": 527,
        "counts_by_kind": EXPECTED, "start_after_element_id": first - 1,
        "next_start_after_element_id": marks[-1]["first_curve_id"] if truncated else 0,
        "truncated": truncated, "returned_count": len(marks), "marks": marks,
        "provider_egress_performed": False, "document_mutation_performed": False,
    }


def test_page_requires_exact_cursor_geometry_and_read_only_flag(tmp_path: Path) -> None:
    page = _page(101, ["DUPLEX", "GFI"], truncated=True)
    path = tmp_path / "page.json"
    path.write_text(json.dumps(page), encoding="utf-8")
    assert load_power_mark_page(path, expected_sha256=SOURCE_HASH, expected_cursor=100)["returned_count"] == 2
    page["marks"][0]["curve_ids"] = [101]
    path.write_text(json.dumps(page), encoding="utf-8")
    with pytest.raises(RevitIntegrationError, match="geometry"):
        load_power_mark_page(path, expected_sha256=SOURCE_HASH, expected_cursor=100)
    page = _page(101, ["DUPLEX"], truncated=False)
    page["document_mutation_performed"] = True
    path.write_text(json.dumps(page), encoding="utf-8")
    with pytest.raises(RevitIntegrationError, match="contract"):
        load_power_mark_page(path, expected_sha256=SOURCE_HASH, expected_cursor=100)


def test_reconcile_requires_complete_unique_527_mark_sequence() -> None:
    kinds = [kind for kind, count in EXPECTED.items() for _ in range(count)]
    pages = []
    cursor = 0
    for start in range(0, len(kinds), 32):
        batch = kinds[start:start + 32]
        page = _page(cursor + 1, batch, truncated=start + 32 < len(kinds))
        pages.append(page)
        cursor = page["next_start_after_element_id"]
    assert reconcile_power_mark_pages(pages)["total_count"] == 527
    with pytest.raises(RevitIntegrationError, match="incomplete"):
        reconcile_power_mark_pages(pages[:-1])
    pages[-1]["marks"][0]["first_curve_id"] = pages[-2]["marks"][-1]["first_curve_id"]
    with pytest.raises(RevitIntegrationError):
        reconcile_power_mark_pages(pages)


def test_existing_outlet_audit_flags_proximity_without_deleting_marks() -> None:
    marks = ([{"kind": "DUPLEX", "level_id": 30, "first_curve_id": index + 1,
               "x_mm": float(index * 2000), "y_mm": 0.0} for index in range(426)]
             + [{"kind": "GFI", "level_id": 30, "first_curve_id": index + 500,
                 "x_mm": float(index * 2000), "y_mm": 5000.0} for index in range(21)]
             + [{"kind": "DEDICATED", "level_id": 30, "first_curve_id": index + 600,
                 "x_mm": float(index * 2000), "y_mm": 10000.0} for index in range(78)]
             + [{"kind": "PANEL", "level_id": 30, "first_curve_id": index + 800,
                 "x_mm": 0.0, "y_mm": 0.0} for index in range(2)])
    existing = [{"element_id": index + 100, "family_name": "Outlet-Duplex", "level_id": 30,
                 "location_point": {"x_mm": 500.0 if index == 0 else 900000.0 + index,
                                    "y_mm": 0.0}} for index in range(16)]
    report = audit_existing_outlet_proximity(
        {"total_count": 527, "source_sha256": SOURCE_HASH, "marks": marks},
        {"category_key": "electrical_fixtures", "total_count": 16,
         "returned_count": 16, "elements": existing},
    )
    assert report["new_review_candidate_count"] == 525
    assert report["possible_duplicate_count"] == 1
    assert report["automatic_removal_performed"] is False
    assert report["outlets"][0]["nearest_candidate_curve_id"] == 1
