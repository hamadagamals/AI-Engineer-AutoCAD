"""Tests for ordered, hash-bound manufacturer asset collection."""

from __future__ import annotations

import hashlib
from dataclasses import replace
from io import BytesIO

import pytest

from agent.exceptions import ManufacturerEvidenceError
from core.manufacturer_asset_collection import (
    ManufacturerAssetCandidate,
    ManufacturerAssetKind,
    approve_asset_collection,
    collect_approved_assets,
    create_asset_search_request,
    create_asset_search_result,
)
from integrations.manufacturer_retrieval import retrieve_to_quarantine


PDF = b"%PDF-1.7\nmanufacturer data sheet\n%%EOF\n"
STEP = b"ISO-10303-21;\nHEADER;\nENDSEC;\nDATA;\nENDSEC;\nEND-ISO-10303-21;\n"


def _request():
    return create_asset_search_request(
        request_id="asset-search:nsx250", project_id="project:test",
        manufacturer_name="Schneider Electric", part_number="C25B32TM250",
        product_page_url="https://www.se.com/product/C25B32TM250",
        product_evidence_sha256="a" * 64, official_domains=("www.se.com",),
        requested_kinds=(ManufacturerAssetKind.DATA_SHEET, ManufacturerAssetKind.CAD_3D),
        approved_by="engineer:gamal", approved_on="2026-08-26", egress_approved=True,
    )


def _candidates():
    return (
        ManufacturerAssetCandidate(
            "asset-candidate:datasheet", ManufacturerAssetKind.DATA_SHEET,
            "C25B32TM250", "Official data sheet",
            "https://www.se.com/files/C25B32TM250.pdf", "www.se.com",
            "application/pdf", "https://www.se.com/product/C25B32TM250", "2026-08-26",
        ),
        ManufacturerAssetCandidate(
            "asset-candidate:step", ManufacturerAssetKind.CAD_3D,
            "C25B32TM250", "Official STEP model",
            "https://www.se.com/files/C25B32TM250.stp", "www.se.com",
            "model/step", "https://www.se.com/product/C25B32TM250", "2026-08-26",
        ),
    )


def test_ordered_collection_writes_separate_quarantine_folders(tmp_path) -> None:
    request = _request()
    result = create_asset_search_result(
        request, _candidates(), result_id="asset-result:nsx250", provider_name="official-search:test"
    )
    approval = approve_asset_collection(
        result, approval_id="asset-approval:nsx250",
        selected_candidate_ids=("asset-candidate:step", "asset-candidate:datasheet"),
        approved_by="engineer:gamal", approved_on="2026-08-26", maximum_bytes_per_asset=4096,
    )

    def retrieve(request_value, root, *, retrieved_on):
        content = STEP if request_value.expected_media_types == ("model/step",) else PDF
        media = request_value.expected_media_types[0]
        return retrieve_to_quarantine(
            request_value, root, resolver=lambda _host: ("93.184.216.34",),
            transport=lambda _url, _maximum: (media, BytesIO(content)),
            retrieved_on=retrieved_on,
        )

    collected = collect_approved_assets(
        request, result, approval, tmp_path / "manufacturer-assets",
        retrieved_on="2026-08-26", retriever=retrieve,
    )
    root = tmp_path / "manufacturer-assets" / "schneider-electric" / "c25b32tm250"
    assert collected.complete is True
    assert len(collected.records) == 2
    assert (root / "01-cad_3d" / collected.records[0].quarantine_relative_path).read_bytes() == STEP
    assert (root / "02-data_sheet" / collected.records[1].quarantine_relative_path).read_bytes() == PDF
    assert collected.catalog_registration_performed is False
    assert collected.drawing_mutation_performed is False
    assert len(collected.collection_sha256) == 64


def test_search_and_collection_fail_closed_on_scope_or_lineage(tmp_path) -> None:
    request = _request()
    bad = replace(_candidates()[0], part_number="OTHER")
    with pytest.raises(ManufacturerEvidenceError, match="part number"):
        create_asset_search_result(
            request, (bad,), result_id="asset-result:bad", provider_name="official-search:test"
        )
    result = create_asset_search_result(
        request, _candidates(), result_id="asset-result:good", provider_name="official-search:test"
    )
    approval = approve_asset_collection(
        result, approval_id="asset-approval:good",
        selected_candidate_ids=("asset-candidate:datasheet",),
        approved_by="engineer:gamal", approved_on="2026-08-26",
    )
    with pytest.raises(ManufacturerEvidenceError, match="SHA-256"):
        collect_approved_assets(
            request, result, replace(approval, result_sha256="b" * 64), tmp_path,
            retrieved_on="2026-08-26",
        )


def test_asset_and_source_page_may_use_different_approved_official_hosts() -> None:
    request = create_asset_search_request(
        request_id="asset-search:cdn", project_id="project:test",
        manufacturer_name="Example", part_number="ABC123",
        product_page_url="https://www.example.test/product/ABC123",
        product_evidence_sha256="c" * 64,
        official_domains=("www.example.test", "assets.example.test"),
        requested_kinds=(ManufacturerAssetKind.DATA_SHEET,),
        approved_by="engineer:gamal", approved_on="2026-08-26", egress_approved=True,
    )
    candidate = ManufacturerAssetCandidate(
        "asset-candidate:cdn", ManufacturerAssetKind.DATA_SHEET, "ABC123",
        "Official data sheet", "https://assets.example.test/ABC123.pdf",
        "assets.example.test", "application/pdf",
        "https://www.example.test/product/ABC123", "2026-08-26",
    )
    result = create_asset_search_result(
        request, (candidate,), result_id="asset-result:cdn", provider_name="official-search:test"
    )
    assert result.candidates == (candidate,)

    with pytest.raises(ManufacturerEvidenceError, match="source-page host"):
        create_asset_search_result(
            request,
            (replace(candidate, source_page_url="https://unapproved.example/ABC123"),),
            result_id="asset-result:bad-source", provider_name="official-search:test",
        )
@pytest.mark.parametrize(
    ("media", "suffix", "content"),
    (("model/step", "stp", STEP), ("image/png", "png", b"\x89PNG\r\n\x1a\n" + b"x" * 20)),
)
def test_new_asset_media_signatures_are_supported(tmp_path, media, suffix, content) -> None:
    from integrations.manufacturer_retrieval import create_retrieval_request

    request = create_retrieval_request(
        request_id=f"retrieval:test-{suffix}", manufacturer_id="manufacturer:test",
        product_id="product:test", artifact_id=f"artifact:test-{suffix}",
        locator=f"https://assets.example.test/item.{suffix}", approved_host="assets.example.test",
        expected_media_types=(media,), maximum_bytes=4096,
        expected_content_sha256=hashlib.sha256(content).hexdigest(),
        approved_by="engineer:gamal", approved_on="2026-08-26",
    )
    record = retrieve_to_quarantine(
        request, tmp_path / suffix, resolver=lambda _host: ("93.184.216.34",),
        transport=lambda _url, _maximum: ("application/octet-stream", BytesIO(content)),
        retrieved_on="2026-08-26",
    )
    assert record.media_type == media
