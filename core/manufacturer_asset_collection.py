"""Hash-bound discovery and ordered collection contracts for manufacturer assets."""

from __future__ import annotations

import hashlib
import json
import re
from dataclasses import dataclass
from enum import Enum
from pathlib import Path
from typing import Any, Callable
from urllib.parse import urlsplit

from agent.exceptions import ManufacturerEvidenceError
from core.electrical_model import JsonRecord
from integrations.manufacturer_retrieval import (
    QuarantineRecord,
    create_retrieval_request,
    retrieve_to_quarantine,
)


REQUEST_VERSION = "m23.manufacturer_asset_search_request.v1"
RESULT_VERSION = "m23.manufacturer_asset_search_result.v1"
APPROVAL_VERSION = "m23.manufacturer_asset_collection_approval.v1"
COLLECTION_VERSION = "m23.manufacturer_asset_collection.v1"
_IDENTIFIER = re.compile(r"[a-z][a-z0-9_.:-]{0,127}")
_SHA256 = re.compile(r"[0-9a-f]{64}")
_PART_SAFE = re.compile(r"[^A-Za-z0-9._+-]+")


class ManufacturerAssetKind(str, Enum):
    DATA_SHEET = "data_sheet"
    CAD_2D = "cad_2d"
    CAD_3D = "cad_3d"
    IMAGE = "image"


SUPPORTED_MEDIA_TYPES = frozenset({
    "application/pdf", "application/dxf", "application/acad",
    "model/step", "application/step", "model/iges", "image/jpeg", "image/png",
})


@dataclass(frozen=True, slots=True)
class ManufacturerAssetSearchRequest(JsonRecord):
    schema_version: int
    request_version: str
    request_id: str
    project_id: str
    manufacturer_name: str
    part_number: str
    product_page_url: str
    product_evidence_sha256: str
    official_domains: tuple[str, ...]
    requested_kinds: tuple[ManufacturerAssetKind, ...]
    approved_by: str
    approved_on: str
    egress_approved: bool
    automatic_download_approved: bool
    catalog_registration_approved: bool
    drawing_mutation_approved: bool
    request_sha256: str

    def __post_init__(self) -> None:
        if self.schema_version != 1 or self.request_version != REQUEST_VERSION:
            raise ManufacturerEvidenceError("Unsupported manufacturer asset-search request version.")
        _identifier(self.request_id, "asset-search request ID")
        _identifier(self.project_id, "asset-search project ID")
        _text(self.manufacturer_name, "asset-search manufacturer", 256)
        _text(self.part_number, "asset-search part number", 128)
        _sha256(self.product_evidence_sha256, "product evidence hash")
        product = _safe_url(self.product_page_url)
        if not isinstance(self.official_domains, tuple) or not 1 <= len(self.official_domains) <= 32:
            raise ManufacturerEvidenceError("Asset-search official domains are invalid.")
        if len(self.official_domains) != len(set(self.official_domains)):
            raise ManufacturerEvidenceError("Asset-search official domains must be unique.")
        for domain in self.official_domains:
            _domain(domain)
        if product.hostname not in self.official_domains:
            raise ManufacturerEvidenceError("Product page host is not an approved official domain.")
        if not isinstance(self.requested_kinds, tuple) or not 1 <= len(self.requested_kinds) <= 4:
            raise ManufacturerEvidenceError("Requested manufacturer asset kinds are invalid.")
        if len(self.requested_kinds) != len(set(self.requested_kinds)):
            raise ManufacturerEvidenceError("Requested manufacturer asset kinds must be unique.")
        if any(not isinstance(item, ManufacturerAssetKind) for item in self.requested_kinds):
            raise ManufacturerEvidenceError("Requested manufacturer asset kind is invalid.")
        _text(self.approved_by, "asset-search approver", 128)
        _date(self.approved_on, "asset-search approval date")
        if self.egress_approved is not True:
            raise ManufacturerEvidenceError("Manufacturer asset search requires explicit egress approval.")
        if self.automatic_download_approved or self.catalog_registration_approved or self.drawing_mutation_approved:
            raise ManufacturerEvidenceError("Asset search cannot approve download, catalog, or drawing actions.")
        _verify_hash(self, "request_sha256", "Manufacturer asset-search request")


