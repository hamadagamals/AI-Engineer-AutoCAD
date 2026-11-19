"""Deterministic acceptance and rejection tests for drafting validation."""

from __future__ import annotations

import hashlib
from dataclasses import replace
from pathlib import Path

import pytest

from agent.exceptions import DraftingContractError
from core.electrical_drafting import (
    ProposalInputIssue,
    ProposalIssueKind,
    ProposedCircuit,
    ProposedDevice,
    ProposedLayer,
    ProposedRoute,
    ProposedTerminal,
    create_drafting_proposal,
    create_source_model_binding,
)
from core.electrical_drafting_validation import validate_drafting_proposal
from core.electrical_model import (
    Confidence,
    Drawing,
    DrawingLayer,
    ElectricalProjectModel,
    PrivacyClassification,
    Provenance,
    SourceKind,
    UnitSystem,
)
from integrations.electrical_symbols import ElectricalSymbolCatalog


PROJECT_ROOT = Path(__file__).resolve().parents[1]
CATALOG_PATH = PROJECT_ROOT / "plugins" / "electrical_symbols" / "catalog.json"
CONTEXT_SHA256 = hashlib.sha256(b"validation context").hexdigest()


def _model(unit_system: UnitSystem = UnitSystem.MILLIMETER) -> ElectricalProjectModel:
    drawing_id = "autocad:123456781234123412341234567890ab"
    return ElectricalProjectModel(
        1,
        "project:test",
        drawing_id,
        "a" * 32,
        unit_system,
        Provenance(
            SourceKind.AUTOCAD_SELECTION,
            "2026-08-07T08:00:00Z",
            Confidence.OBSERVED,
            PrivacyClassification.PROJECT_CONFIDENTIAL,
            False,
            CONTEXT_SHA256,
        ),
        (Drawing(drawing_id, "sample.dwg", unit_system, "Model", ("layer:power",), 0),),
        (DrawingLayer("layer:power", "E-POWR-DEVC", 2, "CONTINUOUS", False, False, False),),
        (),
        (),
        (),
        (),
        (),
        (),
        (),
        (),
        (),
        (),
    )


def _catalog_sha256(path: Path = CATALOG_PATH) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _valid_parts() -> tuple[
    tuple[ProposedLayer, ...],
    tuple[ProposedDevice, ...],
    tuple[ProposedTerminal, ...],
    tuple[ProposedCircuit, ...],
    tuple[ProposedRoute, ...],
]:
    symbol = ElectricalSymbolCatalog.load(CATALOG_PATH).get("socket_single")
    layers = (ProposedLayer("layer:power", "E-POWR-DEVC", 2, "CONTINUOUS", False),)
    devices = (
        ProposedDevice(
            "device:source",
            symbol.symbol_id,
            symbol.definition_sha256,
            symbol.block_name,
            "layer:power",
            (0.0, 0.0, 0.0),
            0.0,
            (1.0, 1.0, 1.0),
            (),
        ),
        ProposedDevice(
            "device:load",
            symbol.symbol_id,
            symbol.definition_sha256,
            symbol.block_name,
            "layer:power",
            (1000.0, 0.0, 0.0),
            90.0,
            (1.0, 1.0, 1.0),
            (),
        ),
    )
    terminals = (
        ProposedTerminal("terminal:source-l", "device:source", "L", (0.0, 0.0, 0.0)),
        ProposedTerminal("terminal:load-l", "device:load", "L", (1000.0, 0.0, 0.0)),
    )
    circuits = (
        ProposedCircuit(
            "circuit:power-1",
            "Power 1",
            ("terminal:source-l", "terminal:load-l"),
            ("route:power-1",),
        ),
    )
    routes = (
        ProposedRoute(
            "route:power-1",
            "circuit:power-1",
            "layer:power",
            "terminal:source-l",
            "terminal:load-l",
            ((0.0, 0.0, 0.0), (500.0, 0.0, 0.0), (1000.0, 0.0, 0.0)),
        ),
    )
    return layers, devices, terminals, circuits, routes


