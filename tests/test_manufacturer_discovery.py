"""Tests for project-scoped manufacturer discovery comparison contracts."""

from __future__ import annotations

from dataclasses import replace
import json
from pathlib import Path

import pytest

from agent.exceptions import ManufacturerEvidenceError
from agent.manufacturer_provider import ManufacturerProviderAdapter
from integrations.openai_manufacturer_search import (
    OfficialPageAccessRefused,
    create_openai_manufacturer_provider,
    verify_official_candidate_draft,
    verify_saved_candidate_draft,
)
from core.manufacturer_asset_collection import (
    ManufacturerAssetKind,
    create_asset_search_request,
)
from integrations.openai_provider import OpenAIProviderConfig
from integrations.build_m23_discovery_live_acceptance import build as build_live_request
from core.manufacturer_discovery import (
    CandidateOutcome,
    CandidateProperty,
    ManufacturerCandidate,
    ManufacturerRequirement,
    RequirementOperator,
    compare_candidates,
    create_discovery_request,
    parse_discovery_result,
    select_candidate_for_review,
)


MODEL_HASH = "a" * 64
SOURCE_HASH = "b" * 64


def _requirements() -> tuple[ManufacturerRequirement, ...]:
    return (
        ManufacturerRequirement(
            "requirement:rated-current", "rated_current", RequirementOperator.MINIMUM,
            "16", "A", "calculation", "calculation:socket-load", "c" * 64, True,
        ),
        ManufacturerRequirement(
            "requirement:rated-voltage", "rated_voltage", RequirementOperator.MINIMUM,
            "230", "V", "shared_model", "load:socket-1", "d" * 64, True,
        ),
        ManufacturerRequirement(
            "requirement:poles", "pole_configuration", RequirementOperator.EXACT,
            "2P+E", None, "engineer_input", "input:socket-poles", "e" * 64, True,
        ),
    )


def _request():
    return create_discovery_request(
        request_id="manufacturer-search:socket",
        project_id="project:test",
        model_sha256=MODEL_HASH,
        product_category="modular socket outlet",
        search_text="DIN rail socket 16 A 230 V 2P+E",
        official_domains=("www.legrand.com", "assets.legrand.com"),
        requirements=_requirements(),
        approved_by="engineer:gamal",
        approved_on="2026-08-12",
    )


def _candidate(
    candidate_id: str = "candidate:legrand-004280",
    *, current: str = "16", voltage: str = "250", poles: str = "2P+E",
    include_voltage: bool = True,
) -> ManufacturerCandidate:
    properties = [
        CandidateProperty("rated_current", current, "A", "F02438EN-00.pdf page 1 section 3"),
        CandidateProperty("pole_configuration", poles, None, "F02438EN-00.pdf page 1 section 1"),
    ]
    if include_voltage:
        properties.insert(
            1,
            CandidateProperty("rated_voltage", voltage, "V", "F02438EN-00.pdf page 1 section 3"),
        )
    return ManufacturerCandidate(
        candidate_id, "Legrand SA", "Legrand", "Lexic modular socket outlet",
        "Socket outlet 2P+E shuttered", "004280",
        "https://www.legrand.com/ecatalogue/en/catalog/products/004280",
        "www.legrand.com", "2026-08-12", SOURCE_HASH, tuple(properties),
    )


def test_project_request_is_self_hashed_and_grants_no_follow_on_authority() -> None:
    request = _request()
    assert len(request.request_sha256) == 64
    assert request.egress_approved is True
    assert request.automatic_selection_approved is False
    assert request.automatic_download_approved is False
    assert request.catalog_registration_approved is False
    assert request.drawing_mutation_approved is False
    with pytest.raises(ManufacturerEvidenceError, match="SHA-256"):
        replace(request, search_text="changed")


