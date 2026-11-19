"""Approved exact-URL manufacturer downloads into a bounded local quarantine."""

from __future__ import annotations

import hashlib
import ipaddress
import json
import os
import re
import socket
import ssl
import tempfile
import urllib.error
import urllib.request
from dataclasses import dataclass
from datetime import date
from enum import Enum
from pathlib import Path
from typing import Any, BinaryIO, Callable, Iterable, Mapping
from urllib.parse import urlsplit

from agent.exceptions import ManufacturerEvidenceError
from core.electrical_model import JsonRecord


REQUEST_VERSION = "m23.manufacturer_retrieval_request.v1"
RECORD_VERSION = "m23.quarantine_record.v1"
_IDENTIFIER = re.compile(r"[a-z][a-z0-9_.:-]{0,127}")
_SHA256 = re.compile(r"[0-9a-f]{64}")
_MEDIA_TYPES = {
    "application/pdf": ".pdf",
    "application/dxf": ".dxf",
    "application/acad": ".dwg",
    "model/step": ".stp",
    "application/step": ".stp",
    "model/iges": ".igs",
    "image/jpeg": ".jpg",
    "image/png": ".png",
}


@dataclass(frozen=True, slots=True)
class ManufacturerRetrievalRequest(JsonRecord):
    """One self-hashed engineer approval for one immutable HTTPS locator."""

    schema_version: int
    request_version: str
    request_id: str
    manufacturer_id: str
    product_id: str
    artifact_id: str
    locator: str
    approved_host: str
    expected_media_types: tuple[str, ...]
    maximum_bytes: int
    expected_content_sha256: str | None
    approved_by: str
    approved_on: str
    retrieval_approved: bool
    redirects_approved: bool
    provider_egress_performed: bool
    drawing_mutation_performed: bool
    request_sha256: str

    def __post_init__(self) -> None:
        if self.schema_version != 1 or self.request_version != REQUEST_VERSION:
            raise ManufacturerEvidenceError("Unsupported manufacturer retrieval-request version.")
        for value, name in (
            (self.request_id, "manufacturer retrieval request ID"),
            (self.manufacturer_id, "retrieval manufacturer ID"),
            (self.product_id, "retrieval product ID"),
            (self.artifact_id, "retrieval artifact ID"),
        ):
            _identifier(value, name)
        parsed = _safe_locator(self.locator)
        if self.approved_host != self.approved_host.lower() or parsed.hostname != self.approved_host:
            raise ManufacturerEvidenceError("Retrieval locator does not match the exact approved host.")
        if not isinstance(self.expected_media_types, tuple) or not 1 <= len(self.expected_media_types) <= 4:
            raise ManufacturerEvidenceError("Retrieval expected media types are invalid.")
        if any(item not in _MEDIA_TYPES for item in self.expected_media_types):
            raise ManufacturerEvidenceError("Retrieval expected media type is unsupported.")
        if len(self.expected_media_types) != len(set(self.expected_media_types)):
            raise ManufacturerEvidenceError("Retrieval expected media types must be unique.")
        if (
            not isinstance(self.maximum_bytes, int)
            or isinstance(self.maximum_bytes, bool)
            or not 1_024 <= self.maximum_bytes <= 100_000_000
        ):
            raise ManufacturerEvidenceError("Retrieval byte limit must be 1024 through 100000000.")
        if self.expected_content_sha256 is not None:
            _sha256(self.expected_content_sha256, "expected artifact content hash")
        _text(self.approved_by, "manufacturer retrieval approver", 128)
        _calendar_date(self.approved_on, "manufacturer retrieval approval date")
        if self.retrieval_approved is not True:
            raise ManufacturerEvidenceError("Manufacturer retrieval requires explicit approval.")
        if self.redirects_approved:
            raise ManufacturerEvidenceError("Manufacturer retrieval redirects cannot be pre-approved.")
        if self.provider_egress_performed or self.drawing_mutation_performed:
            raise ManufacturerEvidenceError("Retrieval approval cannot claim provider egress or drawing mutation.")
        _verify_hash(self, "request_sha256", "Manufacturer retrieval request")


