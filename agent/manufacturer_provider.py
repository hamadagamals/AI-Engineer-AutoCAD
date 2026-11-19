"""Bounded opt-in provider adapter for official manufacturer discovery only."""

from __future__ import annotations

import json
import logging
import re
import threading
from collections import OrderedDict
from concurrent.futures import ThreadPoolExecutor, TimeoutError as FutureTimeoutError
from typing import Callable
from urllib.parse import urlsplit

from agent.exceptions import ManufacturerEvidenceError
from core.manufacturer_discovery import (
    CandidateProperty,
    ManufacturerCandidate,
    ManufacturerDiscoveryRequest,
)
from core.manufacturer_asset_collection import (
    ManufacturerAssetCandidate,
    ManufacturerAssetKind,
    ManufacturerAssetSearchRequest,
)


_NAME = re.compile(r"[a-z][a-z0-9_.:-]{0,127}")
_OUTPUT_KEYS = {
    "candidates", "automatic_selection_performed", "automatic_download_performed",
    "catalog_registration_performed", "drawing_mutation_performed",
}
_CANDIDATE_KEYS = set(ManufacturerCandidate.__dataclass_fields__)
_PROPERTY_KEYS = set(CandidateProperty.__dataclass_fields__)


class ManufacturerProviderAdapter:
    """Invoke bounded search and reuse exact request-hash results within one process."""

    def __init__(
        self, name: str, search: Callable[[ManufacturerDiscoveryRequest], str], *,
        asset_search: Callable[[ManufacturerAssetSearchRequest], str] | None = None,
        timeout_seconds: int = 30, maximum_concurrent_calls: int = 1,
        maximum_cached_requests: int = 32,
    ) -> None:
        if not isinstance(name, str) or _NAME.fullmatch(name) is None:
            raise ManufacturerEvidenceError("Manufacturer provider name is invalid.")
        if not callable(search):
            raise ManufacturerEvidenceError("Manufacturer provider callback is invalid.")
        if not isinstance(timeout_seconds, int) or not 1 <= timeout_seconds <= 180:
            raise ManufacturerEvidenceError("Manufacturer provider timeout is invalid.")
        if not isinstance(maximum_concurrent_calls, int) or not 1 <= maximum_concurrent_calls <= 2:
            raise ManufacturerEvidenceError("Manufacturer provider concurrency is invalid.")
        if (
            not isinstance(maximum_cached_requests, int)
            or isinstance(maximum_cached_requests, bool)
            or not 1 <= maximum_cached_requests <= 128
        ):
            raise ManufacturerEvidenceError("Manufacturer provider cache limit is invalid.")
        self.name = name
        self.timeout_seconds = timeout_seconds
        self.maximum_cached_requests = maximum_cached_requests
        self._search = search
        self._asset_search = asset_search
        self._slots = threading.BoundedSemaphore(maximum_concurrent_calls)
        self._cache_lock = threading.Lock()
        self._cache: OrderedDict[str, tuple[ManufacturerCandidate, ...]] = OrderedDict()
        self._asset_cache: OrderedDict[str, tuple[ManufacturerAssetCandidate, ...]] = OrderedDict()
        self._logger = logging.getLogger("ai_engineer.manufacturer_provider")
        self._executor = ThreadPoolExecutor(
            max_workers=maximum_concurrent_calls,
            thread_name_prefix="ai-engineer-manufacturer-provider",
        )

    def discover(
        self, request: ManufacturerDiscoveryRequest
    ) -> tuple[ManufacturerCandidate, ...]:
        if not isinstance(request, ManufacturerDiscoveryRequest):
            raise ManufacturerEvidenceError("Manufacturer provider requires a typed request.")
        cached = self._cached(request.request_sha256)
        if cached is not None:
            self._logger.info(
                "manufacturer_provider_cache_hit",
                extra={"provider_name": self.name, "request_sha256": request.request_sha256},
            )
            return cached
        if not self._slots.acquire(blocking=False):
            raise ManufacturerEvidenceError("Manufacturer provider concurrency limit reached.")
        try:
            # Another bounded call may have completed while this caller waited for its slot.
            cached = self._cached(request.request_sha256)
            if cached is not None:
                return cached
            future = self._executor.submit(self._search, request)
            try:
                raw = future.result(timeout=self.timeout_seconds)
            except FutureTimeoutError as exc:
                future.cancel()
                raise ManufacturerEvidenceError("Manufacturer provider timed out.") from exc
            except ManufacturerEvidenceError:
                raise
            except Exception as exc:
                raise ManufacturerEvidenceError(
                    f"Manufacturer provider failed: {type(exc).__name__}."
                ) from exc
            candidates = _parse_output(raw, request)
            with self._cache_lock:
                self._cache[request.request_sha256] = candidates
                self._cache.move_to_end(request.request_sha256)
                while len(self._cache) > self.maximum_cached_requests:
                    self._cache.popitem(last=False)
            return candidates
        finally:
            self._slots.release()

    def _cached(
        self, request_sha256: str
    ) -> tuple[ManufacturerCandidate, ...] | None:
        with self._cache_lock:
            cached = self._cache.get(request_sha256)
            if cached is not None:
                self._cache.move_to_end(request_sha256)
            return cached

    def discover_assets(
        self, request: ManufacturerAssetSearchRequest
    ) -> tuple[ManufacturerAssetCandidate, ...]:
        """Return bounded asset candidates; never select or download them."""
        if not isinstance(request, ManufacturerAssetSearchRequest):
            raise ManufacturerEvidenceError("Manufacturer asset provider requires a typed request.")
        if self._asset_search is None:
            raise ManufacturerEvidenceError("Manufacturer asset discovery is not configured.")
        with self._cache_lock:
            cached = self._asset_cache.get(request.request_sha256)
            if cached is not None:
                self._asset_cache.move_to_end(request.request_sha256)
                return cached
        if not self._slots.acquire(blocking=False):
            raise ManufacturerEvidenceError("Manufacturer provider concurrency limit reached.")
        try:
            future = self._executor.submit(self._asset_search, request)
            try:
                raw = future.result(timeout=self.timeout_seconds)
            except FutureTimeoutError as exc:
                future.cancel()
                raise ManufacturerEvidenceError("Manufacturer asset provider timed out.") from exc
            except ManufacturerEvidenceError:
                raise
            except Exception as exc:
                raise ManufacturerEvidenceError(
                    f"Manufacturer asset provider failed: {type(exc).__name__}."
                ) from exc
            candidates = _parse_asset_output(raw, request)
            with self._cache_lock:
                self._asset_cache[request.request_sha256] = candidates
                self._asset_cache.move_to_end(request.request_sha256)
                while len(self._asset_cache) > self.maximum_cached_requests:
                    self._asset_cache.popitem(last=False)
            return candidates
        finally:
            self._slots.release()


