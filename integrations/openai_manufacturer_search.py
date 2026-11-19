"""Opt-in official-domain product discovery with local source verification."""

from __future__ import annotations

import hashlib
import ipaddress
import json
import re
import socket
import ssl
import urllib.error
import urllib.request
from datetime import date
from html.parser import HTMLParser
from typing import Callable, Iterable
from urllib.parse import urlsplit

from agent.exceptions import ManufacturerEvidenceError, ProviderError
from agent.manufacturer_provider import ManufacturerProviderAdapter
from core.manufacturer_discovery import (
    CandidateProperty,
    ManufacturerCandidate,
    ManufacturerDiscoveryRequest,
)
from core.manufacturer_asset_collection import ManufacturerAssetSearchRequest
from integrations.openai_provider import (
    HttpPost,
    OpenAIProviderConfig,
    _extract_output_text,
    _post_without_redirects_or_proxies,
)


_RESPONSES_URL = "https://api.openai.com/v1/responses"
_MAX_PAGE_BYTES = 2_097_152
_MAX_RESPONSE_BYTES = 1_048_576
_TEXT_MEDIA_TYPES = frozenset(
    {"text/html", "application/xhtml+xml", "application/json", "text/plain"}
)
PageGet = Callable[[str, int], tuple[str, bytes]]
Resolver = Callable[[str], Iterable[str]]
Today = Callable[[], date]
EvidenceRead = Callable[[str], bytes]


class OfficialPageAccessRefused(ManufacturerEvidenceError):
    """An official host explicitly refused transparent automated retrieval."""


def create_openai_manufacturer_provider(
    config: OpenAIProviderConfig,
    *,
    http_post: HttpPost | None = None,
    page_get: PageGet | None = None,
    resolver: Resolver | None = None,
    today: Today | None = None,
    saved_candidate_draft: object | None = None,
    evidence_read: EvidenceRead | None = None,
) -> ManufacturerProviderAdapter:
    """Create the separately opt-in official manufacturer search adapter."""
    search = OpenAIManufacturerSearch(
        config,
        http_post=http_post,
        page_get=page_get,
        resolver=resolver,
        today=today,
        saved_candidate_draft=saved_candidate_draft,
        evidence_read=evidence_read,
    )
    fallback_enabled = saved_candidate_draft is not None and evidence_read is not None
    return ManufacturerProviderAdapter(
        f"openai-official-search:{config.model.casefold()}"
        + (":saved-evidence" if fallback_enabled else ""),
        search.search,
        asset_search=search.search_assets,
        timeout_seconds=min(180, max(30, int(config.timeout_seconds) + 45)),
    )


