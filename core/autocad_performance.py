"""Deterministic local-core performance trials for the AutoCAD beta gate."""

from __future__ import annotations

import hashlib
import json
import os
import platform
import statistics
import tempfile
import time
from datetime import UTC, datetime
from pathlib import Path
from typing import Any, Callable

import psutil

from core.electrical_drafting import (
    ProposedCircuit,
    ProposedDevice,
    ProposedRoute,
    ProposedTerminal,
    SourceModelBinding,
    create_drafting_preview,
    create_drafting_proposal,
)
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
)
from core.low_voltage_calculations import (
    CALCULATION_VERSION,
    LowVoltageCalculationInput,
    PhaseSystem,
    calculate_low_voltage,
)
from integrations.autocad_context import ContextLimits, parse_autocad_context


BENCHMARK_VERSION = "autocad.core_performance.v1"
_EVIDENCE_SHA256 = hashlib.sha256(b"autocad performance fixture").hexdigest()
_CONTEXT_ID = "e" * 32
_DOCUMENT_ID = "autocad:123456781234123412341234567890ab"
_TARGETS = {
    ("context_import", "small"): 2.0,
    ("context_import", "maximum"): 8.0,
    ("preview", "small"): 1.0,
    ("preview", "maximum"): 5.0,
    ("calculation", "small"): 0.5,
    ("calculation", "maximum"): 3.0,
}


def run_autocad_core_benchmark(*, runs: int = 5) -> dict[str, Any]:
    """Measure deterministic local work; never start AutoCAD, a provider, or a write path."""
    if not isinstance(runs, int) or isinstance(runs, bool) or not 3 <= runs <= 20:
        raise ValueError("Performance runs must be from 3 through 20.")
    context_small = _context_fixture(50, 8)
    context_maximum = _context_fixture(512, 128)
    preview_small = _preview_fixture(2, 4, 2, 2)
    preview_maximum = _preview_fixture(256, 1024, 256, 256)
    calculation_small = _calculation_fixture(1)
    calculation_maximum = _calculation_fixture(256)

    trials = (
        _measure("context_import", "small", runs, lambda: _parse(context_small), 50,
                 {"entities": 50, "layers": 8}),
        _measure("context_import", "maximum", runs, lambda: _parse(context_maximum), 512,
                 {"entities": 512, "layers": 128}),
        _measure("preview", "small", runs, lambda: _preview(preview_small), 10,
                 {"devices": 2, "terminals": 4, "circuits": 2, "routes": 2}),
        _measure("preview", "maximum", runs, lambda: _preview(preview_maximum), 1792,
                 {"devices": 256, "terminals": 1024, "circuits": 256, "routes": 256}),
        _measure(
            "calculation", "small", runs,
            lambda: _calculate(calculation_small), 1, {"load_objects": 1, "routes": 1},
        ),
        _measure(
            "calculation", "maximum", runs,
            lambda: _calculate(calculation_maximum), 256,
            {"load_objects": 256, "routes": 256},
        ),
    )
    payload: dict[str, Any] = {
        "schema_version": 1,
        "benchmark_version": BENCHMARK_VERSION,
        "measured_at": datetime.now(UTC).isoformat().replace("+00:00", "Z"),
        "scope": "synthetic_local_core_only",
        "platform": platform.platform(),
        "cpu_model": os.environ.get("PROCESSOR_IDENTIFIER", "unknown"),
        "python_version": platform.python_version(),
        "logical_cpu_count": os.cpu_count(),
        "total_memory_bytes": psutil.virtual_memory().total,
        "storage_type": "not_collected_for_local_core_trial",
        "package_sha256": None,
        "drawing_sha256": None,
        "runs_per_trial": runs,
        "trials": list(trials),
        "all_local_targets_satisfied": all(item["target_satisfied"] for item in trials),
        "autocad_context_capture_measured": False,
        "autocad_preview_display_measured": False,
        "scratch_execution_recovery_measured": False,
        "release_performance_gate_complete": False,
        "provider_egress_performed": False,
        "drawing_mutation_performed": False,
    }
    payload["result_sha256"] = _sha256(payload)
    return payload