def test_candidates_are_compared_deterministically_without_selection_or_download() -> None:
    result = compare_candidates(
        _request(), (_candidate(), _candidate("candidate:legrand-failing", current="10")),
        discovery_id="manufacturer-discovery:socket",
        provider_name="official-search-fixture",
        reviewed_by="engineer:gamal",
        reviewed_on="2026-08-12",
        assumptions=("Official-source fixture only.",),
    )
    assert result.complete is True
    assert result.comparisons[0].mandatory_requirements_satisfied is True
    assert {item.outcome for item in result.comparisons[0].findings} == {CandidateOutcome.PASS}
    assert result.comparisons[1].mandatory_requirements_satisfied is False
    assert CandidateOutcome.FAIL in {item.outcome for item in result.comparisons[1].findings}
    assert result.provider_egress_performed is True
    assert result.automatic_selection_performed is False
    assert result.automatic_download_performed is False
    assert result.catalog_registration_performed is False
    assert result.drawing_mutation_performed is False


def test_missing_competing_and_wrong_unit_evidence_are_visible_and_incomplete() -> None:
    missing = _candidate(include_voltage=False)
    competing = replace(
        _candidate("candidate:competing"),
        properties=_candidate("candidate:competing").properties + (
            CandidateProperty("rated_voltage", "240", "V", "official page alternate table"),
        ),
    )
    wrong_unit = replace(
        _candidate("candidate:wrong-unit"),
        properties=(
            CandidateProperty("rated_current", "16", "A", "official page"),
            CandidateProperty("rated_voltage", "0.25", "kV", "official page"),
            CandidateProperty("pole_configuration", "2P+E", None, "official page"),
        ),
    )
    result = compare_candidates(
        _request(), (missing, competing, wrong_unit),
        discovery_id="manufacturer-discovery:uncertain",
        provider_name="official-search-fixture",
        reviewed_by="engineer:gamal", reviewed_on="2026-08-12",
        assumptions=("Uncertainty fixture.",),
    )
    outcomes = [{item.outcome for item in comparison.findings} for comparison in result.comparisons]
    assert CandidateOutcome.UNKNOWN in outcomes[0]
    assert CandidateOutcome.CONFLICT in outcomes[1]
    assert CandidateOutcome.CONFLICT in outcomes[2]
    assert result.complete is False


def test_engineer_choice_is_hash_bound_and_grants_no_follow_on_authority() -> None:
    result = compare_candidates(
        _request(), (_candidate(), _candidate("candidate:legrand-alternate", voltage="230")),
        discovery_id="manufacturer-discovery:choice",
        provider_name="official-search-fixture",
        reviewed_by="engineer:gamal", reviewed_on="2026-08-12",
        assumptions=("Multiple official candidates were reviewed.",),
    )
    parsed = parse_discovery_result(result.to_dict())
    selection = select_candidate_for_review(
        parsed,
        selection_id="manufacturer-selection:socket",
        candidate_id="candidate:legrand-alternate",
        selected_by="engineer:gamal",
        selected_on="2026-08-12",
        rationale="Closer nominal voltage match; continue to datasheet review.",
    )
    assert selection.discovery_result_sha256 == result.result_sha256
    assert selection.part_number == "004280"
    assert selection.selection_scope == "candidate_for_follow_on_review"
    assert selection.product_approval_performed is False
    assert selection.automatic_download_performed is False
    assert selection.catalog_registration_performed is False
    assert selection.drawing_mutation_performed is False
    with pytest.raises(ManufacturerEvidenceError, match="not in"):
        select_candidate_for_review(
            parsed, selection_id="manufacturer-selection:missing",
            candidate_id="candidate:not-returned", selected_by="engineer:gamal",
            selected_on="2026-08-12", rationale="Invalid fixture.",
        )


def test_unapproved_host_and_action_authority_fail_closed() -> None:
    request = _request()
    with pytest.raises(ManufacturerEvidenceError, match="not approved"):
        compare_candidates(
            request,
            (replace(_candidate(), product_url="https://reseller.example/product/004280", official_host="reseller.example"),),
            discovery_id="manufacturer-discovery:host", provider_name="official-search-fixture",
            reviewed_by="engineer:gamal", reviewed_on="2026-08-12",
            assumptions=("Host fixture.",),
        )
    with pytest.raises(ManufacturerEvidenceError, match="cannot approve"):
        replace(request, automatic_download_approved=True)


