"""Tests for complete paged Revit MEP quantity aggregation."""

from __future__ import annotations

import pytest

from agent.exceptions import RevitIntegrationError
from integrations.revit_category_query import RevitCategoryQuery
from integrations.revit_mep_project_inventory import (
    MEP_QUANTITY_CATEGORIES,
    merge_mep_category_pages,
)


def _page(category: str, element_id: int, **element: object) -> RevitCategoryQuery:
    value = {
        "element_id": element_id,
        "family_name": "Family",
        "type_name": "Type",
        "curve_length_mm": None,
        "diameter_mm": None,
        "width_mm": None,
        "height_mm": None,
        "host": None,
        **element,
    }
    return RevitCategoryQuery(
        request_id=f"{category}-request",
        document_title="Fixture",
        category_key=category,
        host_filter="all",
        total_count=1,
        returned_count=1,
        truncated=False,
        start_after_element_id=0,
        next_start_after_element_id=0,
        elements=(value,),
    )


def test_merge_mep_category_pages_summarizes_length_and_unresolved_hosts() -> None:
    grouped = {category: [_page(category, index + 1)] for index, category in enumerate(MEP_QUANTITY_CATEGORIES)}
    grouped["conduit"] = [_page("conduit", 20, curve_length_mm=2500.0, diameter_mm=20.0)]
    grouped["electrical_fixtures"] = [_page(
        "electrical_fixtures", 10,
        host={"is_linked": True, "linked_document_title": ""},
    )]
    report = merge_mep_category_pages(grouped)
    conduit = next(item for item in report["categories"] if item["category_key"] == "conduit")
    fixtures = next(item for item in report["categories"] if item["category_key"] == "electrical_fixtures")
    assert conduit["modelled_curve_length_m"] == 2.5
    assert conduit["missing_curve_length_count"] == 0
    assert fixtures["unresolved_linked_host_count"] == 1
    assert report["document_mutation_performed"] is False


def test_merge_mep_category_pages_requires_complete_category_set() -> None:
    with pytest.raises(RevitIntegrationError, match="category set"):
        merge_mep_category_pages({"conduit": [_page("conduit", 1)]})


def test_merge_mep_category_pages_rejects_incomplete_chain() -> None:
    grouped = {category: [_page(category, index + 1)] for index, category in enumerate(MEP_QUANTITY_CATEGORIES)}
    first = grouped["conduit"][0]
    grouped["conduit"] = [RevitCategoryQuery(
        request_id=first.request_id,
        document_title=first.document_title,
        category_key=first.category_key,
        host_filter=first.host_filter,
        total_count=2,
        returned_count=1,
        truncated=True,
        start_after_element_id=0,
        next_start_after_element_id=1,
        elements=first.elements,
    )]
    with pytest.raises(RevitIntegrationError, match="incomplete"):
        merge_mep_category_pages(grouped)
