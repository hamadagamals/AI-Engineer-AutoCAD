"""Tests for pinned reviewed code profiles and non-activating edition discovery."""

from __future__ import annotations

import hashlib
from dataclasses import replace

import pytest

from agent.exceptions import CodeProfileError
from core.code_profile import (
    CodeAuthority,
    CodeSourceIdentity,
    EditionRelationship,
    EvidenceAccessBasis,
    ProjectCodeRequirement,
    assess_discovered_edition,
    create_project_code_profile,
)


def _source(*, source_id: str = "source:iec-2020", edition: str = "2020", content: bytes = b"iec") -> CodeSourceIdentity:
    return CodeSourceIdentity(
        source_id=source_id,
        authority_name="International Electrotechnical Commission",
        standard_name="IEC reviewed test standard",
        edition=edition,
        publication_date="2020-01-01" if edition == "2020" else "2026-01-01",
        effective_from="2020-06-01" if edition == "2020" else "2026-06-01",
        effective_to=None,
        access_basis=EvidenceAccessBasis.OFFICIAL_PUBLIC,
        locator=f"https://example.test/{edition}",
        retrieved_on="2026-08-12",
        content_sha256=hashlib.sha256(content).hexdigest(),
    )


def _profile():
    source = _source()
    return create_project_code_profile(
        profile_id="code-profile:test",
        project_id="project:test",
        jurisdiction="Egypt",
        project_type="Low-voltage building electrical installation",
        sources=(source,),
        authorities=(CodeAuthority("authority:iec", 1, source.source_id, ("low_voltage",)),),
        requirements=(
            ProjectCodeRequirement("requirement:review", "Engineer review is required.", None, None),
        ),
        reviewed_by="engineer:gamal",
        reviewed_on="2026-08-12",
        assumptions=("Test fixture metadata only; no protected clause text is stored.",),
    )


def test_profile_is_ordered_self_hashed_and_makes_no_compliance_claim() -> None:
    profile = _profile()
    assert len(profile.profile_sha256) == 64
    assert profile.authorities[0].precedence == 1
    assert profile.sources[0].edition == "2020"
    assert profile.sources[0].full_text_stored is False
    assert profile.provider_retrieval_performed is False
    assert profile.compliance_evaluation_performed is False
    with pytest.raises(CodeProfileError, match="SHA-256"):
        replace(profile, profile_sha256="f" * 64)


def test_newer_edition_requires_impact_review_and_never_activates() -> None:
    profile = _profile()
    original_hash = profile.profile_sha256
    discovered = _source(source_id="source:iec-2026", edition="2026", content=b"new")
    assessment = assess_discovered_edition(
        profile,
        assessment_id="edition-impact:iec-2026",
        authority_id="authority:iec",
        discovered_source=discovered,
        relationship=EditionRelationship.NEWER,
        assessed_by="engineer:gamal",
        assessed_on="2026-08-12",
        notes=("Impact review is required before any profile revision.",),
    )
    assert assessment.impact_review_required is True
    assert assessment.activation_performed is False
    assert assessment.active_source_id == "source:iec-2020"
    assert profile.sources[0].edition == "2020"
    assert profile.profile_sha256 == original_hash


def test_profile_rejects_broken_authority_order_unknown_sources_and_full_text() -> None:
    source = _source()
    with pytest.raises(CodeProfileError, match="contiguous precedence"):
        create_project_code_profile(
            profile_id="code-profile:test", project_id="project:test",
            jurisdiction="Egypt", project_type="Building",
            sources=(source,), authorities=(CodeAuthority("authority:iec", 2, source.source_id, ("lv",)),),
            requirements=(), reviewed_by="engineer:gamal", reviewed_on="2026-08-12",
            assumptions=("Reviewed.",),
        )
    with pytest.raises(CodeProfileError, match="unknown source"):
        create_project_code_profile(
            profile_id="code-profile:test", project_id="project:test",
            jurisdiction="Egypt", project_type="Building", sources=(source,),
            authorities=(CodeAuthority("authority:iec", 1, "source:missing", ("lv",)),),
            requirements=(), reviewed_by="engineer:gamal", reviewed_on="2026-08-12",
            assumptions=("Reviewed.",),
        )
    with pytest.raises(CodeProfileError, match="full protected text"):
        replace(source, full_text_stored=True)


def test_official_sources_require_https_and_same_discovery_must_match_content() -> None:
    with pytest.raises(CodeProfileError, match="HTTPS"):
        replace(_source(), locator="document:local")
    profile = _profile()
    changed = _source(source_id="source:iec-copy", content=b"changed")
    with pytest.raises(CodeProfileError, match="same-edition"):
        assess_discovered_edition(
            profile,
            assessment_id="edition-impact:same",
            authority_id="authority:iec",
            discovered_source=changed,
            relationship=EditionRelationship.SAME,
            assessed_by="engineer:gamal",
            assessed_on="2026-08-12",
            notes=("Reviewed.",),
        )
    with pytest.raises(CodeProfileError, match="calendar date"):
        replace(_source(), publication_date="2026-02-31")