class OpenAIManufacturerSearch:
    """Use hosted web search only for discovery, then verify cited official pages locally."""

    def __init__(
        self,
        config: OpenAIProviderConfig,
        *,
        http_post: HttpPost | None = None,
        page_get: PageGet | None = None,
        resolver: Resolver | None = None,
        today: Today | None = None,
        saved_candidate_draft: object | None = None,
        evidence_read: EvidenceRead | None = None,
    ) -> None:
        if (saved_candidate_draft is None) != (evidence_read is None):
            raise ManufacturerEvidenceError(
                "Saved official-evidence fallback requires both draft and reader."
            )
        self.config = config
        self._http_post = http_post or _post_without_redirects_or_proxies
        self._page_get = page_get or _https_get_text
        self._resolver = resolver or _resolve_addresses
        self._today = today or date.today
        self._saved_candidate_draft = saved_candidate_draft
        self._evidence_read = evidence_read

    def search(self, request: ManufacturerDiscoveryRequest) -> str:
        """Return adapter-compatible candidates backed by bytes fetched from approved hosts."""
        if not isinstance(request, ManufacturerDiscoveryRequest):
            raise ManufacturerEvidenceError("OpenAI manufacturer search requires a typed request.")
        body = {
            "model": self.config.model,
            "instructions": (
                "Find real matching products only on the allowed official manufacturer domains. "
                "Do not use distributors, marketplaces, snippets without an official product URL, "
                "or claim stock/availability. Return factual values exactly as stated. For every "
                "property include a short verbatim evidence_text copied from that same official "
                "product page. Use the exact property_name strings from the supplied requirements; "
                "omit a property when the official page does not state it. Return multiple distinct "
                "part numbers when the official evidence supports them. Do not select, download, "
                "register, or modify anything."
            ),
            "input": json.dumps(request.to_dict(), ensure_ascii=False, separators=(",", ":")),
            "store": False,
            "max_output_tokens": min(self.config.max_output_tokens, 8192),
            "reasoning": {"effort": self.config.reasoning_effort},
            "tools": [{
                "type": "web_search",
                "filters": {"allowed_domains": list(request.official_domains)},
                "search_context_size": "medium",
            }],
            "text": {
                "verbosity": "low",
                "format": {
                    "type": "json_schema",
                    "name": "official_manufacturer_candidates",
                    "strict": True,
                    "schema": _draft_schema(),
                },
            },
        }
        if self.config.reasoning_mode == "pro":
            body["reasoning"]["mode"] = "pro"
        encoded = json.dumps(body, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        if len(encoded) > 524_288:
            raise ManufacturerEvidenceError("Manufacturer search request exceeds its safe limit.")
        headers = {
            "Authorization": f"Bearer {self.config.api_key}",
            "Content-Type": "application/json",
            "Accept": "application/json",
            "User-Agent": "AI-Engineer/manufacturer-discovery",
        }
        try:
            raw = self._http_post(
                _RESPONSES_URL, headers, encoded, float(self.config.timeout_seconds)
            )
        except ProviderError as exc:
            raise ManufacturerEvidenceError(str(exc)) from exc
        except Exception as exc:
            raise ManufacturerEvidenceError(
                f"Official manufacturer search failed: {type(exc).__name__}."
            ) from exc
        if not isinstance(raw, (bytes, bytearray)) or not 1 <= len(raw) <= _MAX_RESPONSE_BYTES:
            raise ManufacturerEvidenceError("Manufacturer search response exceeds its safe limit.")
        try:
            response = json.loads(bytes(raw).decode("utf-8"))
            draft = json.loads(_extract_output_text(response))
        except (UnicodeError, json.JSONDecodeError) as exc:
            raise ManufacturerEvidenceError("Manufacturer search returned invalid structured data.") from exc
        try:
            candidates = self._verify_draft(draft, request)
        except OfficialPageAccessRefused:
            if self._saved_candidate_draft is None or self._evidence_read is None:
                raise
            matching_saved = _matching_saved_candidate_draft(
                draft, self._saved_candidate_draft
            )
            candidates = tuple(
                item.to_dict()
                for item in verify_saved_candidate_draft(
                    matching_saved,
                    request,
                    evidence_read=self._evidence_read,
                    today=self._today,
                )
            )
        if not candidates:
            raise ManufacturerEvidenceError(
                "Manufacturer search found no candidate with independently verified official evidence."
            )
        return json.dumps(
            {
                "candidates": candidates,
                "automatic_selection_performed": False,
                "automatic_download_performed": False,
                "catalog_registration_performed": False,
                "drawing_mutation_performed": False,
            },
            ensure_ascii=False,
            separators=(",", ":"),
        )

    def search_assets(self, request: ManufacturerAssetSearchRequest) -> str:
        """Find official asset URLs as candidates only; never download or select them."""
        if not isinstance(request, ManufacturerAssetSearchRequest):
            raise ManufacturerEvidenceError("OpenAI asset search requires a typed request.")
        body = {
            "model": self.config.model,
            "instructions": (
                "Find downloadable files for the exact supplied manufacturer part number only on "
                "the allowed official domains. Return direct official HTTPS asset URLs and the "
                "official source page URL. Supported kinds are data_sheet, cad_2d, cad_3d, image. "
                "Supported media types are application/pdf, application/dxf, application/acad, "
                "model/step, application/step, model/iges, image/jpeg, image/png. Do not return "
                "distributors, login bypasses, executable files, ZIP archives, family-only assets, "
                "or a different part number. Do not select, download, register, or modify anything."
            ),
            "input": json.dumps(request.to_dict(), ensure_ascii=False, separators=(",", ":")),
            "store": False,
            "max_output_tokens": min(self.config.max_output_tokens, 8192),
            "reasoning": {"effort": self.config.reasoning_effort},
            "tools": [{
                "type": "web_search",
                "filters": {"allowed_domains": list(request.official_domains)},
                "search_context_size": "medium",
            }],
            "text": {"verbosity": "low", "format": {
                "type": "json_schema", "name": "official_manufacturer_assets",
                "strict": True, "schema": _asset_draft_schema(),
            }},
        }
        if self.config.reasoning_mode == "pro":
            body["reasoning"]["mode"] = "pro"
        encoded = json.dumps(body, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        headers = {
            "Authorization": f"Bearer {self.config.api_key}",
            "Content-Type": "application/json", "Accept": "application/json",
            "User-Agent": "AI-Engineer/manufacturer-asset-discovery",
        }
        try:
            raw = self._http_post(
                _RESPONSES_URL, headers, encoded, float(self.config.timeout_seconds)
            )
            if not isinstance(raw, (bytes, bytearray)) or not 1 <= len(raw) <= _MAX_RESPONSE_BYTES:
                raise ManufacturerEvidenceError(
                    "Manufacturer asset search response exceeds its safe limit."
                )
            response = json.loads(bytes(raw).decode("utf-8"))
            draft = json.loads(_extract_output_text(response))
        except ProviderError as exc:
            raise ManufacturerEvidenceError(str(exc)) from exc
        except (UnicodeError, json.JSONDecodeError, TypeError) as exc:
            raise ManufacturerEvidenceError("Manufacturer asset search returned invalid structured data.") from exc
        if not isinstance(draft, dict) or set(draft) != {
            "candidates", "automatic_selection_performed", "automatic_download_performed",
            "catalog_registration_performed", "drawing_mutation_performed",
        }:
            raise ManufacturerEvidenceError("Manufacturer asset search fields are invalid.")
        return json.dumps(draft, ensure_ascii=False, separators=(",", ":"))

    def _verify_draft(
        self, draft: object, request: ManufacturerDiscoveryRequest
    ) -> list[dict[str, object]]:
        return [candidate.to_dict() for candidate in verify_official_candidate_draft(
            draft,
            request,
            page_get=self._page_get,
            resolver=self._resolver,
            today=self._today,
        )]

    def _verify_candidate(
        self, candidate: object, request: ManufacturerDiscoveryRequest
    ) -> dict[str, object] | None:
        verified = _verify_candidate(
            candidate,
            request,
            page_get=self._page_get,
            resolver=self._resolver,
            today=self._today,
        )
        return verified.to_dict() if verified is not None else None


def verify_official_candidate_draft(
    draft: object,
    request: ManufacturerDiscoveryRequest,
    *,
    page_get: PageGet | None = None,
    resolver: Resolver | None = None,
    today: Today | None = None,
) -> tuple[ManufacturerCandidate, ...]:
    """Verify user/provider candidate drafts against freshly fetched official pages."""
    if not isinstance(request, ManufacturerDiscoveryRequest):
        raise ManufacturerEvidenceError("Official candidate verification requires a typed request.")
    if not isinstance(draft, dict) or set(draft) != {"candidates"}:
        raise ManufacturerEvidenceError("Manufacturer candidate-draft fields are invalid.")
    raw_candidates = draft["candidates"]
    if not isinstance(raw_candidates, list) or not 1 <= len(raw_candidates) <= 16:
        raise ManufacturerEvidenceError("Manufacturer candidate-draft count is invalid.")
    get_page = page_get or _https_get_text
    resolve = resolver or _resolve_addresses
    clock = today or date.today
    verified: list[ManufacturerCandidate] = []
    identities: set[tuple[str, str]] = set()
    ids: set[str] = set()
    for raw_candidate in raw_candidates:
        candidate = _verify_candidate(
            raw_candidate, request, page_get=get_page, resolver=resolve, today=clock
        )
        if candidate is None:
            continue
        identity = (candidate.official_host, candidate.part_number)
        if identity in identities or candidate.candidate_id in ids:
            continue
        identities.add(identity)
        ids.add(candidate.candidate_id)
        verified.append(candidate)
    if not verified:
        raise ManufacturerEvidenceError(
            "Manufacturer search found no candidate with independently verified official evidence."
        )
    return tuple(verified)


def verify_saved_candidate_draft(
    draft: object,
    request: ManufacturerDiscoveryRequest,
    *,
    evidence_read: EvidenceRead,
    today: Today | None = None,
) -> tuple[ManufacturerCandidate, ...]:
    """Verify a browser-saved UTF-8 HTML snapshot inside the configured workspace."""
    if not isinstance(request, ManufacturerDiscoveryRequest):
        raise ManufacturerEvidenceError("Saved evidence verification requires a typed request.")
    if not callable(evidence_read):
        raise ManufacturerEvidenceError("Saved evidence reader is invalid.")
    if not isinstance(draft, dict) or set(draft) != {"candidates"}:
        raise ManufacturerEvidenceError("Saved candidate-draft fields are invalid.")
    raw_candidates = draft["candidates"]
    if not isinstance(raw_candidates, list) or not 1 <= len(raw_candidates) <= 16:
        raise ManufacturerEvidenceError("Saved candidate-draft count is invalid.")
    clock = today or date.today
    verified: list[ManufacturerCandidate] = []
    identities: set[tuple[str, str]] = set()
    for raw_candidate in raw_candidates:
        if not isinstance(raw_candidate, dict) or "evidence_path" not in raw_candidate:
            raise ManufacturerEvidenceError("Saved candidate evidence path is missing.")
        path = raw_candidate["evidence_path"]
        if not isinstance(path, str) or not path.casefold().endswith((".html", ".htm")):
            raise ManufacturerEvidenceError("Saved evidence must be an HTML file.")
        candidate = {name: value for name, value in raw_candidate.items() if name != "evidence_path"}
        content = evidence_read(path)
        if not isinstance(content, bytes) or not 1 <= len(content) <= _MAX_PAGE_BYTES:
            raise ManufacturerEvidenceError("Saved official HTML exceeds its safe byte limit.")
        item = _verify_candidate_content(
            candidate, request, media_type="text/html", content=content, today=clock
        )
        if item is None:
            continue
        identity = (item.official_host, item.part_number)
        if identity in identities:
            continue
        identities.add(identity)
        verified.append(item)
    if not verified:
        raise ManufacturerEvidenceError(
            "Saved HTML contained no candidate with matching part number and property evidence."
        )
    return tuple(verified)


def _matching_saved_candidate_draft(
    provider_draft: object, saved_draft: object
) -> dict[str, object]:
    """Retain saved evidence only for identities actually returned by paid discovery."""
    if not isinstance(provider_draft, dict) or not isinstance(
        provider_draft.get("candidates"), list
    ):
        raise ManufacturerEvidenceError("Provider candidate draft is invalid for fallback.")
    if not isinstance(saved_draft, dict) or set(saved_draft) != {"candidates"} or not isinstance(
        saved_draft["candidates"], list
    ):
        raise ManufacturerEvidenceError("Saved candidate fallback draft is invalid.")
    identities = {
        (item.get("official_host"), item.get("part_number"))
        for item in provider_draft["candidates"]
        if isinstance(item, dict)
    }
    matching = [
        item
        for item in saved_draft["candidates"]
        if isinstance(item, dict)
        and (item.get("official_host"), item.get("part_number")) in identities
    ]
    if not matching:
        raise ManufacturerEvidenceError(
            "Paid discovery candidates have no matching engineer-saved official evidence."
        )
    return {"candidates": matching}


def _verify_candidate(
    candidate: object,
    request: ManufacturerDiscoveryRequest,
    *,
    page_get: PageGet,
    resolver: Resolver,
    today: Today,
) -> ManufacturerCandidate | None:
        fields = {
            "manufacturer_name", "brand_name", "family_name", "model",
            "part_number", "product_url", "official_host", "properties",
        }
        if not isinstance(candidate, dict) or set(candidate) != fields:
            raise ManufacturerEvidenceError("Manufacturer search candidate fields are invalid.")
        url = candidate["product_url"]
        host = candidate["official_host"]
        if not isinstance(url, str) or not isinstance(host, str):
            return None
        parsed = urlsplit(url)
        if (
            parsed.scheme != "https" or parsed.hostname != host or host not in request.official_domains
            or parsed.username is not None or parsed.password is not None
            or parsed.port not in (None, 443) or parsed.fragment
        ):
            return None
        addresses = tuple(sorted(set(resolver(host))))
        if not addresses:
            return None
        for address in addresses:
            _require_public_ip(address)
        media_type, content = page_get(url, _MAX_PAGE_BYTES)
        return _verify_candidate_content(
            candidate, request, media_type=media_type, content=content, today=today
        )


def _verify_candidate_content(
    candidate: object,
    request: ManufacturerDiscoveryRequest,
    *,
    media_type: str,
    content: bytes,
    today: Today,
) -> ManufacturerCandidate | None:
        fields = {
            "manufacturer_name", "brand_name", "family_name", "model",
            "part_number", "product_url", "official_host", "properties",
        }
        if not isinstance(candidate, dict) or set(candidate) != fields:
            raise ManufacturerEvidenceError("Manufacturer search candidate fields are invalid.")
        url = candidate["product_url"]
        host = candidate["official_host"]
        if not isinstance(url, str) or not isinstance(host, str):
            return None
        parsed = urlsplit(url)
        if (
            parsed.scheme != "https" or parsed.hostname != host or host not in request.official_domains
            or parsed.username is not None or parsed.password is not None
            or parsed.port not in (None, 443) or parsed.fragment
        ):
            return None
        if media_type.casefold() not in _TEXT_MEDIA_TYPES or not 1 <= len(content) <= _MAX_PAGE_BYTES:
            return None
        decoded = content.decode("utf-8", errors="replace")
        page_text = _normalize(_visible_text(decoded) if media_type.casefold() in {"text/html", "application/xhtml+xml"} else decoded)
        part_number = candidate["part_number"]
        if not isinstance(part_number, str) or _normalize(part_number) not in page_text:
            return None
        raw_properties = candidate["properties"]
        if not isinstance(raw_properties, list) or len(raw_properties) > 128:
            return None
        properties: list[dict[str, object]] = []
        for prop in raw_properties:
            if not isinstance(prop, dict) or set(prop) != {
                "property_name", "value", "unit", "source_locator", "evidence_text"
            }:
                continue
            evidence = prop["evidence_text"]
            value = prop["value"]
            if (
                not isinstance(evidence, str) or not isinstance(value, str)
                or not 1 <= len(evidence.encode("utf-8")) <= 512
                or _normalize(evidence) not in page_text
                or _normalize(value) not in _normalize(evidence)
            ):
                continue
            properties.append({name: prop[name] for name in (
                "property_name", "value", "unit", "source_locator"
            )})
        if not properties:
            return None
        values = {
            **{name: candidate[name] for name in fields if name != "properties"},
            "candidate_id": "candidate:" + hashlib.sha256(
                (host + "\n" + part_number).encode("utf-8")
            ).hexdigest()[:24],
            "source_retrieved_on": today().isoformat(),
            "source_content_sha256": hashlib.sha256(content).hexdigest(),
            "properties": tuple(CandidateProperty(**item) for item in properties),
        }
        return ManufacturerCandidate(**values)


def _draft_schema() -> dict[str, object]:
    # OpenAI strict Structured Outputs accepts only a subset of JSON Schema. Length and
    # array-cardinality bounds stay in AI-Engineer's local typed validators, not here.
    text = {"type": "string"}
    return {
        "type": "object",
        "additionalProperties": False,
        "required": ["candidates"],
        "properties": {
            "candidates": {
                "type": "array",
                "items": {
                    "type": "object", "additionalProperties": False,
                    "required": [
                        "manufacturer_name", "brand_name", "family_name",
                        "model", "part_number", "product_url", "official_host", "properties",
                    ],
                    "properties": {
                        "manufacturer_name": text, "brand_name": text,
                        "family_name": text, "model": text, "part_number": text,
                        "product_url": {"type": "string"},
                        "official_host": text,
                        "properties": {
                            "type": "array",
                            "items": {
                                "type": "object", "additionalProperties": False,
                                "required": [
                                    "property_name", "value", "unit", "source_locator",
                                    "evidence_text",
                                ],
                                "properties": {
                                    "property_name": text, "value": text,
                                    "unit": {"type": ["string", "null"]},
                                    "source_locator": text,
                                    "evidence_text": {"type": "string"},
                                },
                            },
                        },
                    },
                },
            }
        },
    }


def _asset_draft_schema() -> dict[str, object]:
    text = {"type": "string"}
    flags = (
        "automatic_selection_performed", "automatic_download_performed",
        "catalog_registration_performed", "drawing_mutation_performed",
    )
    return {
        "type": "object", "additionalProperties": False,
        "required": ["candidates", *flags],
        "properties": {
            "candidates": {
                "type": "array",
                "items": {
                    "type": "object", "additionalProperties": False,
                    "required": [
                        "candidate_id", "kind", "part_number", "title", "locator",
                        "official_host", "media_type", "source_page_url", "source_retrieved_on",
                    ],
                    "properties": {
                        "candidate_id": text,
                        "kind": {"type": "string", "enum": ["data_sheet", "cad_2d", "cad_3d", "image"]},
                        "part_number": text, "title": text, "locator": text,
                        "official_host": text,
                        "media_type": {"type": "string", "enum": [
                            "application/pdf", "application/dxf", "application/acad",
                            "model/step", "application/step", "model/iges",
                            "image/jpeg", "image/png",
                        ]},
                        "source_page_url": text, "source_retrieved_on": text,
                    },
                },
            },
            **{name: {"type": "boolean", "const": False} for name in flags},
        },
    }


class _NoRedirectHandler(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):  # type: ignore[no-untyped-def]
        return None


def _https_get_text(url: str, maximum_bytes: int) -> tuple[str, bytes]:
    request = urllib.request.Request(
        url,
        headers={"Accept": "text/html, application/xhtml+xml, application/json, text/plain"},
        method="GET",
    )
    opener = urllib.request.build_opener(
        urllib.request.ProxyHandler({}),
        _NoRedirectHandler(),
        urllib.request.HTTPSHandler(context=ssl.create_default_context()),
    )
    try:
        with opener.open(request, timeout=30) as response:
            if getattr(response, "status", 200) != 200:
                raise ManufacturerEvidenceError("Official product page returned a non-success status.")
            content = response.read(maximum_bytes + 1)
            media_type = response.headers.get_content_type().lower()
    except urllib.error.HTTPError as exc:
        if 300 <= exc.code < 400:
            raise ManufacturerEvidenceError("Official product-page redirect requires new review.") from exc
        if exc.code in {403, 503}:
            raise OfficialPageAccessRefused(
                f"Official product page returned HTTP {exc.code}."
            ) from exc
        raise ManufacturerEvidenceError(f"Official product page returned HTTP {exc.code}.") from exc
    except urllib.error.URLError as exc:
        raise ManufacturerEvidenceError("Official product page could not be reached.") from exc
    if len(content) > maximum_bytes:
        raise ManufacturerEvidenceError("Official product page exceeds its safe byte limit.")
    return media_type, content


def _resolve_addresses(host: str) -> Iterable[str]:
    try:
        return (item[4][0] for item in socket.getaddrinfo(host, 443, type=socket.SOCK_STREAM))
    except socket.gaierror as exc:
        raise ManufacturerEvidenceError("Official manufacturer host could not be resolved.") from exc


def _require_public_ip(value: str) -> None:
    try:
        address = ipaddress.ip_address(value)
    except ValueError as exc:
        raise ManufacturerEvidenceError("Official manufacturer host resolved to an invalid address.") from exc
    if not address.is_global:
        raise ManufacturerEvidenceError("Official manufacturer host resolved to a non-public address.")


def _normalize(value: str) -> str:
    return re.sub(r"\s+", " ", value).strip().casefold()


class _TextExtractor(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.parts: list[str] = []

    def handle_data(self, data: str) -> None:
        self.parts.append(data)


def _visible_text(value: str) -> str:
    parser = _TextExtractor()
    parser.feed(value)
    parser.close()
    return " ".join(parser.parts)