def _proposal(
    *,
    model: ElectricalProjectModel | None = None,
    catalog_sha256: str | None = None,
    layers: tuple[ProposedLayer, ...] | None = None,
    devices: tuple[ProposedDevice, ...] | None = None,
    terminals: tuple[ProposedTerminal, ...] | None = None,
    circuits: tuple[ProposedCircuit, ...] | None = None,
    routes: tuple[ProposedRoute, ...] | None = None,
    issues: tuple[ProposalInputIssue, ...] = (),
):
    source_model = model or _model()
    valid_layers, valid_devices, valid_terminals, valid_circuits, valid_routes = _valid_parts()
    binding = create_source_model_binding(
        source_model,
        context_evidence_sha256=CONTEXT_SHA256,
        symbol_catalog_sha256=catalog_sha256 or _catalog_sha256(),
    )
    return create_drafting_proposal(
        proposal_id="b" * 32,
        binding=binding,
        layers=valid_layers if layers is None else layers,
        devices=valid_devices if devices is None else devices,
        terminals=valid_terminals if terminals is None else terminals,
        circuits=valid_circuits if circuits is None else circuits,
        routes=valid_routes if routes is None else routes,
        input_issues=issues,
        created_at="2026-08-07T09:00:00Z",
        expires_at="2026-08-07T10:00:00Z",
    )


def _codes(result) -> set[str]:
    return {item.code for item in result.findings}


def test_valid_proposal_is_deterministic_hash_bound_and_preview_ready() -> None:
    proposal = _proposal()

    first = validate_drafting_proposal(proposal, model=_model(), symbol_catalog_path=CATALOG_PATH)
    second = validate_drafting_proposal(proposal, model=_model(), symbol_catalog_path=CATALOG_PATH)

    assert first == second
    assert first.findings == ()
    assert first.is_valid is True
    assert first.preview_allowed is True
    assert first.drawing_mutation_performed is False
    assert first.validation_sha256 == second.validation_sha256
    assert first.to_dict()["validator_version"] == "m18.validation.v1"
    with pytest.raises(DraftingContractError, match="readiness"):
        replace(first, preview_allowed=False)


def test_exact_model_catalog_and_units_are_required(tmp_path: Path) -> None:
    proposal = _proposal(catalog_sha256="f" * 64)
    result = validate_drafting_proposal(proposal, model=_model(), symbol_catalog_path=CATALOG_PATH)
    assert "catalog.hash_mismatch" in _codes(result)

    changed_model = replace(_model(), warnings=("changed after proposal",))
    result = validate_drafting_proposal(
        _proposal(), model=changed_model, symbol_catalog_path=CATALOG_PATH
    )
    assert "binding.model_hash_mismatch" in _codes(result)

    meter_model = _model(UnitSystem.METER)
    result = validate_drafting_proposal(
        _proposal(model=meter_model), model=meter_model, symbol_catalog_path=CATALOG_PATH
    )
    assert "unit.catalog_mismatch" in _codes(result)

    missing = tmp_path / "missing-catalog.json"
    result = validate_drafting_proposal(_proposal(), model=_model(), symbol_catalog_path=missing)
    assert "catalog.unavailable" in _codes(result)
    assert result.preview_allowed is False


def test_identity_layer_and_reviewed_symbol_mismatches_fail_closed() -> None:
    layers, devices, terminals, circuits, routes = _valid_parts()
    conflicting_circuit = replace(circuits[0], circuit_id=devices[0].device_id)
    result = validate_drafting_proposal(
        _proposal(circuits=(conflicting_circuit,), routes=routes),
        model=_model(),
        symbol_catalog_path=CATALOG_PATH,
    )
    assert "identity.cross_kind_duplicate" in _codes(result)

    wrong_layer = replace(layers[0], name="E-WRONG")
    wrong_device = replace(devices[0], symbol_definition_sha256="e" * 64)
    result = validate_drafting_proposal(
        _proposal(layers=(wrong_layer,), devices=(wrong_device, devices[1])),
        model=_model(),
        symbol_catalog_path=CATALOG_PATH,
    )
    assert {
        "layer.schema_mismatch",
        "layer.catalog_schema_mismatch",
        "catalog.definition_hash_mismatch",
    } <= _codes(result)