@dataclass(frozen=True, slots=True)
class QuarantineRecord(JsonRecord):
    """Self-hashed result for an artifact written only into quarantine."""

    schema_version: int
    record_version: str
    record_id: str
    request_id: str
    request_sha256: str
    artifact_id: str
    locator: str
    approved_host: str
    resolved_addresses: tuple[str, ...]
    media_type: str
    byte_count: int
    content_sha256: str
    quarantine_relative_path: str
    retrieved_on: str
    redirect_performed: bool
    proxy_used: bool
    executable_content_detected: bool
    quarantine_write_performed: bool
    catalog_registration_performed: bool
    drawing_mutation_performed: bool
    record_sha256: str

    def __post_init__(self) -> None:
        if self.schema_version != 1 or self.record_version != RECORD_VERSION:
            raise ManufacturerEvidenceError("Unsupported quarantine-record version.")
        for value, name in (
            (self.record_id, "quarantine record ID"),
            (self.request_id, "quarantine request ID"),
            (self.artifact_id, "quarantine artifact ID"),
        ):
            _identifier(value, name)
        _sha256(self.request_sha256, "quarantine request hash")
        parsed = _safe_locator(self.locator)
        if parsed.hostname != self.approved_host:
            raise ManufacturerEvidenceError("Quarantine locator host differs from approval.")
        if not isinstance(self.resolved_addresses, tuple) or not self.resolved_addresses:
            raise ManufacturerEvidenceError("Quarantine record requires resolved public addresses.")
        for address in self.resolved_addresses:
            _public_ip(address)
        if self.media_type not in _MEDIA_TYPES:
            raise ManufacturerEvidenceError("Quarantine media type is unsupported.")
        if not isinstance(self.byte_count, int) or isinstance(self.byte_count, bool) or self.byte_count < 1:
            raise ManufacturerEvidenceError("Quarantine byte count is invalid.")
        _sha256(self.content_sha256, "quarantine content hash")
        if (
            not isinstance(self.quarantine_relative_path, str)
            or Path(self.quarantine_relative_path).is_absolute()
            or ".." in Path(self.quarantine_relative_path).parts
            or len(Path(self.quarantine_relative_path).parts) != 1
        ):
            raise ManufacturerEvidenceError("Quarantine relative path is unsafe.")
        _calendar_date(self.retrieved_on, "quarantine retrieval date")
        if self.redirect_performed or self.proxy_used or self.executable_content_detected:
            raise ManufacturerEvidenceError("Quarantine record contains a forbidden transport/content state.")
        if self.quarantine_write_performed is not True:
            raise ManufacturerEvidenceError("Quarantine record must confirm its bounded write.")
        if self.catalog_registration_performed or self.drawing_mutation_performed:
            raise ManufacturerEvidenceError("Quarantine retrieval cannot register a catalog item or mutate a drawing.")
        _verify_hash(self, "record_sha256", "Quarantine record")


def create_retrieval_request(
    *, request_id: str, manufacturer_id: str, product_id: str, artifact_id: str,
    locator: str, approved_host: str, expected_media_types: tuple[str, ...],
    maximum_bytes: int, expected_content_sha256: str | None,
    approved_by: str, approved_on: str,
) -> ManufacturerRetrievalRequest:
    """Create exact one-URL approval; it performs no network or filesystem action."""
    payload: dict[str, Any] = {
        "schema_version": 1, "request_version": REQUEST_VERSION,
        "request_id": request_id, "manufacturer_id": manufacturer_id,
        "product_id": product_id, "artifact_id": artifact_id, "locator": locator,
        "approved_host": approved_host, "expected_media_types": expected_media_types,
        "maximum_bytes": maximum_bytes, "expected_content_sha256": expected_content_sha256,
        "approved_by": approved_by, "approved_on": approved_on,
        "retrieval_approved": True, "redirects_approved": False,
        "provider_egress_performed": False, "drawing_mutation_performed": False,
    }
    return ManufacturerRetrievalRequest(**payload, request_sha256=_canonical_sha256(payload))