@dataclass(frozen=True, slots=True)
class ManufacturerAssetCandidate(JsonRecord):
    candidate_id: str
    kind: ManufacturerAssetKind
    part_number: str
    title: str
    locator: str
    official_host: str
    media_type: str
    source_page_url: str
    source_retrieved_on: str

    def __post_init__(self) -> None:
        _identifier(self.candidate_id, "asset candidate ID")
        if not isinstance(self.kind, ManufacturerAssetKind):
            raise ManufacturerEvidenceError("Manufacturer asset candidate kind is invalid.")
        _text(self.part_number, "asset candidate part number", 128)
        _text(self.title, "asset candidate title", 256)
        asset = _safe_url(self.locator)
        _safe_url(self.source_page_url)
        _domain(self.official_host)
        if asset.hostname != self.official_host:
            raise ManufacturerEvidenceError("Asset candidate URL does not match its official host.")
        if self.media_type not in SUPPORTED_MEDIA_TYPES:
            raise ManufacturerEvidenceError("Manufacturer asset candidate media type is unsupported.")
        _date(self.source_retrieved_on, "asset candidate retrieval date")


@dataclass(frozen=True, slots=True)
class ManufacturerAssetSearchResult(JsonRecord):
    schema_version: int
    result_version: str
    result_id: str
    request_sha256: str
    provider_name: str
    candidates: tuple[ManufacturerAssetCandidate, ...]
    complete: bool
    provider_egress_performed: bool
    automatic_selection_performed: bool
    automatic_download_performed: bool
    catalog_registration_performed: bool
    drawing_mutation_performed: bool
    result_sha256: str

    def __post_init__(self) -> None:
        if self.schema_version != 1 or self.result_version != RESULT_VERSION:
            raise ManufacturerEvidenceError("Unsupported manufacturer asset-search result version.")
        _identifier(self.result_id, "asset-search result ID")
        _sha256(self.request_sha256, "asset-search request hash")
        _identifier(self.provider_name, "asset-search provider")
        if not isinstance(self.candidates, tuple) or not 1 <= len(self.candidates) <= 64:
            raise ManufacturerEvidenceError("Manufacturer asset candidates are invalid.")
        if any(not isinstance(item, ManufacturerAssetCandidate) for item in self.candidates):
            raise ManufacturerEvidenceError("Manufacturer asset candidate is invalid.")
        ids = [item.candidate_id for item in self.candidates]
        if len(ids) != len(set(ids)):
            raise ManufacturerEvidenceError("Manufacturer asset candidate IDs must be unique.")
        if self.complete is not True or self.provider_egress_performed is not True:
            raise ManufacturerEvidenceError("Manufacturer asset search result is incomplete.")
        if self.automatic_selection_performed or self.automatic_download_performed or self.catalog_registration_performed or self.drawing_mutation_performed:
            raise ManufacturerEvidenceError("Asset search result claimed an unauthorized action.")
        _verify_hash(self, "result_sha256", "Manufacturer asset-search result")


@dataclass(frozen=True, slots=True)
class ManufacturerAssetCollectionApproval(JsonRecord):
    schema_version: int
    approval_version: str
    approval_id: str
    result_sha256: str
    selected_candidate_ids: tuple[str, ...]
    approved_by: str
    approved_on: str
    maximum_bytes_per_asset: int
    download_approved: bool
    catalog_registration_approved: bool
    drawing_mutation_approved: bool
    approval_sha256: str

    def __post_init__(self) -> None:
        if self.schema_version != 1 or self.approval_version != APPROVAL_VERSION:
            raise ManufacturerEvidenceError("Unsupported asset collection approval version.")
        _identifier(self.approval_id, "asset collection approval ID")
        _sha256(self.result_sha256, "asset search-result hash")
        if not isinstance(self.selected_candidate_ids, tuple) or not 1 <= len(self.selected_candidate_ids) <= 16:
            raise ManufacturerEvidenceError("Selected manufacturer assets are invalid.")
        if len(self.selected_candidate_ids) != len(set(self.selected_candidate_ids)):
            raise ManufacturerEvidenceError("Selected manufacturer assets must be unique.")
        for item in self.selected_candidate_ids:
            _identifier(item, "selected asset candidate ID")
        _text(self.approved_by, "asset collection approver", 128)
        _date(self.approved_on, "asset collection approval date")
        if not 1_024 <= self.maximum_bytes_per_asset <= 100_000_000:
            raise ManufacturerEvidenceError("Asset collection byte limit is invalid.")
        if self.download_approved is not True:
            raise ManufacturerEvidenceError("Asset collection requires explicit download approval.")
        if self.catalog_registration_approved or self.drawing_mutation_approved:
            raise ManufacturerEvidenceError("Asset collection cannot approve catalog or drawing actions.")
        _verify_hash(self, "approval_sha256", "Manufacturer asset collection approval")