def test_provider_adapter_returns_only_strict_official_candidates() -> None:
    candidate = _candidate()
    output = {
        "candidates": [candidate.to_dict()],
        "automatic_selection_performed": False,
        "automatic_download_performed": False,
        "catalog_registration_performed": False,
        "drawing_mutation_performed": False,
    }
    provider = ManufacturerProviderAdapter(
        "official-search-fixture", lambda request: json.dumps(output)
    )
    assert provider.discover(_request()) == (candidate,)

    unsafe = {**output, "automatic_download_performed": True}
    with pytest.raises(ManufacturerEvidenceError, match="unauthorized action"):
        ManufacturerProviderAdapter(
            "unsafe-search", lambda request: json.dumps(unsafe)
        ).discover(_request())

    reseller = replace(
        candidate,
        product_url="https://reseller.example/product/004280",
        official_host="reseller.example",
    )
    with pytest.raises(ManufacturerEvidenceError, match="unapproved host"):
        ManufacturerProviderAdapter(
            "reseller-search",
            lambda request: json.dumps({**output, "candidates": [reseller.to_dict()]}),
        ).discover(_request())


def test_provider_adapter_reuses_exact_request_hash_without_second_paid_call() -> None:
    candidate = _candidate()
    output = json.dumps({
        "candidates": [candidate.to_dict()],
        "automatic_selection_performed": False,
        "automatic_download_performed": False,
        "catalog_registration_performed": False,
        "drawing_mutation_performed": False,
    })
    calls: list[str] = []

    def search(request) -> str:
        calls.append(request.request_sha256)
        return output

    provider = ManufacturerProviderAdapter(
        "paid-search-fixture", search, maximum_cached_requests=2
    )
    first = provider.discover(_request())
    second = provider.discover(_request())

    assert first is second
    assert calls == [_request().request_sha256]
    assert provider.maximum_cached_requests == 2


@pytest.mark.parametrize("limit", [0, 129, True])
def test_provider_adapter_rejects_invalid_cache_limits(limit: object) -> None:
    with pytest.raises(ManufacturerEvidenceError, match="cache limit"):
        ManufacturerProviderAdapter(
            "paid-search-fixture",
            lambda request: "{}",
            maximum_cached_requests=limit,  # type: ignore[arg-type]
        )

def test_openai_official_search_is_domain_bounded_and_locally_verifies_page_bytes() -> None:
    seen: dict[str, object] = {}
    draft = {
        "candidates": [
            {
                "manufacturer_name": "Legrand SA",
                "brand_name": "Legrand",
                "family_name": "Lexic modular socket outlet",
                "model": "Socket outlet 2P+E shuttered",
                "part_number": "004280",
                "product_url": "https://www.legrand.com/ecatalogue/en/catalog/products/004280",
                "official_host": "www.legrand.com",
                "properties": [
                    {
                        "property_name": "rated_current", "value": "16", "unit": "A",
                        "source_locator": "official product page",
                        "evidence_text": "Rated current 16 A",
                    },
                    {
                        "property_name": "rated_voltage", "value": "250", "unit": "V",
                        "source_locator": "official product page",
                        "evidence_text": "Rated voltage 250 V",
                    },
                    {
                        "property_name": "pole_configuration", "value": "2P+E", "unit": None,
                        "source_locator": "official product page",
                        "evidence_text": "Configuration 2P+E",
                    },
                ],
            }
        ]
    }

    def post(url: str, headers, body: bytes, timeout: float) -> bytes:
        seen.update(url=url, headers=dict(headers), body=json.loads(body), timeout=timeout)
        return json.dumps({"output_text": json.dumps(draft)}).encode()

    page = (
        b"<html>Product 004280 Rated current 16 A Rated voltage 250 V "
        b"Configuration 2P+E</html>"
    )
    provider = create_openai_manufacturer_provider(
        OpenAIProviderConfig(api_key="sk-test-abcdefghijklmnopqrstuvwxyz123456"),
        http_post=post,
        page_get=lambda url, maximum: ("text/html", page),
        resolver=lambda host: ("93.184.216.34",),
        today=lambda: __import__("datetime").date(2026, 8, 12),
    )
    candidate = provider.discover(_request())[0]

    assert provider.name == "openai-official-search:gpt-5.6"
    assert candidate.source_content_sha256 == __import__("hashlib").sha256(page).hexdigest()
    assert candidate.source_retrieved_on == "2026-08-12"
    assert seen["url"] == "https://api.openai.com/v1/responses"
    assert seen["body"]["tools"][0]["filters"]["allowed_domains"] == [
        "www.legrand.com", "assets.legrand.com"
    ]
    assert seen["body"]["text"]["format"]["strict"] is True
    schema_text = json.dumps(seen["body"]["text"]["format"]["schema"])
    assert "minLength" not in schema_text
    assert "maxLength" not in schema_text
    assert "minItems" not in schema_text
    assert "maxItems" not in schema_text


