"""Tests for exact-evidence-bound panel schedule and SLD identity contracts."""

from __future__ import annotations

import hashlib
from dataclasses import replace

import pytest

from agent.exceptions import PanelSldContractError
from core.electrical_model import (
    Circuit,
    Confidence,
    Device,
    Drawing,
    ElectricalProjectModel,
    Load,
    Panel,
    PrivacyClassification,
    ProtectionDevice,
    Provenance,
    SourceKind,
    SourceReference,
    UnitSystem,
)
from core.panel_sld import (
    LoadAssignmentContract,
    OutgoingWayContract,
    PanelContract,
    PanelPhase,
    ProtectiveDeviceContract,
    SldEdgeContract,
    SldNodeContract,
    SldNodeKind,
    WayKind,
    create_panel_sld_contract,
    validate_panel_sld_contract,
)


CONTEXT_ID = "a" * 32
EVIDENCE_SHA256 = hashlib.sha256(b"panel context evidence").hexdigest()


def _source(object_id: str) -> SourceReference:
    return SourceReference(
        "autocad",
        "autocad:123456781234123412341234567890ab",
        object_id,
        CONTEXT_ID,
    )


def _model() -> ElectricalProjectModel:
    device_source = _source("10")
    return ElectricalProjectModel(
        schema_version=1,
        project_id="project:test",
        document_id="autocad:123456781234123412341234567890ab",
        context_id=CONTEXT_ID,
        unit_system=UnitSystem.MILLIMETER,
        provenance=Provenance(
            SourceKind.AUTOCAD_SELECTION,
            "2026-08-11T12:00:00Z",
            Confidence.REVIEWED,
            PrivacyClassification.PROJECT_CONFIDENTIAL,
            False,
            EVIDENCE_SHA256,
        ),
        drawings=(
            Drawing(
                "autocad:123456781234123412341234567890ab",
                "panel-test.dwg",
                UnitSystem.MILLIMETER,
                "Model",
                (),
                0,
            ),
        ),
        layers=(),
        devices=(
            Device(
                "device:load-1",
                "symbol:load",
                "AIENG_LOAD",
                "layer:power",
                (0.0, 0.0, 0.0),
                0.0,
                (1.0, 1.0, 1.0),
                (),
                (),
                device_source,
            ),
        ),
        panels=(Panel("panel:db1", "DB-1", _source("20")),),
        circuits=(
            Circuit("circuit:1", "panel:db1", "Lighting", ("device:load-1",)),
            Circuit("circuit:2", "panel:db1", "Spare", ()),
        ),
        protection_devices=(
            ProtectionDevice("protection:1", "circuit:1", "mcb", _source("30")),
            ProtectionDevice("protection:2", "circuit:2", "mcb", _source("31")),
        ),
        cables=(),
        conductors=(),
        loads=(Load("load:1", "device:load-1", "Lighting load", _source("40")),),
        routes=(),
        unknown_objects=(),
        warnings=(),
    )


def _records() -> dict[str, object]:
    return {
        "panel": PanelContract("panel:db1", "DB-1", _source("20")),
        "outgoing_ways": (
            OutgoingWayContract(
                "way:1",
                "panel:db1",
                "circuit:1",
                1,
                WayKind.LOAD,
                PanelPhase.L1,
                1,
                "protection:1",
            ),
            OutgoingWayContract(
                "way:2",
                "panel:db1",
                "circuit:2",
                2,
                WayKind.SPARE,
                PanelPhase.L2,
                1,
                "protection:2",
            ),
        ),
        "protective_devices": (
            ProtectiveDeviceContract(
                "protection:1",
                "circuit:1",
                "mcb",
                "10 A / 6 kA",
                10.0,
                6000.0,
                _source("30"),
            ),
            ProtectiveDeviceContract(
                "protection:2",
                "circuit:2",
                "mcb",
                "16 A / 6 kA spare",
                16.0,
                6000.0,
                _source("31"),
            ),
        ),
        "load_assignments": (
            LoadAssignmentContract(
                "assignment:1", "circuit:1", "load:1", PanelPhase.L1, _source("40")
            ),
        ),
        "sld_nodes": (
            SldNodeContract("node:panel", SldNodeKind.PANEL, "panel:db1", None),
            SldNodeContract(
                "node:protection-1",
                SldNodeKind.PROTECTIVE_DEVICE,
                "protection:1",
                "circuit:1",
            ),
            SldNodeContract("node:load-1", SldNodeKind.LOAD, "load:1", "circuit:1"),
            SldNodeContract(
                "node:protection-2",
                SldNodeKind.PROTECTIVE_DEVICE,
                "protection:2",
                "circuit:2",
            ),
        ),
        "sld_edges": (
            SldEdgeContract("edge:1", "node:panel", "node:protection-1", "circuit:1"),
            SldEdgeContract("edge:2", "node:protection-1", "node:load-1", "circuit:1"),
            SldEdgeContract("edge:3", "node:panel", "node:protection-2", "circuit:2"),
        ),
    }