@dataclass(frozen=True, slots=True)
class ManufacturerAssetCollectionResult(JsonRecord):
    schema_version: int
    collection_version: str
    collection_id: str
    request_sha256: str
    result_sha256: str
    approval_sha256: str
    manufacturer_name: str
    part_number: str
    library_relative_root: str
    records: tuple[QuarantineRecord, ...]
    complete: bool
    retrieval_performed: bool
    quarantine_write_performed: bool
    catalog_registration_performed: bool
    drawing_mutation_performed: bool
    collection_sha256: str

    def __post_init__(self) -> None:
        if self.schema_version != 1 or self.collection_version != COLLECTION_VERSION:
            raise ManufacturerEvidenceError("Unsupported manufacturer asset collection version.")
        _identifier(self.collection_id, "asset collection ID")
        for value in (self.request_sha256, self.result_sha256, self.approval_sha256):
            _sha256(value, "asset collection lineage hash")
        _text(self.manufacturer_name, "asset collection manufacturer", 256)
        _text(self.part_number, "asset collection part number", 128)
        relative = Path(self.library_relative_root)
        if relative.is_absolute() or ".." in relative.parts or len(relative.parts) != 2:
            raise ManufacturerEvidenceError("Manufacturer asset library root is unsafe.")
        if not isinstance(self.records, tuple) or not self.records:
            raise ManufacturerEvidenceError("Manufacturer asset collection records are invalid.")
        if self.complete is not True or self.retrieval_performed is not True or self.quarantine_write_performed is not True:
            raise ManufacturerEvidenceError("Manufacturer asset collection is incomplete.")
        if self.catalog_registration_performed or self.drawing_mutation_performed:
            raise ManufacturerEvidenceError("Asset collection cannot register or mutate a drawing.")
        _verify_hash(self, "collection_sha256", "Manufacturer asset collection")


def create_asset_search_request(**kwargs: Any) -> ManufacturerAssetSearchRequest:
    payload = {
        "schema_version": 1, "request_version": REQUEST_VERSION,
        **kwargs, "automatic_download_approved": False,
        "catalog_registration_approved": False, "drawing_mutation_approved": False,
    }
    return ManufacturerAssetSearchRequest(**payload, request_sha256=_hash(payload))


def parse_asset_search_request(value: object) -> ManufacturerAssetSearchRequest:
    item = _exact_mapping(value, ManufacturerAssetSearchRequest, "asset-search request")
    domains = item["official_domains"]
    kinds = item["requested_kinds"]
    if not isinstance(domains, list) or not isinstance(kinds, list):
        raise ManufacturerEvidenceError("Asset-search request arrays are invalid.")
    try:
        return ManufacturerAssetSearchRequest(**{
            **item, "official_domains": tuple(domains),
            "requested_kinds": tuple(ManufacturerAssetKind(kind) for kind in kinds),
        })
    except ValueError as exc:
        raise ManufacturerEvidenceError("Requested manufacturer asset kind is invalid.") from exc


