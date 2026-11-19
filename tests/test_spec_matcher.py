"""Tests for project specification parser and compliance matcher."""

from __future__ import annotations

import pytest

from core.spec_matcher import (
    ComplianceStatus,
    ProjectSpecification,
    ProjectSpecMatcher,
)


def test_parse_from_text() -> None:
    sample_spec = """
    SECTION 16120 - ELECTRICAL SYSTEMS
    1. Maximum permitted voltage drop for lighting circuits shall not exceed 2.5%.
    2. Power sub-feeder circuits voltage drop shall be limited to 3.5%.
    3. Ambient temperature for cable sizing: 45°C.
    4. All cables in public areas must be Low Smoke Zero Halogen (LSZH).
    """

    matcher = ProjectSpecMatcher.parse_from_text(sample_spec, project_name="Hospital Expansion")
    spec = matcher.spec

    assert spec.project_name == "Hospital Expansion"
    assert spec.max_voltage_drop_lighting_percent == 2.5
    assert spec.max_voltage_drop_power_percent == 3.5
    assert spec.ambient_temperature_celsius == 45.0
    assert spec.low_smoke_zero_halogen_required is True


def test_verify_voltage_drop_compliance() -> None:
    spec = ProjectSpecification(
        project_name="Test Spec",
        max_voltage_drop_lighting_percent=2.5,
        max_voltage_drop_power_percent=4.0,
    )
    matcher = ProjectSpecMatcher(spec)

    # Lighting compliant: 2.1% <= 2.5%
    finding_ok = matcher.verify_voltage_drop("Lighting", 2.1, circuit_tag="L1")
    assert finding_ok.status == ComplianceStatus.COMPLIANT

    # Lighting non-compliant: 2.8% > 2.5%
    finding_bad = matcher.verify_voltage_drop("Lighting", 2.8, circuit_tag="L2")
    assert finding_bad.status == ComplianceStatus.NON_COMPLIANT
    assert "يلزم زيادة مقاس الكابل" in finding_bad.explanation_ar


def test_verify_manufacturer_avl() -> None:
    spec = ProjectSpecification(
        project_name="Test Spec",
        approved_panel_manufacturers=("SCHNEIDER ELECTRIC", "ABB", "SIEMENS"),
    )
    matcher = ProjectSpecMatcher(spec)

    # Schneider is approved
    finding_ok = matcher.verify_manufacturer("Panels", "Schneider Electric")
    assert finding_ok.status == ComplianceStatus.COMPLIANT

    # Unknown brand requires review
    finding_unknown = matcher.verify_manufacturer("Panels", "Unlisted Brand X")
    assert finding_unknown.status == ComplianceStatus.REQUIRES_REVIEW
    assert "material submittal" in finding_unknown.explanation_en.lower()