def _parse_output(
    raw: object, request: ManufacturerDiscoveryRequest
) -> tuple[ManufacturerCandidate, ...]:
    if not isinstance(raw, str) or not 1 <= len(raw.encode("utf-8")) <= 262_144:
        raise ManufacturerEvidenceError("Manufacturer provider output exceeds its text limit.")
    try:
        value = json.loads(raw)
    except json.JSONDecodeError as exc:
        raise ManufacturerEvidenceError("Manufacturer provider output is not valid JSON.") from exc
    if not isinstance(value, dict) or set(value) != _OUTPUT_KEYS:
        raise ManufacturerEvidenceError("Manufacturer provider output fields are invalid.")
    if any(value[name] is not False for name in _OUTPUT_KEYS if name != "candidates"):
        raise ManufacturerEvidenceError("Manufacturer provider claimed an unauthorized action.")
    raw_candidates = value["candidates"]
    if not isinstance(raw_candidates, list) or not 1 <= len(raw_candidates) <= 32:
        raise ManufacturerEvidenceError("Manufacturer provider candidate count is invalid.")
    candidates = []
    for raw_candidate in raw_candidates:
        if not isinstance(raw_candidate, dict) or set(raw_candidate) != _CANDIDATE_KEYS:
            raise ManufacturerEvidenceError("Manufacturer provider candidate fields are invalid.")
        raw_properties = raw_candidate["properties"]
        if not isinstance(raw_properties, list):
            raise ManufacturerEvidenceError("Manufacturer provider properties are invalid.")
        properties = []
        for raw_property in raw_properties:
            if not isinstance(raw_property, dict) or set(raw_property) != _PROPERTY_KEYS:
                raise ManufacturerEvidenceError("Manufacturer provider property fields are invalid.")
            properties.append(CandidateProperty(**raw_property))
        candidate = ManufacturerCandidate(
            **{**raw_candidate, "properties": tuple(properties)}
        )
        if candidate.official_host not in request.official_domains:
            raise ManufacturerEvidenceError("Manufacturer provider returned an unapproved host.")
        candidates.append(candidate)
    ids = [item.candidate_id for item in candidates]
    identities = [(item.official_host, item.part_number) for item in candidates]
    if len(ids) != len(set(ids)) or len(identities) != len(set(identities)):
        raise ManufacturerEvidenceError("Manufacturer provider candidates must be unique.")
    return tuple(candidates)


