"""Tests for the immutable shared electrical project model."""

from __future__ import annotations

import hashlib
from dataclasses import replace

import pytest

from agent.exceptions import ElectricalModelError
from core.electrical_model import (
    Confidence,
    Drawing,
    ElectricalProjectModel,
    PrivacyClassification,
    Provenance,
    SourceKind,
    UnitSystem,
    unit_system_from_insunits,
)


def _empty_model() -> ElectricalProjectModel:
    return ElectricalProjectModel(
        schema_version=1,
        project_id="project:test",
        document_id="autocad:123456781234123412341234567890ab",
        context_id="a" * 32,
        unit_system=UnitSystem.MILLIMETER,
        provenance=Provenance(
            SourceKind.AUTOCAD_SELECTION,
            "2026-08-06T12:00:00Z",
            Confidence.OBSERVED,
            PrivacyClassification.PROJECT_CONFIDENTIAL,
            False,
            hashlib.sha256(b"context").hexdigest(),
        ),
        drawings=(
            Drawing(
                "autocad:123456781234123412341234567890ab",
                "sample.dwg",
                UnitSystem.MILLIMETER,
                "Model",
                (),
                0,
            ),
        ),
        layers=(),
        devices=(),
        panels=(),
        circuits=(),
        protection_devices=(),
        cables=(),
        conductors=(),
        loads=(),
        routes=(),
        unknown_objects=(),
        warnings=(),
    )


def test_empty_model_is_immutable_and_json_serializable() -> None:
    model = _empty_model()

    assert model.to_dict()["unit_system"] == "millimeter"
    assert model.to_dict()["drawings"][0]["layout"] == "Model"
    assert model.to_dict()["provenance"]["provider_egress_allowed"] is False
    with pytest.raises(AttributeError):
        model.project_id = "project:changed"  # type: ignore[misc]
    with pytest.raises(ElectricalModelError, match="immutable typed tuple"):
        replace(model, layers=[])  # type: ignore[arg-type]


def test_model_rejects_invalid_identity_schema_timestamp_and_hash() -> None:
    model = _empty_model()
    values = model.to_dict()
    assert values["schema_version"] == 1

    with pytest.raises(ElectricalModelError, match="schema"):
        replace(model, schema_version=2)
    with pytest.raises(ElectricalModelError, match="timestamp"):
        Provenance(
            SourceKind.AUTOCAD_SELECTION,
            "not-a-time",
            Confidence.OBSERVED,
            PrivacyClassification.INTERNAL,
            False,
            "a" * 64,
        )
    with pytest.raises(ElectricalModelError, match="SHA-256"):
        Provenance(
            SourceKind.AUTOCAD_SELECTION,
            "2026-08-06T12:00:00Z",
            Confidence.OBSERVED,
            PrivacyClassification.INTERNAL,
            False,
            "bad",
        )


def test_auto_cad_units_are_explicit_and_unknown_units_are_not_guessed() -> None:
    assert unit_system_from_insunits(0) is UnitSystem.UNITLESS
    assert unit_system_from_insunits(4) is UnitSystem.MILLIMETER
    assert unit_system_from_insunits(6) is UnitSystem.METER
    assert unit_system_from_insunits(24) is UnitSystem.UNKNOWN