def parse_retrieval_request(value: object) -> ManufacturerRetrievalRequest:
    """Parse exact JSON request data and revalidate the approval hash."""
    item = _mapping(value, "manufacturer retrieval request")
    _exact_keys(item, ManufacturerRetrievalRequest, "manufacturer retrieval request")
    media = item["expected_media_types"]
    if not isinstance(media, list):
        raise ManufacturerEvidenceError("Retrieval expected media types must be a JSON array.")
    return ManufacturerRetrievalRequest(**{**item, "expected_media_types": tuple(media)})


def parse_quarantine_record(value: object) -> QuarantineRecord:
    """Parse exact JSON quarantine data and revalidate the result hash."""
    item = _mapping(value, "manufacturer quarantine record")
    _exact_keys(item, QuarantineRecord, "manufacturer quarantine record")
    addresses = item["resolved_addresses"]
    if not isinstance(addresses, list):
        raise ManufacturerEvidenceError("Quarantine addresses must be a JSON array.")
    return QuarantineRecord(**{**item, "resolved_addresses": tuple(addresses)})


def retrieve_to_quarantine(
    request: ManufacturerRetrievalRequest,
    quarantine_root: Path,
    *,
    resolver: Callable[[str], Iterable[str]] | None = None,
    transport: Callable[[str, int], tuple[str, BinaryIO]] | None = None,
    retrieved_on: str,
) -> QuarantineRecord:
    """Download one approved URL with no redirects/proxy and atomically quarantine it."""
    if not isinstance(request, ManufacturerRetrievalRequest):
        raise ManufacturerEvidenceError("Manufacturer retrieval requires a typed approved request.")
    _calendar_date(retrieved_on, "manufacturer retrieval date")
    root = quarantine_root.resolve()
    root.mkdir(parents=True, exist_ok=True)
    if root.is_symlink():
        raise ManufacturerEvidenceError("Quarantine root cannot be a symbolic link.")
    resolve = resolver or _resolve_addresses
    addresses = tuple(sorted(set(resolve(request.approved_host))))
    if not addresses:
        raise ManufacturerEvidenceError("Approved manufacturer host resolved to no addresses.")
    for address in addresses:
        _public_ip(address)
    fetch = transport or _https_get
    media_type, stream = fetch(request.locator, request.maximum_bytes)
    normalized_media = _normalize_declared_media_type(media_type, request)
    if normalized_media not in request.expected_media_types:
        stream.close()
        raise ManufacturerEvidenceError("Downloaded media type was not explicitly approved.")
    extension = _MEDIA_TYPES[normalized_media]
    filename = _safe_filename(request.artifact_id) + extension
    target = root / filename
    descriptor, temporary_name = tempfile.mkstemp(prefix=f".{filename}.", dir=root)
    temporary = Path(temporary_name)
    digest = hashlib.sha256()
    byte_count = 0
    prefix = bytearray()
    try:
        with os.fdopen(descriptor, "wb") as output, stream:
            while True:
                chunk = stream.read(min(65_536, request.maximum_bytes + 1 - byte_count))
                if not chunk:
                    break
                if not isinstance(chunk, bytes):
                    raise ManufacturerEvidenceError("Manufacturer transport returned non-byte content.")
                byte_count += len(chunk)
                if byte_count > request.maximum_bytes:
                    raise ManufacturerEvidenceError("Downloaded artifact exceeds its approved byte limit.")
                if len(prefix) < 512:
                    prefix.extend(chunk[: 512 - len(prefix)])
                digest.update(chunk)
                output.write(chunk)
            output.flush()
            os.fsync(output.fileno())
        if byte_count == 0:
            raise ManufacturerEvidenceError("Downloaded artifact is empty.")
        _validate_content_signature(normalized_media, bytes(prefix))
        content_sha256 = digest.hexdigest()
        if request.expected_content_sha256 is not None and content_sha256 != request.expected_content_sha256:
            raise ManufacturerEvidenceError("Downloaded artifact SHA-256 differs from approval.")
        try:
            os.link(temporary, target)
        except FileExistsError as exc:
            raise ManufacturerEvidenceError(
                "Quarantine artifact identity already exists and cannot be overwritten."
            ) from exc
        temporary.unlink()
    except Exception:
        try:
            temporary.unlink()
        except FileNotFoundError:
            pass
        raise
    payload: dict[str, Any] = {
        "schema_version": 1, "record_version": RECORD_VERSION,
        "record_id": f"quarantine:{request.request_id}", "request_id": request.request_id,
        "request_sha256": request.request_sha256, "artifact_id": request.artifact_id,
        "locator": request.locator, "approved_host": request.approved_host,
        "resolved_addresses": addresses, "media_type": normalized_media,
        "byte_count": byte_count, "content_sha256": content_sha256,
        "quarantine_relative_path": filename, "retrieved_on": retrieved_on,
        "redirect_performed": False, "proxy_used": False,
        "executable_content_detected": False, "quarantine_write_performed": True,
        "catalog_registration_performed": False, "drawing_mutation_performed": False,
    }
    return QuarantineRecord(**payload, record_sha256=_canonical_sha256(payload))