def _parse_asset_output(
    raw: object, request: ManufacturerAssetSearchRequest
) -> tuple[ManufacturerAssetCandidate, ...]:
    if not isinstance(raw, str) or not 1 <= len(raw.encode("utf-8")) <= 262_144:
        raise ManufacturerEvidenceError("Manufacturer asset provider output exceeds its text limit.")
    try:
        value = json.loads(raw)
    except json.JSONDecodeError as exc:
        raise ManufacturerEvidenceError("Manufacturer asset provider output is not valid JSON.") from exc
    expected = {
        "candidates", "automatic_selection_performed", "automatic_download_performed",
        "catalog_registration_performed", "drawing_mutation_performed",
    }
    if not isinstance(value, dict) or set(value) != expected:
        raise ManufacturerEvidenceError("Manufacturer asset provider output fields are invalid.")
    if any(value[name] is not False for name in expected if name != "candidates"):
        raise ManufacturerEvidenceError("Manufacturer asset provider claimed an unauthorized action.")
    raw_candidates = value["candidates"]
    if not isinstance(raw_candidates, list) or not 1 <= len(raw_candidates) <= 64:
        raise ManufacturerEvidenceError("Manufacturer asset provider candidate count is invalid.")
    candidates: list[ManufacturerAssetCandidate] = []
    for item in raw_candidates:
        if not isinstance(item, dict) or set(item) != set(ManufacturerAssetCandidate.__dataclass_fields__):
            raise ManufacturerEvidenceError("Manufacturer asset provider candidate fields are invalid.")
        try:
            candidate = ManufacturerAssetCandidate(
                **{**item, "kind": ManufacturerAssetKind(item["kind"])}
            )
        except ValueError as exc:
            raise ManufacturerEvidenceError("Manufacturer asset provider kind is invalid.") from exc
        if candidate.part_number.casefold() != request.part_number.casefold():
            raise ManufacturerEvidenceError("Manufacturer asset provider changed the part number.")
        if candidate.official_host not in request.official_domains:
            raise ManufacturerEvidenceError("Manufacturer asset provider returned an unapproved host.")
        source_host = urlsplit(candidate.source_page_url).hostname
        if source_host not in request.official_domains:
            raise ManufacturerEvidenceError(
                "Manufacturer asset provider returned an unapproved source-page host."
            )
        if candidate.kind not in request.requested_kinds:
            raise ManufacturerEvidenceError("Manufacturer asset provider returned an unrequested kind.")
        candidates.append(candidate)
    ids = [item.candidate_id for item in candidates]
    if len(ids) != len(set(ids)):
        raise ManufacturerEvidenceError("Manufacturer asset provider candidates must be unique.")
    return tuple(candidates)