def test_paid_search_falls_back_only_to_matching_saved_official_evidence() -> None:
    provider_draft = {
        "candidates": [{
            "manufacturer_name": "Legrand SA", "brand_name": "Legrand",
            "family_name": "Lexic", "model": "Socket outlet 2P+E",
            "part_number": "004280",
            "product_url": "https://www.legrand.com/product/004280",
            "official_host": "www.legrand.com",
            "properties": [{
                "property_name": "rated_current", "value": "16", "unit": "A",
                "source_locator": "provider official result",
                "evidence_text": "Rated current 16 A",
            }],
        }]
    }
    saved_draft = {"candidates": [{
        **provider_draft["candidates"][0],
        "evidence_path": "workspace/manufacturer-evidence/legrand.html",
        "properties": [{
            "property_name": "rated_current", "value": "16", "unit": "A",
            "source_locator": "saved official page",
            "evidence_text": "Rated current 16 A",
        }],
    }, {
        **provider_draft["candidates"][0],
        "part_number": "NOT-RETURNED",
        "evidence_path": "workspace/manufacturer-evidence/unmatched.html",
    }]}
    provider = create_openai_manufacturer_provider(
        OpenAIProviderConfig(api_key="sk-test-abcdefghijklmnopqrstuvwxyz123456"),
        http_post=lambda *args: json.dumps(
            {"output_text": json.dumps(provider_draft)}
        ).encode(),
        page_get=lambda *args: (_ for _ in ()).throw(
            OfficialPageAccessRefused("Official product page returned HTTP 403.")
        ),
        resolver=lambda host: ("93.184.216.34",),
        today=lambda: __import__("datetime").date(2026, 8, 13),
        saved_candidate_draft=saved_draft,
        evidence_read=lambda path: b"Product 004280 Rated current 16 A",
    )

    candidates = provider.discover(_request())

    assert provider.name == "openai-official-search:gpt-5.6:saved-evidence"
    assert [item.part_number for item in candidates] == ["004280"]
    assert candidates[0].source_retrieved_on == "2026-08-13"


