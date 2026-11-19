"""Tests for deterministic, source-linked Milestone 19 quantity takeoff."""

from __future__ import annotations

import hashlib
import csv
import io
from dataclasses import replace

import pytest

from agent.exceptions import QuantityTakeoffError
from core.electrical_model import (
    Confidence,
    Device,
    Drawing,
    DrawingLayer,
    ElectricalProjectModel,
    PrivacyClassification,
    Provenance,
    Route,
    SourceKind,
    SourceReference,
    UnitSystem,
    UnknownObject,
)
from core.quantity_takeoff import (
    RouteLengthOverride,
    TakeoffPolicy,
    create_takeoff_report,
)
from core.quantity_export import create_reviewed_csv_export


EVIDENCE_SHA256 = hashlib.sha256(b"quantity context").hexdigest()
CONTEXT_ID = "a" * 32
DOCUMENT_ID = "autocad:123456781234123412341234567890ab"


def _source(object_id: str) -> SourceReference:
    return SourceReference("autocad", DOCUMENT_ID, object_id, CONTEXT_ID)


def _model(unit: UnitSystem = UnitSystem.MILLIMETER) -> ElectricalProjectModel:
    devices = (
        Device(
            "device:socket-2", "socket_single", "AIENG_SOCKET", "layer:device",
            (1.0, 0.0, 0.0), 0.0, (1.0, 1.0, 1.0), (), (), _source("A2"),
        ),
        Device(
            "device:socket-1", "socket_single", "AIENG_SOCKET", "layer:device",
            (0.0, 0.0, 0.0), 0.0, (1.0, 1.0, 1.0), (), (), _source("A1"),
        ),
        Device(
            "device:light-1", "light_point", "AIENG_LIGHT", "layer:device",
            (2.0, 0.0, 0.0), 0.0, (1.0, 1.0, 1.0), (), (), _source("A3"),
        ),
    )
    routes = (
        Route("route:main", "layer:wire", ((0.0, 0.0, 0.0), (3000.0, 4000.0, 0.0)), _source("B1")),
        Route("route:branch", "layer:wire", ((0.0, 0.0, 0.0), (0.0, 0.0, 2000.0)), _source("B2")),
    )
    return ElectricalProjectModel(
        schema_version=1,
        project_id="project:test",
        document_id=DOCUMENT_ID,
        context_id=CONTEXT_ID,
        unit_system=unit,
        provenance=Provenance(
            SourceKind.AUTOCAD_SELECTION,
            "2026-08-10T08:00:00Z",
            Confidence.OBSERVED,
            PrivacyClassification.PROJECT_CONFIDENTIAL,
            False,
            EVIDENCE_SHA256,
        ),
        drawings=(Drawing(DOCUMENT_ID, "sample.dwg", unit, "Model", ("layer:device", "layer:wire"), 0),),
        layers=(
            DrawingLayer("layer:device", "E-DEVICE", 2, "CONTINUOUS", False, False, False),
            DrawingLayer("layer:wire", "E-WIRE", 3, "CONTINUOUS", False, False, False),
        ),
        devices=devices,
        panels=(),
        circuits=(),
        protection_devices=(),
        cables=(),
        conductors=(),
        loads=(),
        routes=routes,
        unknown_objects=(),
        warnings=(),
    )


def test_takeoff_counts_exact_symbols_and_separates_length_components() -> None:
    policy = TakeoffPolicy(
        vertical_allowance_per_route_meters=0.5,
        termination_allowance_per_end_meters=0.25,
        waste_percent=10.0,
        route_overrides=(
            RouteLengthOverride("route:branch", 4.25, "Reviewed site riser.", "engineer:gamal"),
        ),
    )

    report = create_takeoff_report(
        _model(), context_evidence_sha256=EVIDENCE_SHA256, policy=policy
    )

    assert [(item.symbol_id, item.quantity) for item in report.device_quantities] == [
        ("light_point", 1),
        ("socket_single", 2),
    ]
    sockets = report.device_quantities[1]
    assert sockets.device_ids == ("device:socket-1", "device:socket-2")
    assert tuple(source.object_id for source in sockets.sources) == ("A1", "A2")
    routes = {item.route_id: item for item in report.route_quantities}
    assert routes["route:main"].geometric_length_meters == 5.0
    assert routes["route:main"].vertical_allowance_meters == 0.5
    assert routes["route:main"].termination_allowance_meters == 0.5
    assert routes["route:main"].waste_meters == 0.6
    assert routes["route:main"].final_total_meters == 6.6
    assert routes["route:branch"].calculated_total_meters == 3.3
    assert routes["route:branch"].reviewed_override_meters == 4.25
    assert routes["route:branch"].final_total_meters == 4.25
    assert report.is_complete is True
    assert report.drawing_mutation_performed is False
    assert len(report.report_sha256) == 64
    assert report.report_sha256 == create_takeoff_report(
        _model(), context_evidence_sha256=EVIDENCE_SHA256, policy=policy
    ).report_sha256
    with pytest.raises(QuantityTakeoffError, match="does not match"):
        replace(report, report_sha256="f" * 64)