def create_asset_search_result(
    request: ManufacturerAssetSearchRequest, candidates: tuple[ManufacturerAssetCandidate, ...],
    *, result_id: str, provider_name: str,
) -> ManufacturerAssetSearchResult:
    if any(item.part_number.casefold() != request.part_number.casefold() for item in candidates):
        raise ManufacturerEvidenceError("Asset candidate part number differs from the approved product.")
    if any(item.kind not in request.requested_kinds for item in candidates):
        raise ManufacturerEvidenceError("Asset candidate kind was not requested.")
    if any(
        item.official_host not in request.official_domains
        or _safe_url(item.source_page_url).hostname not in request.official_domains
        for item in candidates
    ):
        raise ManufacturerEvidenceError("Asset candidate or source-page host was not approved.")
    payload = {
        "schema_version": 1, "result_version": RESULT_VERSION, "result_id": result_id,
        "request_sha256": request.request_sha256, "provider_name": provider_name,
        "candidates": candidates, "complete": True, "provider_egress_performed": True,
        "automatic_selection_performed": False, "automatic_download_performed": False,
        "catalog_registration_performed": False, "drawing_mutation_performed": False,
    }
    return ManufacturerAssetSearchResult(**payload, result_sha256=_hash(payload))


def parse_asset_search_result(value: object) -> ManufacturerAssetSearchResult:
    item = _exact_mapping(value, ManufacturerAssetSearchResult, "asset-search result")
    raw = item["candidates"]
    if not isinstance(raw, list):
        raise ManufacturerEvidenceError("Asset-search candidates must be an array.")
    try:
        candidates = tuple(
            ManufacturerAssetCandidate(**{**candidate, "kind": ManufacturerAssetKind(candidate["kind"])})
            for candidate in raw if isinstance(candidate, dict)
        )
    except (KeyError, TypeError, ValueError) as exc:
        raise ManufacturerEvidenceError("Asset-search candidate fields are invalid.") from exc
    if len(candidates) != len(raw):
        raise ManufacturerEvidenceError("Asset-search candidate fields are invalid.")
    return ManufacturerAssetSearchResult(**{**item, "candidates": candidates})


def approve_asset_collection(
    result: ManufacturerAssetSearchResult, *, approval_id: str,
    selected_candidate_ids: tuple[str, ...], approved_by: str, approved_on: str,
    maximum_bytes_per_asset: int = 50_000_000,
) -> ManufacturerAssetCollectionApproval:
    available = {item.candidate_id for item in result.candidates}
    if any(item not in available for item in selected_candidate_ids):
        raise ManufacturerEvidenceError("Selected manufacturer asset is not in the search result.")
    payload = {
        "schema_version": 1, "approval_version": APPROVAL_VERSION,
        "approval_id": approval_id, "result_sha256": result.result_sha256,
        "selected_candidate_ids": selected_candidate_ids, "approved_by": approved_by,
        "approved_on": approved_on, "maximum_bytes_per_asset": maximum_bytes_per_asset,
        "download_approved": True, "catalog_registration_approved": False,
        "drawing_mutation_approved": False,
    }
    return ManufacturerAssetCollectionApproval(**payload, approval_sha256=_hash(payload))


def parse_asset_collection_approval(value: object) -> ManufacturerAssetCollectionApproval:
    item = _exact_mapping(value, ManufacturerAssetCollectionApproval, "asset collection approval")
    selected = item["selected_candidate_ids"]
    if not isinstance(selected, list):
        raise ManufacturerEvidenceError("Selected manufacturer assets must be an array.")
    return ManufacturerAssetCollectionApproval(**{**item, "selected_candidate_ids": tuple(selected)})