def test_paid_asset_search_returns_official_candidates_without_download() -> None:
    seen = {}
    draft = {
        "candidates": [{
            "candidate_id": "asset-candidate:legrand-step",
            "kind": "cad_3d", "part_number": "004280", "title": "Official STEP",
            "locator": "https://assets.legrand.com/pim/004280.stp",
            "official_host": "assets.legrand.com", "media_type": "model/step",
            "source_page_url": "https://assets.legrand.com/product/004280",
            "source_retrieved_on": "2026-08-26",
        }],
        "automatic_selection_performed": False,
        "automatic_download_performed": False,
        "catalog_registration_performed": False,
        "drawing_mutation_performed": False,
    }

    def post(url, headers, body, timeout):
        seen["body"] = json.loads(body)
        return json.dumps({"output_text": json.dumps(draft)}).encode()

    provider = create_openai_manufacturer_provider(
        OpenAIProviderConfig(api_key="sk-test-abcdefghijklmnopqrstuvwxyz123456"),
        http_post=post,
    )
    request = create_asset_search_request(
        request_id="asset-search:legrand", project_id="project:test",
        manufacturer_name="Legrand", part_number="004280",
        product_page_url="https://www.legrand.com/product/004280",
        product_evidence_sha256="a" * 64,
        official_domains=("www.legrand.com", "assets.legrand.com"),
        requested_kinds=(ManufacturerAssetKind.CAD_3D,),
        approved_by="engineer:gamal", approved_on="2026-08-26", egress_approved=True,
    )
    candidates = provider.discover_assets(request)
    assert [item.locator for item in candidates] == [
        "https://assets.legrand.com/pim/004280.stp"
    ]
    assert seen["body"]["tools"][0]["filters"]["allowed_domains"] == [
        "www.legrand.com", "assets.legrand.com"
    ]
    assert seen["body"]["text"]["format"]["name"] == "official_manufacturer_assets"


def test_free_candidate_draft_is_verified_without_provider_egress() -> None:
    draft = {
        "candidates": [{
            "manufacturer_name": "Legrand SA", "brand_name": "Legrand",
            "family_name": "Lexic", "model": "Socket outlet 2P+E",
            "part_number": "004280",
            "product_url": "https://www.legrand.com/product/004280",
            "official_host": "www.legrand.com",
            "properties": [{
                "property_name": "rated_current", "value": "16", "unit": "A",
                "source_locator": "official product page",
                "evidence_text": "Rated current 16 A",
            }],
        }]
    }
    page = b"<html>Product 004280 Rated current 16 A</html>"
    candidates = verify_official_candidate_draft(
        draft, _request(),
        page_get=lambda url, maximum: ("text/html", page),
        resolver=lambda host: ("93.184.216.34",),
        today=lambda: __import__("datetime").date(2026, 8, 12),
    )
    result = compare_candidates(
        _request(), candidates,
        discovery_id="manufacturer-discovery:free",
        provider_name="manual-official-verification",
        reviewed_by="engineer:gamal", reviewed_on="2026-08-12",
        assumptions=("User supplied the candidate draft.",),
        provider_egress_performed=False,
    )
    assert candidates[0].source_content_sha256 == __import__("hashlib").sha256(page).hexdigest()
    assert result.provider_egress_performed is False
    assert result.automatic_selection_performed is False


def test_browser_saved_html_is_hash_bound_and_never_fetches_provider() -> None:
    raw = {
        "manufacturer_name": "Legrand SA", "brand_name": "Legrand",
        "family_name": "Lexic", "model": "Socket outlet 2P+E",
        "part_number": "004280",
        "product_url": "https://www.legrand.com/product/004280",
        "official_host": "www.legrand.com",
        "evidence_path": "workspace/manufacturer-evidence/legrand-004280.html",
        "properties": [{
            "property_name": "rated_current", "value": "16", "unit": "A",
            "source_locator": "Saved official page", "evidence_text": "Rated current 16 A",
        }],
    }
    page = b"<html>Official product 004280 Rated current 16 A</html>"
    seen: list[str] = []

    def read(path: str) -> bytes:
        seen.append(path)
        return page

    candidates = verify_saved_candidate_draft(
        {"candidates": [raw]}, _request(), evidence_read=read,
        today=lambda: __import__("datetime").date(2026, 8, 12),
    )
    assert seen == ["workspace/manufacturer-evidence/legrand-004280.html"]
    assert candidates[0].source_content_sha256 == __import__("hashlib").sha256(page).hexdigest()
    assert candidates[0].properties[0].value == "16"

    with pytest.raises(ManufacturerEvidenceError, match="matching part number"):
        verify_saved_candidate_draft(
            {"candidates": [raw]}, _request(), evidence_read=lambda path: b"unrelated page"
        )