@pytest.mark.parametrize(
    ("unit", "coordinate", "expected"),
    [
        (UnitSystem.INCH, 10.0, 0.254),
        (UnitSystem.FOOT, 10.0, 3.048),
        (UnitSystem.MILLIMETER, 10.0, 0.01),
        (UnitSystem.CENTIMETER, 10.0, 0.1),
        (UnitSystem.METER, 10.0, 10.0),
    ],
)
def test_takeoff_converts_supported_drawing_units(
    unit: UnitSystem, coordinate: float, expected: float
) -> None:
    model = _model(unit)
    route = replace(model.routes[0], points=((0.0, 0.0, 0.0), (coordinate, 0.0, 0.0)))
    model = replace(model, routes=(route,))

    report = create_takeoff_report(model, context_evidence_sha256=EVIDENCE_SHA256)

    assert report.route_quantities[0].geometric_length_meters == expected
    assert report.route_quantities[0].source_unit is unit


def test_takeoff_marks_unknown_objects_incomplete_without_guessing() -> None:
    model = _model()
    model = replace(
        model,
        unknown_objects=(
            UnknownObject("unknown:c1", "Circle", "layer:wire", "Not classified.", _source("C1")),
        ),
    )

    report = create_takeoff_report(model, context_evidence_sha256=EVIDENCE_SHA256)

    assert report.is_complete is False
    assert report.unknown_object_ids == ("unknown:c1",)
    assert "remain unclassified" in report.warnings[-1]


@pytest.mark.parametrize("unit", [UnitSystem.UNITLESS, UnitSystem.UNKNOWN])
def test_takeoff_rejects_units_that_cannot_be_converted(unit: UnitSystem) -> None:
    with pytest.raises(QuantityTakeoffError, match="explicit supported"):
        create_takeoff_report(_model(unit), context_evidence_sha256=EVIDENCE_SHA256)


def test_takeoff_rejects_zero_length_segments_and_unknown_overrides() -> None:
    model = _model()
    invalid = replace(
        model.routes[0],
        points=((0.0, 0.0, 0.0), (0.0, 0.0, 0.0), (1.0, 0.0, 0.0)),
    )
    with pytest.raises(QuantityTakeoffError, match="zero-length"):
        create_takeoff_report(
            replace(model, routes=(invalid,)), context_evidence_sha256=EVIDENCE_SHA256
        )
    with pytest.raises(QuantityTakeoffError, match="unknown route"):
        create_takeoff_report(
            model,
            context_evidence_sha256=EVIDENCE_SHA256,
            policy=TakeoffPolicy(
                route_overrides=(
                    RouteLengthOverride("route:missing", 1.0, "Reviewed.", "engineer:gamal"),
                )
            ),
        )


def test_takeoff_rejects_wrong_evidence_and_invalid_policy() -> None:
    with pytest.raises(QuantityTakeoffError, match="provenance"):
        create_takeoff_report(_model(), context_evidence_sha256="f" * 64)
    with pytest.raises(QuantityTakeoffError, match="waste percent"):
        TakeoffPolicy(waste_percent=101.0)
    duplicate = RouteLengthOverride("route:main", 1.0, "Reviewed.", "engineer:gamal")
    with pytest.raises(QuantityTakeoffError, match="only one"):
        TakeoffPolicy(route_overrides=(duplicate, duplicate))


def test_reviewed_csv_export_is_deterministic_bounded_and_formula_safe() -> None:
    policy = TakeoffPolicy(
        route_overrides=(
            RouteLengthOverride("route:branch", 4.25, "=reviewed formula", "@engineer"),
        )
    )
    report = create_takeoff_report(
        _model(), context_evidence_sha256=EVIDENCE_SHA256, policy=policy
    )

    exported = create_reviewed_csv_export(
        report, reviewed_report_sha256=report.report_sha256
    )
    repeated = create_reviewed_csv_export(
        report, reviewed_report_sha256=report.report_sha256
    )

    assert exported.content.startswith("\ufeffrecord_type,")
    assert exported.content_sha256 == repeated.content_sha256
    assert exported.content == repeated.content
    assert exported.row_count == 4
    assert exported.drawing_mutation_performed is False
    assert exported.file_write_performed is False
    rows = list(csv.DictReader(io.StringIO(exported.content.removeprefix("\ufeff"))))
    branch = next(row for row in rows if row["item_id"] == "route:branch")
    assert branch["override_reason"] == "'=reviewed formula"
    assert branch["override_reviewed_by"] == "'@engineer"
    assert branch["report_sha256"] == report.report_sha256
    assert branch["source_object_ids"] == "B2"

    with pytest.raises(QuantityTakeoffError, match="does not match"):
        create_reviewed_csv_export(report, reviewed_report_sha256="f" * 64)


def test_reviewed_csv_export_keeps_unclassified_objects_visible() -> None:
    model = replace(
        _model(),
        unknown_objects=(
            UnknownObject("unknown:c1", "Circle", "layer:wire", "Not classified.", _source("C1")),
        ),
    )
    report = create_takeoff_report(model, context_evidence_sha256=EVIDENCE_SHA256)

    exported = create_reviewed_csv_export(
        report, reviewed_report_sha256=report.report_sha256
    )

    rows = list(csv.DictReader(io.StringIO(exported.content.removeprefix("\ufeff"))))
    unknown = next(row for row in rows if row["record_type"] == "unknown")
    assert unknown["item_id"] == "unknown:c1"
    assert unknown["report_complete"] == "false"