def write_autocad_core_benchmark(path: Path, *, runs: int = 5) -> dict[str, Any]:
    """Create one no-overwrite UTF-8 benchmark report atomically."""
    target = path.expanduser().resolve(strict=False)
    if target.exists():
        raise FileExistsError(f"Performance report already exists: {target}")
    target.parent.mkdir(parents=True, exist_ok=True)
    report = run_autocad_core_benchmark(runs=runs)
    content = json.dumps(report, ensure_ascii=False, indent=2, sort_keys=True) + "\n"
    descriptor, temporary_name = tempfile.mkstemp(prefix=f".{target.name}.", dir=target.parent)
    temporary = Path(temporary_name)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as stream:
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
        try:
            os.link(temporary, target)
        except FileExistsError as exc:
            raise FileExistsError(f"Performance report appeared concurrently: {target}") from exc
        temporary.unlink()
    finally:
        if temporary.exists():
            temporary.unlink()
    return report


def _measure(
    operation: str,
    fixture: str,
    runs: int,
    function: Callable[[], str],
    object_count: int,
    fixture_details: dict[str, int],
) -> dict[str, Any]:
    warm_identity = function()
    durations: list[float] = []
    identities: list[str] = []
    for _ in range(runs):
        started = time.perf_counter_ns()
        identities.append(function())
        durations.append((time.perf_counter_ns() - started) / 1_000_000_000)
    if any(identity != warm_identity for identity in identities):
        raise RuntimeError(f"{operation} {fixture} produced non-deterministic identities.")
    target = _TARGETS[(operation, fixture)]
    median = statistics.median(durations)
    maximum = max(durations)
    return {
        "operation": operation,
        "fixture": fixture,
        "object_count": object_count,
        "fixture_details": fixture_details,
        "target_seconds": target,
        "durations_seconds": [round(item, 9) for item in durations],
        "median_seconds": round(median, 9),
        "maximum_seconds": round(maximum, 9),
        "target_satisfied": maximum <= target,
        "output_identity": warm_identity,
    }


def _context_fixture(entity_count: int, layer_count: int) -> dict[str, Any]:
    limits = ContextLimits()
    layers = [
        {
            "name": f"PERF-{index:03d}", "color_index": (index % 255) + 1,
            "linetype": "Continuous", "is_off": False, "is_frozen": False,
            "is_locked": False,
        }
        for index in range(layer_count)
    ]
    entities = [
        {
            "object_id": f"{index + 1:X}", "entity_type": "LINE", "original_type": "Line",
            "layer": layers[index % layer_count]["name"], "layout": "Model", "visible": True,
            "points": [[float(index), 0.0, 0.0], [float(index + 1), 1.0, 0.0]],
            "radius": None, "rotation_degrees": None, "scale": None, "block_name": None,
            "text": None, "attributes": [],
        }
        for index in range(entity_count)
    ]
    return {
        "schema_version": 1, "context_id": _CONTEXT_ID,
        "captured_at": "2026-08-13T08:00:00Z", "source_mode": "selection",
        "privacy": {"classification": "project_confidential", "provider_egress_allowed": False},
        "document": {
            "document_id": "12345678-1234-1234-1234-1234567890ab",
            "identity_source": "fingerprint_guid", "name": "performance.dwg",
            "drawing_units": 4, "layout": "Model", "database_modified": 0,
        },
        "limits": limits.to_dict(), "truncated": False, "truncation_reasons": [],
        "layers": layers, "entities": entities,
    }


def _parse(raw: dict[str, Any]) -> str:
    context = parse_autocad_context(raw, expected_limits=ContextLimits())
    return hashlib.sha256(
        f"{context.context_id}:{len(context.layers)}:{len(context.entities)}".encode("ascii")
    ).hexdigest()