def test_openai_official_search_rejects_unverified_evidence_and_private_dns() -> None:
    draft = {
        "candidates": [
            {
                "manufacturer_name": "Legrand SA", "brand_name": "Legrand",
                "family_name": "Lexic", "model": "Socket", "part_number": "004280",
                "product_url": "https://www.legrand.com/product/004280",
                "official_host": "www.legrand.com",
                "properties": [{
                    "property_name": "rated_current", "value": "16", "unit": "A",
                    "source_locator": "official page", "evidence_text": "Rated current 16 A",
                }],
            }
        ]
    }
    response = json.dumps({"output_text": json.dumps(draft)}).encode()
    provider = create_openai_manufacturer_provider(
        OpenAIProviderConfig(api_key="sk-test-abcdefghijklmnopqrstuvwxyz123456"),
        http_post=lambda *args: response,
        page_get=lambda url, maximum: ("text/html", b"Product 004280 without claimed value"),
        resolver=lambda host: ("93.184.216.34",),
    )
    with pytest.raises(ManufacturerEvidenceError, match="no candidate"):
        provider.discover(_request())

    private = create_openai_manufacturer_provider(
        OpenAIProviderConfig(api_key="sk-test-abcdefghijklmnopqrstuvwxyz123456"),
        http_post=lambda *args: response,
        page_get=lambda url, maximum: ("text/html", b"004280 Rated current 16 A"),
        resolver=lambda host: ("127.0.0.1",),
    )
    with pytest.raises(ManufacturerEvidenceError, match="non-public"):
        private.discover(_request())


def test_provider_preserves_safe_typed_manufacturer_failure() -> None:
    def fail(request):
        raise ManufacturerEvidenceError("OpenAI provider returned HTTP 400.")

    provider = ManufacturerProviderAdapter("safe-failure", fail)
    with pytest.raises(ManufacturerEvidenceError, match="HTTP 400"):
        provider.discover(_request())


def test_live_discovery_builder_is_self_hashed_multi_domain_and_idempotent(tmp_path) -> None:
    path = build_live_request(tmp_path, reviewed_on="2026-08-12")
    document = json.loads(path.read_text(encoding="utf-8"))
    request = document["request"]
    manifest_path = path.with_name("manifest.json")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    copied_template = (
        tmp_path / "workspace" / "manufacturer-evidence" / "free-candidates.template.json"
    )
    packaged_template = (
        Path(__file__).resolve().parents[1]
        / "integrations" / "manufacturer_templates" / "free-candidates.template.json"
    )

    assert path.parent.name == "2026-08-12"
    assert build_live_request(tmp_path, reviewed_on="2026-08-12") == path
    assert request["official_domains"] == ["www.legrand.com", "www.se.com"]
    assert len(request["requirements"]) == 3
    assert manifest["minimum_expected_candidates"] == 2
    assert manifest["discovery_request_sha256"] == request["request_sha256"]
    assert manifest["product_approval_performed"] is False
    assert manifest["automatic_download_performed"] is False
    assert manifest["catalog_registration_performed"] is False
    assert manifest["drawing_mutation_performed"] is False
    assert copied_template.read_bytes() == packaged_template.read_bytes()

    path.write_text("{}\n", encoding="utf-8")
    with pytest.raises(RuntimeError, match="not overwritten"):
        build_live_request(tmp_path, reviewed_on="2026-08-12")


def test_live_discovery_builder_preserves_prior_date_and_rejects_unsafe_dates(
    tmp_path,
) -> None:
    prior = build_live_request(tmp_path, reviewed_on="2026-08-12")
    prior_bytes = prior.read_bytes()

    current = build_live_request(tmp_path, reviewed_on="2026-08-13")

    assert current != prior
    assert current.parent.name == "2026-08-13"
    assert prior.read_bytes() == prior_bytes
    for invalid in ("2026-8-13", "../2026-08-13", "not-a-date"):
        with pytest.raises(RuntimeError, match="ISO calendar date"):
            build_live_request(tmp_path, reviewed_on=invalid)
