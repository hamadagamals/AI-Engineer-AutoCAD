"""Tests for exact-approved manufacturer retrieval into immutable quarantine."""

from __future__ import annotations

import hashlib
from dataclasses import replace
from io import BytesIO

import pytest

from agent.exceptions import ManufacturerEvidenceError
from integrations.manufacturer_retrieval import (
    _NoRedirectHandler,
    create_retrieval_request,
    retrieve_to_quarantine,
)


PDF = b"%PDF-1.7\nreviewed manufacturer fixture\n%%EOF\n"
PUBLIC_IPS = lambda host: ("93.184.216.34",)


def _request(*, media=("application/pdf",), expected_hash=None, maximum=4096):
    return create_retrieval_request(
        request_id="retrieval:breaker-datasheet",
        manufacturer_id="manufacturer:test",
        product_id="product:breaker-abc",
        artifact_id="artifact:breaker-datasheet",
        locator="https://products.example.test/files/abc.pdf?revision=1",
        approved_host="products.example.test",
        expected_media_types=media,
        maximum_bytes=maximum,
        expected_content_sha256=expected_hash,
        approved_by="engineer:gamal",
        approved_on="2026-08-12",
    )


def _transport(content=PDF, media="application/pdf"):
    def fetch(url: str, maximum: int):
        assert url == "https://products.example.test/files/abc.pdf?revision=1"
        assert maximum >= 1024
        return media, BytesIO(content)
    return fetch


def test_exact_approved_pdf_is_atomically_quarantined_and_self_hashed(tmp_path) -> None:
    request = _request(expected_hash=hashlib.sha256(PDF).hexdigest())
    root = tmp_path / "quarantine"
    record = retrieve_to_quarantine(
        request, root, resolver=PUBLIC_IPS, transport=_transport(),
        retrieved_on="2026-08-12",
    )

    target = root / record.quarantine_relative_path
    assert target.read_bytes() == PDF
    assert record.content_sha256 == hashlib.sha256(PDF).hexdigest()
    assert record.resolved_addresses == ("93.184.216.34",)
    assert record.redirect_performed is False
    assert record.proxy_used is False
    assert record.quarantine_write_performed is True
    assert record.catalog_registration_performed is False
    assert record.drawing_mutation_performed is False
    assert len(record.record_sha256) == 64


def test_non_public_resolution_and_unapproved_media_fail_before_write(tmp_path) -> None:
    with pytest.raises(ManufacturerEvidenceError, match="non-public"):
        retrieve_to_quarantine(
            _request(), tmp_path / "private",
            resolver=lambda host: ("127.0.0.1",), transport=_transport(),
            retrieved_on="2026-08-12",
        )
    with pytest.raises(ManufacturerEvidenceError, match="media type"):
        retrieve_to_quarantine(
            _request(), tmp_path / "media", resolver=PUBLIC_IPS,
            transport=_transport(media="text/html"), retrieved_on="2026-08-12",
        )
    assert not list((tmp_path / "media").iterdir())


def test_exact_typed_dwg_accepts_generic_server_type_only_after_signature_check(tmp_path) -> None:
    request = create_retrieval_request(
        request_id="retrieval:socket-cad", manufacturer_id="manufacturer:test",
        product_id="product:socket", artifact_id="artifact:socket-cad",
        locator="https://products.example.test/files/socket.dwg",
        approved_host="products.example.test", expected_media_types=("application/acad",),
        maximum_bytes=4096, expected_content_sha256=None,
        approved_by="engineer:gamal", approved_on="2026-08-12",
    )
    content = b"AC1032" + b"\x00" * 1018
    record = retrieve_to_quarantine(
        request, tmp_path / "dwg", resolver=PUBLIC_IPS,
        transport=lambda url, maximum: ("application/octet-stream", BytesIO(content)),
        retrieved_on="2026-08-12",
    )
    assert record.media_type == "application/acad"
    assert (tmp_path / "dwg" / record.quarantine_relative_path).read_bytes() == content

    bad_request = create_retrieval_request(
        request_id="retrieval:generic", manufacturer_id="manufacturer:test",
        product_id="product:socket", artifact_id="artifact:generic",
        locator="https://products.example.test/files/no-extension",
        approved_host="products.example.test", expected_media_types=("application/acad",),
        maximum_bytes=4096, expected_content_sha256=None,
        approved_by="engineer:gamal", approved_on="2026-08-12",
    )
    with pytest.raises(ManufacturerEvidenceError, match="exact typed CAD locator"):
        retrieve_to_quarantine(
            bad_request, tmp_path / "generic", resolver=PUBLIC_IPS,
            transport=lambda url, maximum: ("application/octet-stream", BytesIO(content)),
            retrieved_on="2026-08-12",
        )


def test_size_hash_and_executable_signature_fail_closed_without_artifact(tmp_path) -> None:
    cases = (
        (_request(maximum=1024), _transport(content=b"%PDF-" + b"x" * 1100), "byte limit"),
        (_request(expected_hash="f" * 64), _transport(), "SHA-256"),
        (_request(), _transport(content=b"MZ" + b"x" * 100), "Executable"),
    )
    for index, (request, transport, message) in enumerate(cases):
        root = tmp_path / str(index)
        with pytest.raises(ManufacturerEvidenceError, match=message):
            retrieve_to_quarantine(
                request, root, resolver=PUBLIC_IPS, transport=transport,
                retrieved_on="2026-08-12",
            )
        assert not list(root.iterdir())


def test_redirects_and_locator_scope_cannot_be_preapproved() -> None:
    handler = _NoRedirectHandler()
    assert handler.redirect_request(None, None, 302, "Found", {}, "https://other.test") is None
    with pytest.raises(ManufacturerEvidenceError, match="exact approved host"):
        replace(_request(), approved_host="cdn.example.test")
    with pytest.raises(ManufacturerEvidenceError, match="safe HTTPS"):
        create_retrieval_request(
            request_id="retrieval:test", manufacturer_id="manufacturer:test",
            product_id="product:test", artifact_id="artifact:test",
            locator="https://user@products.example.test/file.pdf",
            approved_host="products.example.test", expected_media_types=("application/pdf",),
            maximum_bytes=4096, expected_content_sha256=None,
            approved_by="engineer:gamal", approved_on="2026-08-12",
        )


def test_quarantine_identity_is_immutable_and_existing_file_is_not_overwritten(tmp_path) -> None:
    root = tmp_path / "quarantine"
    first = retrieve_to_quarantine(
        _request(), root, resolver=PUBLIC_IPS, transport=_transport(),
        retrieved_on="2026-08-12",
    )
    with pytest.raises(ManufacturerEvidenceError, match="cannot be overwritten"):
        retrieve_to_quarantine(
            _request(), root, resolver=PUBLIC_IPS, transport=_transport(),
            retrieved_on="2026-08-12",
        )
    assert (root / first.quarantine_relative_path).read_bytes() == PDF