def _preview_fixture(
    devices_count: int, terminals_count: int, circuits_count: int, routes_count: int
):
    binding = SourceModelBinding(
        "project:performance", _DOCUMENT_ID, _DOCUMENT_ID, _CONTEXT_ID,
        _EVIDENCE_SHA256, "a" * 64, "b" * 64, UnitSystem.MILLIMETER,
    )
    devices = tuple(
        ProposedDevice(
            f"device:perf-{index}", "socket_single", "c" * 64, "AIENG_SOCKET",
            "layer:perf", (float(index), 0.0, 0.0), 0.0, (1.0, 1.0, 1.0), (),
        )
        for index in range(devices_count)
    )
    terminals = tuple(
        ProposedTerminal(
            f"terminal:perf-{index}", f"device:perf-{index % devices_count}", "L",
            (float(index), 0.0, 0.0),
        )
        for index in range(terminals_count)
    )
    circuits = tuple(
        ProposedCircuit(f"circuit:perf-{index}", f"Performance {index}", (), ())
        for index in range(circuits_count)
    )
    routes = tuple(
        ProposedRoute(
            f"route:perf-{index}", f"circuit:perf-{index % circuits_count}", "layer:perf",
            f"terminal:perf-{index % terminals_count}",
            f"terminal:perf-{(index + 1) % terminals_count}",
            ((float(index), 0.0, 0.0), (float(index + 1), 0.0, 0.0)),
        )
        for index in range(routes_count)
    )
    return create_drafting_proposal(
        proposal_id="a" * 32, binding=binding, layers=(), devices=devices,
        terminals=terminals, circuits=circuits, routes=routes, input_issues=(),
        created_at="2026-08-13T08:00:00Z", expires_at="2026-08-13T10:00:00Z",
    )


def _preview(proposal) -> str:
    return create_drafting_preview(
        preview_id="b" * 32, proposal=proposal,
        created_at="2026-08-13T08:30:00Z", expires_at="2026-08-13T09:30:00Z",
    ).preview_sha256


def _calculation_fixture(count: int):
    layer_device = DrawingLayer("layer:device", "E-DEVICE", 2, "CONTINUOUS", False, False, False)
    layer_route = DrawingLayer("layer:route", "E-WIRE", 3, "CONTINUOUS", False, False, False)
    devices = tuple(
        Device(
            f"device:load-{index}", "socket_single", "AIENG_SOCKET", "layer:device",
            (float(index), 0.0, 0.0), 0.0, (1.0, 1.0, 1.0), (), (),
            SourceReference("autocad", _DOCUMENT_ID, f"{index + 1:X}", _CONTEXT_ID),
        )
        for index in range(count)
    )
    routes = tuple(
        Route(
            f"route:path-{index}", "layer:route",
            ((float(index), 0.0, 0.0), (float(index) + 1.0, 0.0, 0.0)),
            SourceReference("autocad", _DOCUMENT_ID, f"{4096 + index:X}", _CONTEXT_ID),
        )
        for index in range(count)
    )
    model = ElectricalProjectModel(
        schema_version=1,
        project_id="project:performance",
        document_id=_DOCUMENT_ID,
        context_id=_CONTEXT_ID,
        unit_system=UnitSystem.METER,
        provenance=Provenance(
            SourceKind.AUTOCAD_SELECTION, "2026-08-13T08:00:00Z", Confidence.OBSERVED,
            PrivacyClassification.PROJECT_CONFIDENTIAL, False, _EVIDENCE_SHA256,
        ),
        drawings=(Drawing(_DOCUMENT_ID, "performance.dwg", UnitSystem.METER, "Model",
                          ("layer:device", "layer:route"), 0),),
        layers=(layer_device, layer_route),
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
    calculation_input = LowVoltageCalculationInput(
        "calculation:performance", CALCULATION_VERSION, PhaseSystem.SINGLE_PHASE,
        1000.0, 1.0, 230.0, 0.9, 0.95, 7.41, 0.08,
        tuple(item.device_id for item in devices), tuple(item.route_id for item in routes),
        "engineer:performance", ("Synthetic deterministic performance fixture.",),
    )
    return model, calculation_input


def _calculate(fixture) -> str:
    model, calculation_input = fixture
    return calculate_low_voltage(
        model, calculation_input, context_evidence_sha256=_EVIDENCE_SHA256
    ).result_sha256


def _sha256(value: object) -> str:
    return hashlib.sha256(
        json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
    ).hexdigest()
