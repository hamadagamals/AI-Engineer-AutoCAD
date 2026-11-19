"""Tests for immutable hash-bound electrical drafting and preview contracts."""

from __future__ import annotations

import hashlib
from dataclasses import replace

import pytest

from agent.exceptions import DraftingContractError
from core.electrical_drafting import (
    DraftObjectKind,
    ProposalInputIssue,
    ProposalIssueKind,
    ProposedCircuit,
    ProposedDevice,
    ProposedLayer,
    ProposedRoute,
    ProposedTerminal,
    canonical_model_sha256,
    create_drafting_preview,
    create_drafting_proposal,
    create_source_model_binding,
    drafting_proposal_from_dict,
)
from core.electrical_model import (
    Confidence,
    Drawing,
    ElectricalProjectModel,
    PrivacyClassification,
    Provenance,
    SourceKind,
    UnitSystem,
)


CONTEXT_BYTES = b'exact context evidence'
CONTEXT_SHA256 = hashlib.sha256(CONTEXT_BYTES).hexdigest()
CATALOG_SHA256 = hashlib.sha256(b'reviewed symbol catalog').hexdigest()


def _model() -> ElectricalProjectModel:
    return ElectricalProjectModel(
        schema_version=1,
        project_id='project:test',
        document_id='autocad:123456781234123412341234567890ab',
        context_id='a' * 32,
        unit_system=UnitSystem.MILLIMETER,
        provenance=Provenance(
            SourceKind.AUTOCAD_SELECTION,
            '2026-08-07T08:00:00Z',
            Confidence.OBSERVED,
            PrivacyClassification.PROJECT_CONFIDENTIAL,
            False,
            CONTEXT_SHA256,
        ),
        drawings=(
            Drawing(
                'autocad:123456781234123412341234567890ab',
                'sample.dwg',
                UnitSystem.MILLIMETER,
                'Model',
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


def _proposal():
    binding = create_source_model_binding(
        _model(),
        context_evidence_sha256=CONTEXT_SHA256,
        symbol_catalog_sha256=CATALOG_SHA256,
    )
    devices = (
        ProposedDevice(
            'device:light-1',
            'light.fixture',
            hashlib.sha256(b'light definition').hexdigest(),
            'AI_LIGHT',
            'layer:lighting',
            (100.0, 200.0, 0.0),
            0.0,
            (1.0, 1.0, 1.0),
            (('TAG', 'L1'),),
        ),
        ProposedDevice(
            'device:switch-1',
            'switch.single',
            hashlib.sha256(b'switch definition').hexdigest(),
            'AI_SWITCH',
            'layer:lighting',
            (120.0, 200.0, 0.0),
            0.0,
            (1.0, 1.0, 1.0),
            (('TAG', 'S1'),),
        ),
    )
    terminals = (
        ProposedTerminal('terminal:light-1-l', 'device:light-1', 'L', (100.0, 200.0, 0.0)),
        ProposedTerminal('terminal:switch-1-l', 'device:switch-1', 'L', None),
    )
    circuits = (
        ProposedCircuit(
            'circuit:lighting-1',
            'Lighting 1',
            ('terminal:switch-1-l', 'terminal:light-1-l'),
            ('route:lighting-1',),
        ),
    )
    routes = (
        ProposedRoute(
            'route:lighting-1',
            'circuit:lighting-1',
            'layer:lighting',
            'terminal:switch-1-l',
            'terminal:light-1-l',
            ((120.0, 200.0, 0.0), (100.0, 200.0, 0.0)),
        ),
    )
    issues = (
        ProposalInputIssue(
            'issue:switch-terminal-position',
            ProposalIssueKind.MISSING,
            'terminals.position',
            'Switch terminal position requires engineer review.',
            ('terminal:switch-1-l',),
        ),
    )
    return create_drafting_proposal(
        proposal_id='b' * 32,
        binding=binding,
        layers=(ProposedLayer('layer:lighting', 'E-LITE-WIRE', 2, 'CONTINUOUS', True),),
        devices=devices,
        terminals=terminals,
        circuits=circuits,
        routes=routes,
        input_issues=issues,
        created_at='2026-08-07T09:00:00Z',
        expires_at='2026-08-07T10:00:00Z',
    )


def test_binding_uses_exact_context_model_catalog_and_units() -> None:
    model = _model()
    binding = create_source_model_binding(
        model,
        context_evidence_sha256=CONTEXT_SHA256,
        symbol_catalog_sha256=CATALOG_SHA256,
    )

    assert binding.context_id == model.context_id
    assert binding.context_evidence_sha256 == model.provenance.evidence_sha256
    assert binding.model_sha256 == canonical_model_sha256(model)
    assert binding.symbol_catalog_sha256 == CATALOG_SHA256
    assert binding.unit_system is UnitSystem.MILLIMETER
    with pytest.raises(DraftingContractError, match='model provenance'):
        create_source_model_binding(
            model,
            context_evidence_sha256='f' * 64,
            symbol_catalog_sha256=CATALOG_SHA256,
        )


def test_heterogeneous_proposal_is_immutable_serializable_and_self_hashed() -> None:
    proposal = _proposal()
    value = proposal.to_dict()

    assert len(value['devices']) == 2
    assert len(value['terminals']) == 2
    assert value['circuits'][0]['terminal_ids'] == [
        'terminal:switch-1-l',
        'terminal:light-1-l',
    ]
    assert value['routes'][0]['points'][1] == [100.0, 200.0, 0.0]
    assert value['input_issues'][0]['kind'] == 'missing'
    assert value['binding']['unit_system'] == 'millimeter'
    assert value['drawing_mutation_performed'] is False
    with pytest.raises(AttributeError):
        proposal.devices = ()  # type: ignore[misc]
    with pytest.raises(DraftingContractError, match='SHA-256 does not match'):
        replace(proposal, devices=proposal.devices[:1])


def test_preview_covers_exact_ordered_proposal_and_cannot_approve_execution() -> None:
    proposal = _proposal()
    preview = create_drafting_preview(
        preview_id='c' * 32,
        proposal=proposal,
        created_at='2026-08-07T09:05:00Z',
        expires_at='2026-08-07T09:55:00Z',
    )

    assert tuple((item.kind, item.object_id) for item in preview.items) == (
        (DraftObjectKind.LAYER, 'layer:lighting'),
        (DraftObjectKind.DEVICE, 'device:light-1'),
        (DraftObjectKind.DEVICE, 'device:switch-1'),
        (DraftObjectKind.TERMINAL, 'terminal:light-1-l'),
        (DraftObjectKind.TERMINAL, 'terminal:switch-1-l'),
        (DraftObjectKind.CIRCUIT, 'circuit:lighting-1'),
        (DraftObjectKind.ROUTE, 'route:lighting-1'),
    )
    assert preview.binding == proposal.binding
    assert preview.drawing_mutation_performed is False
    assert preview.execution_approved is False
    with pytest.raises(DraftingContractError, match='ordered proposal objects'):
        replace(preview, items=tuple(reversed(preview.items)))
    with pytest.raises(DraftingContractError, match='cannot grant execution'):
        replace(preview, execution_approved=True)


def test_contracts_reject_mutable_collections_and_unbounded_preview_lifetime() -> None:
    proposal = _proposal()
    with pytest.raises(DraftingContractError, match='immutable typed tuple'):
        replace(proposal, terminals=[])  # type: ignore[arg-type]
    with pytest.raises(DraftingContractError, match='proposal lifetime'):
        create_drafting_preview(
            preview_id='d' * 32,
            proposal=proposal,
            created_at='2026-08-07T09:05:00Z',
            expires_at='2026-08-07T10:05:00Z',
        )


def test_untrusted_json_round_trip_is_exact_and_rejects_coercion_and_extra_fields() -> None:
    proposal = _proposal()
    raw = proposal.to_dict()

    assert drafting_proposal_from_dict(raw) == proposal
    with pytest.raises(DraftingContractError, match="unexpected"):
        drafting_proposal_from_dict({**raw, "command": "draw"})
    coerced = dict(raw)
    coerced["schema_version"] = "1"
    with pytest.raises(DraftingContractError, match="without coercion"):
        drafting_proposal_from_dict(coerced)
    changed = proposal.to_dict()
    changed["devices"][0]["block_name"] = "CHANGED"
    with pytest.raises(DraftingContractError, match="SHA-256 does not match"):
        drafting_proposal_from_dict(changed)


def test_untrusted_json_rejects_cycles_depth_and_non_json_collections() -> None:
    cyclic: dict[str, object] = {}
    cyclic["self"] = cyclic
    with pytest.raises(DraftingContractError, match="cycle"):
        drafting_proposal_from_dict(cyclic)

    nested: object = None
    for _ in range(20):
        nested = [nested]
    with pytest.raises(DraftingContractError, match="structure limits"):
        drafting_proposal_from_dict(nested)

    raw = _proposal().to_dict()
    raw["layers"] = tuple(raw["layers"])
    with pytest.raises(DraftingContractError, match="non-JSON"):
        drafting_proposal_from_dict(raw)