def collect_approved_assets(
    request: ManufacturerAssetSearchRequest, result: ManufacturerAssetSearchResult,
    approval: ManufacturerAssetCollectionApproval, library_root: Path, *,
    retrieved_on: str, retriever: Callable[..., QuarantineRecord] = retrieve_to_quarantine,
) -> ManufacturerAssetCollectionResult:
    if result.request_sha256 != request.request_sha256 or approval.result_sha256 != result.result_sha256:
        raise ManufacturerEvidenceError("Manufacturer asset collection lineage does not match.")
    brand = _safe_segment(request.manufacturer_name)
    part = _safe_segment(request.part_number)
    relative_root = Path(brand) / part
    target_root = library_root.resolve(strict=False) / relative_root
    selected = {item.candidate_id: item for item in result.candidates}
    records: list[QuarantineRecord] = []
    for order, candidate_id in enumerate(approval.selected_candidate_ids, start=1):
        candidate = selected[candidate_id]
        folder = target_root / f"{order:02d}-{candidate.kind.value}"
        retrieval = create_retrieval_request(
            request_id=f"retrieval:{approval.approval_id}:{order}",
            manufacturer_id=f"manufacturer:{brand}", product_id=f"product:{brand}:{part}",
            artifact_id=f"artifact:{brand}:{part}:{order}", locator=candidate.locator,
            approved_host=candidate.official_host,
            expected_media_types=(candidate.media_type,),
            maximum_bytes=approval.maximum_bytes_per_asset,
            expected_content_sha256=None, approved_by=approval.approved_by,
            approved_on=approval.approved_on,
        )
        records.append(retriever(retrieval, folder, retrieved_on=retrieved_on))
    payload = {
        "schema_version": 1, "collection_version": COLLECTION_VERSION,
        "collection_id": f"collection:{approval.approval_id}",
        "request_sha256": request.request_sha256, "result_sha256": result.result_sha256,
        "approval_sha256": approval.approval_sha256,
        "manufacturer_name": request.manufacturer_name, "part_number": request.part_number,
        "library_relative_root": relative_root.as_posix(), "records": tuple(records),
        "complete": True, "retrieval_performed": True, "quarantine_write_performed": True,
        "catalog_registration_performed": False, "drawing_mutation_performed": False,
    }
    return ManufacturerAssetCollectionResult(**payload, collection_sha256=_hash(payload))


def _safe_segment(value: str) -> str:
    result = _PART_SAFE.sub("-", value).strip("-.").lower()[:64]
    if not result or not result[0].isalnum():
        raise ManufacturerEvidenceError("Manufacturer asset library segment is invalid.")
    return result


def _safe_url(value: str):
    if not isinstance(value, str) or value != value.strip() or len(value) > 2048:
        raise ManufacturerEvidenceError("Manufacturer asset URL is invalid.")
    parsed = urlsplit(value)
    if parsed.scheme != "https" or not parsed.hostname or parsed.username or parsed.password or parsed.port not in (None, 443) or parsed.fragment:
        raise ManufacturerEvidenceError("Manufacturer asset URL must be safe HTTPS.")
    return parsed


def _domain(value: str) -> None:
    if not isinstance(value, str) or value != value.lower() or not re.fullmatch(r"(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}", value):
        raise ManufacturerEvidenceError("Manufacturer official domain is invalid.")


def _identifier(value: str, name: str) -> None:
    if not isinstance(value, str) or _IDENTIFIER.fullmatch(value) is None:
        raise ManufacturerEvidenceError(f"{name} is invalid.")


def _text(value: str, name: str, maximum: int) -> None:
    if not isinstance(value, str) or not value.strip() or value != value.strip() or len(value.encode()) > maximum:
        raise ManufacturerEvidenceError(f"{name} is invalid.")


def _date(value: str, name: str) -> None:
    if not isinstance(value, str) or re.fullmatch(r"\d{4}-\d{2}-\d{2}", value) is None:
        raise ManufacturerEvidenceError(f"{name} is invalid.")


def _sha256(value: str, name: str) -> None:
    if not isinstance(value, str) or _SHA256.fullmatch(value) is None:
        raise ManufacturerEvidenceError(f"{name} is invalid.")


def _hash(value: object) -> str:
    def convert(item: object) -> object:
        if isinstance(item, Enum): return item.value
        if isinstance(item, JsonRecord): return item.to_dict()
        if isinstance(item, tuple): return [convert(child) for child in item]
        if isinstance(item, dict): return {str(k): convert(v) for k, v in item.items()}
        return item
    return hashlib.sha256(json.dumps(convert(value), ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def _verify_hash(record: JsonRecord, field: str, name: str) -> None:
    value = getattr(record, field)
    _sha256(value, f"{name} hash")
    payload = record.to_dict()
    del payload[field]
    if value != _hash(payload):
        raise ManufacturerEvidenceError(f"{name} SHA-256 does not match content.")


def _exact_mapping(value: object, record_type: type, name: str) -> dict[str, Any]:
    if not isinstance(value, dict) or set(value) != set(record_type.__dataclass_fields__):
        raise ManufacturerEvidenceError(f"Manufacturer {name} fields are invalid.")
    return dict(value)