def _contract(**changes: object):
    values = _records()
    values.update(changes)
    return create_panel_sld_contract(
        _model(),
        values["panel"],  # type: ignore[arg-type]
        values["outgoing_ways"],  # type: ignore[arg-type]
        values["protective_devices"],  # type: ignore[arg-type]
        values["load_assignments"],  # type: ignore[arg-type]
        values["sld_nodes"],  # type: ignore[arg-type]
        values["sld_edges"],  # type: ignore[arg-type]
        context_evidence_sha256=EVIDENCE_SHA256,
        reviewed_by="engineer:gamal",
        assumptions=("Circuit and phase assignments were reviewed.",),
    )


def test_contract_preserves_exact_identity_and_is_read_only_self_hashed() -> None:
    contract = _contract()

    assert [item.circuit_id for item in contract.outgoing_ways] == [
        "circuit:1",
        "circuit:2",
    ]
    assert contract.schedule_generation_performed is False
    assert contract.drawing_mutation_performed is False
    assert len(contract.contract_sha256) == 64
    assert contract.to_dict()["sld_edges"][1]["circuit_id"] == "circuit:1"
    validate_panel_sld_contract(_model(), contract)
    with pytest.raises(PanelSldContractError, match="does not match"):
        replace(contract, contract_sha256="f" * 64)


def test_contract_rejects_missing_duplicate_or_foreign_circuit_ways() -> None:
    records = _records()
    ways = records["outgoing_ways"]
    assert isinstance(ways, tuple)
    with pytest.raises(PanelSldContractError, match="cover every selected-panel circuit"):
        _contract(outgoing_ways=ways[:1])
    with pytest.raises(PanelSldContractError, match="circuit IDs must be unique"):
        _contract(outgoing_ways=(ways[0], replace(ways[1], circuit_id="circuit:1")))
    with pytest.raises(PanelSldContractError, match="conflicting panel"):
        _contract(outgoing_ways=(replace(ways[0], panel_id="panel:other"), ways[1]))


def test_contract_rejects_protection_and_load_identity_conflicts() -> None:
    records = _records()
    protection = records["protective_devices"]
    assignments = records["load_assignments"]
    ways = records["outgoing_ways"]
    assert isinstance(protection, tuple)
    assert isinstance(assignments, tuple)
    assert isinstance(ways, tuple)
    with pytest.raises(PanelSldContractError, match="Protective-device circuit identity"):
        _contract(
            protective_devices=(replace(protection[0], circuit_id="circuit:2"), protection[1])
        )
    with pytest.raises(PanelSldContractError, match="source load, circuit, or way phase"):
        _contract(load_assignments=(replace(assignments[0], phase=PanelPhase.L2),))
    with pytest.raises(PanelSldContractError, match="spare ways must not contain loads"):
        _contract(outgoing_ways=(replace(ways[0], kind=WayKind.SPARE), ways[1]))


def test_contract_rejects_missing_or_conflicting_sld_topology() -> None:
    records = _records()
    edges = records["sld_edges"]
    nodes = records["sld_nodes"]
    assert isinstance(edges, tuple)
    assert isinstance(nodes, tuple)
    with pytest.raises(PanelSldContractError, match="SLD edges conflict"):
        _contract(sld_edges=edges[:-1])
    with pytest.raises(PanelSldContractError, match="SLD nodes must represent"):
        _contract(sld_nodes=nodes[:-1])
    with pytest.raises(PanelSldContractError, match="SLD edges conflict"):
        _contract(
            sld_edges=(edges[0], replace(edges[1], circuit_id="circuit:2"), edges[2])
        )


def test_contract_rejects_changed_context_and_invalid_phase_or_flags() -> None:
    contract = _contract()
    changed_model = replace(_model(), warnings=("changed",))
    with pytest.raises(PanelSldContractError, match="exact model context"):
        validate_panel_sld_contract(changed_model, contract)
    with pytest.raises(PanelSldContractError, match="context evidence"):
        create_panel_sld_contract(
            _model(),
            *_records().values(),  # type: ignore[arg-type]
            context_evidence_sha256="f" * 64,
            reviewed_by="engineer:gamal",
            assumptions=("Reviewed.",),
        )
    with pytest.raises(PanelSldContractError, match="three-phase way"):
        OutgoingWayContract(
            "way:bad",
            "panel:db1",
            "circuit:1",
            1,
            WayKind.LOAD,
            PanelPhase.THREE_PHASE,
            1,
            "protection:1",
        )
    with pytest.raises(PanelSldContractError, match="cannot claim"):
        replace(contract, schedule_generation_performed=True)