class _NoRedirectHandler(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):  # type: ignore[no-untyped-def]
        return None


def _https_get(url: str, maximum_bytes: int) -> tuple[str, BinaryIO]:
    request = urllib.request.Request(url, headers={"Accept": ", ".join(_MEDIA_TYPES)}, method="GET")
    opener = urllib.request.build_opener(
        urllib.request.ProxyHandler({}),
        _NoRedirectHandler(),
        urllib.request.HTTPSHandler(context=ssl.create_default_context()),
    )
    try:
        response = opener.open(request, timeout=30)
    except urllib.error.HTTPError as exc:
        if 300 <= exc.code < 400:
            raise ManufacturerEvidenceError("Manufacturer download redirect requires new review.") from exc
        raise ManufacturerEvidenceError(f"Manufacturer download returned HTTP {exc.code}.") from exc
    except urllib.error.URLError as exc:
        raise ManufacturerEvidenceError("Manufacturer download could not reach the approved host.") from exc
    declared = response.headers.get("Content-Length")
    if declared is not None:
        try:
            length = int(declared)
        except ValueError as exc:
            response.close()
            raise ManufacturerEvidenceError("Manufacturer response Content-Length is invalid.") from exc
        if length < 1 or length > maximum_bytes:
            response.close()
            raise ManufacturerEvidenceError("Manufacturer response exceeds its approved byte limit.")
    return response.headers.get_content_type().lower(), response


def _resolve_addresses(host: str) -> Iterable[str]:
    try:
        return (item[4][0] for item in socket.getaddrinfo(host, 443, type=socket.SOCK_STREAM))
    except socket.gaierror as exc:
        raise ManufacturerEvidenceError("Approved manufacturer host could not be resolved.") from exc


def _public_ip(value: str) -> None:
    try:
        address = ipaddress.ip_address(value)
    except ValueError as exc:
        raise ManufacturerEvidenceError("Manufacturer host resolved to an invalid IP address.") from exc
    if not address.is_global:
        raise ManufacturerEvidenceError("Manufacturer host resolved to a non-public IP address.")


def _safe_locator(value: object):
    if not isinstance(value, str) or len(value) > 2048 or value != value.strip():
        raise ManufacturerEvidenceError("Manufacturer retrieval locator is invalid.")
    parsed = urlsplit(value)
    if (
        parsed.scheme != "https" or not parsed.hostname or parsed.username is not None
        or parsed.password is not None or parsed.port not in (None, 443) or parsed.fragment
    ):
        raise ManufacturerEvidenceError("Manufacturer retrieval requires an exact safe HTTPS locator.")
    return parsed


def _validate_content_signature(media_type: str, prefix: bytes) -> None:
    forbidden = (b"MZ", b"\x7fELF", b"#!", b"\xca\xfe\xba\xbe")
    if any(prefix.startswith(item) for item in forbidden):
        raise ManufacturerEvidenceError("Executable artifact content is forbidden.")
    if media_type == "application/pdf" and not prefix.startswith(b"%PDF-"):
        raise ManufacturerEvidenceError("Downloaded PDF signature is invalid.")
    if media_type == "application/dxf" and b"SECTION" not in prefix.upper():
        raise ManufacturerEvidenceError("Downloaded DXF signature is invalid.")
    if media_type == "application/acad" and not prefix.startswith(b"AC10"):
        raise ManufacturerEvidenceError("Downloaded DWG signature is invalid.")
    if media_type in {"model/step", "application/step"} and not prefix.lstrip().startswith(b"ISO-10303-21;"):
        raise ManufacturerEvidenceError("Downloaded STEP signature is invalid.")
    if media_type == "model/iges" and b"S      1" not in prefix[:160]:
        raise ManufacturerEvidenceError("Downloaded IGES signature is invalid.")
    if media_type == "image/jpeg" and not prefix.startswith(b"\xff\xd8\xff"):
        raise ManufacturerEvidenceError("Downloaded JPEG signature is invalid.")
    if media_type == "image/png" and not prefix.startswith(b"\x89PNG\r\n\x1a\n"):
        raise ManufacturerEvidenceError("Downloaded PNG signature is invalid.")