def test_unknown_missing_endpoints_and_topology_are_explicitly_rejected() -> None:
    layers, devices, terminals, circuits, routes = _valid_parts()
    missing_terminal = replace(terminals[1], position=None)
    unknown_device_terminal = replace(terminals[0], device_id="device:unknown")
    wrong_endpoint_route = replace(
        routes[0],
        points=((10.0, 0.0, 0.0), (1000.0, 0.0, 0.0)),
    )
    issue = ProposalInputIssue(
        "issue:terminal-position",
        ProposalIssueKind.MISSING,
        "terminals.position",
        "Terminal position is not reviewed.",
        ("terminal:load-l",),
    )
    result = validate_drafting_proposal(
        _proposal(
            terminals=(unknown_device_terminal, missing_terminal),
            routes=(wrong_endpoint_route,),
            issues=(issue,),
        ),
        model=_model(),
        symbol_catalog_path=CATALOG_PATH,
    )
    assert {
        "input.missing",
        "endpoint.position_missing",
        "endpoint.start_position_mismatch",
        "topology.terminal_device_unknown",
    } <= _codes(result)

    disconnected = ProposedCircuit(
        "circuit:power-1",
        "Power 1",
        ("terminal:source-l", "terminal:load-l", "terminal:spare-l"),
        ("route:power-1",),
    )
    spare = ProposedTerminal("terminal:spare-l", "device:load", "SP", (500.0, 500.0, 0.0))
    result = validate_drafting_proposal(
        _proposal(terminals=terminals + (spare,), circuits=(disconnected,)),
        model=_model(),
        symbol_catalog_path=CATALOG_PATH,
    )
    assert "topology.not_connected_tree" in _codes(result)


def test_geometry_and_cyclic_route_graph_are_rejected() -> None:
    layers, devices, terminals, _circuits, routes = _valid_parts()
    third_terminal = ProposedTerminal(
        "terminal:third-l", "device:load", "L2", (500.0, 500.0, 0.0)
    )
    extra_routes = (
        replace(
            routes[0],
            route_id="route:power-2",
            start_terminal_id="terminal:load-l",
            end_terminal_id="terminal:third-l",
            points=((1000.0, 0.0, 0.0), (500.0, 500.0, 0.0)),
        ),
        replace(
            routes[0],
            route_id="route:power-3",
            start_terminal_id="terminal:third-l",
            end_terminal_id="terminal:source-l",
            points=((500.0, 500.0, 0.0), (0.0, 0.0, 0.0)),
        ),
    )
    circuit = ProposedCircuit(
        "circuit:power-1",
        "Power 1",
        ("terminal:source-l", "terminal:load-l", "terminal:third-l"),
        ("route:power-1", "route:power-2", "route:power-3"),
    )
    invalid_device = replace(devices[0], rotation_degrees=360.0, scale=(0.0, 1.0, 1.0))
    zero_segment = replace(
        routes[0], points=((0.0, 0.0, 0.0), (0.0, 0.0, 0.0), (1000.0, 0.0, 0.0))
    )
    result = validate_drafting_proposal(
        _proposal(
            devices=(invalid_device, devices[1]),
            terminals=terminals + (third_terminal,),
            circuits=(circuit,),
            routes=(zero_segment,) + extra_routes,
        ),
        model=_model(),
        symbol_catalog_path=CATALOG_PATH,
    )
    assert {
        "geometry.rotation_not_normalized",
        "geometry.scale_out_of_bounds",
        "geometry.zero_length_segment",
        "topology.cycle",
        "topology.not_connected_tree",
    } <= _codes(result)