def _normalize_declared_media_type(
    declared_media_type: object, request: ManufacturerRetrievalRequest
) -> str:
    if not isinstance(declared_media_type, str):
        raise ManufacturerEvidenceError("Manufacturer response media type is invalid.")
    declared = declared_media_type.split(";", 1)[0].strip().lower()
    if declared != "application/octet-stream":
        return declared
    suffix = Path(urlsplit(request.locator).path).suffix.lower()
    typed_by_suffix = {
        ".dwg": "application/acad", ".dxf": "application/dxf",
        ".stp": "model/step", ".step": "model/step",
        ".igs": "model/iges", ".iges": "model/iges",
        ".jpg": "image/jpeg", ".jpeg": "image/jpeg", ".png": "image/png",
    }
    typed = typed_by_suffix.get(suffix)
    if typed is None or typed not in request.expected_media_types:
        raise ManufacturerEvidenceError(
            "Generic binary content requires an exact typed CAD locator and approval."
        )
    return typed


def _safe_filename(value: str) -> str:
    return re.sub(r"[^a-z0-9]+", "-", value.lower()).strip("-")[:96]


def _canonical_sha256(value: object) -> str:
    def convert(item: object) -> object:
        if isinstance(item, Enum): return item.value
        if isinstance(item, JsonRecord): return item.to_dict()
        if isinstance(item, tuple): return [convert(child) for child in item]
        if isinstance(item, dict): return {str(key): convert(child) for key, child in item.items()}
        return item
    encoded = json.dumps(convert(value), ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest()


def _verify_hash(record: JsonRecord, field: str, name: str) -> None:
    value = getattr(record, field)
    _sha256(value, f"{name} hash")
    payload = record.to_dict()
    del payload[field]
    if value != _canonical_sha256(payload):
        raise ManufacturerEvidenceError(f"{name} SHA-256 does not match content.")


def _identifier(value: object, name: str) -> None:
    if not isinstance(value, str) or _IDENTIFIER.fullmatch(value) is None:
        raise ManufacturerEvidenceError(f"{name} is invalid.")


def _text(value: object, name: str, maximum: int) -> None:
    if not isinstance(value, str) or not value.strip() or value != value.strip() or len(value.encode("utf-8")) > maximum:
        raise ManufacturerEvidenceError(f"{name} is invalid or exceeds {maximum} UTF-8 bytes.")


def _calendar_date(value: object, name: str) -> None:
    if not isinstance(value, str) or re.fullmatch(r"\d{4}-\d{2}-\d{2}", value) is None:
        raise ManufacturerEvidenceError(f"{name} must use YYYY-MM-DD.")
    try: date.fromisoformat(value)
    except ValueError as exc: raise ManufacturerEvidenceError(f"{name} is not a valid calendar date.") from exc


def _sha256(value: object, name: str) -> None:
    if not isinstance(value, str) or _SHA256.fullmatch(value) is None:
        raise ManufacturerEvidenceError(f"{name} must be lowercase SHA-256.")


def _mapping(value: object, name: str) -> dict[str, Any]:
    if not isinstance(value, dict) or any(not isinstance(key, str) for key in value):
        raise ManufacturerEvidenceError(f"{name} must be a JSON object with string keys.")
    return value


def _exact_keys(value: Mapping[str, object], record_type: type, name: str) -> None:
    if set(value) != set(record_type.__dataclass_fields__):
        raise ManufacturerEvidenceError(f"{name} fields do not match the supported schema.")
