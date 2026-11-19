"""Fail-closed tests for read-only existing-component relocation proposals."""

from __future__ import annotations

import hashlib
import json
from dataclasses import replace
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import Mock, call

import pytest

from agent.core_commands import _autocad_component_move_preview_payload, _preview_component_moves
from agent.exceptions import AutoCADContextError, PathPolicyError
from core.electrical_model import PrivacyClassification
from core.file_manager import FileManager
from integrations.autocad_component_moves import ComponentMoveTarget, create_component_move_preview
from integrations.autocad_context import AutoCADContext, ContextEntity, ContextImportResult, ContextLayer, ContextLimits


def _imported() -> ContextImportResult:
    entity = ContextEntity("A1", "INSERT", "BlockReference", "DEVICES", "Model", True,
                           ((10.0, 20.0, 0.0),), None, 30.0, (1.0, 1.0, 1.0), "BREAKER", None, ())
    context = AutoCADContext(
        1, "a" * 32, "2026-09-17T10:00:00Z", "selection",
        PrivacyClassification.PROJECT_CONFIDENTIAL, False,
        "12345678-1234-1234-1234-1234567890ab", "fingerprint_guid", "sample.dwg", 4,
        "Model", 0, ContextLimits(), False, (),
        (ContextLayer("DEVICES", 7, "Continuous", False, False, False),), (entity,),
    )
    # The relocation contract deliberately does not use semantic model guesses.
    return ContextImportResult(context, None, "b" * 64, 100, True, False)  # type: ignore[arg-type]


def _preview(imported: ContextImportResult | None = None, **changes):
    arguments = {"source_sha256": "c" * 64, "source_name": "sample.dwg",
                 "moves": (ComponentMoveTarget("A1", (100.0, 200.0, 0.0)),)}
    arguments.update(changes)
    return create_component_move_preview(imported or _imported(), **arguments)


def test_preview_preserves_transform_and_binds_distinct_evidence() -> None:
    before = _imported()
    result = _preview(before)
    assert result["items"][0]["displacement_mm"] == [90.0, 180.0, 0.0]
    assert result["items"][0]["preserved_rotation_degrees"] == 30.0
    assert result["items"][0]["preserved_scale"] == [1.0, 1.0, 1.0]
    assert result["source_sha256"] != result["context_evidence_sha256"]
    assert before.context.entities[0].points == ((10.0, 20.0, 0.0),)
    assert result["execution_ready"] is False
    assert result["execution_approved"] is False
    assert result["drawing_mutation_performed"] is False
    assert len(result["hold_points"]) == 4
    unsigned = {key: value for key, value in result.items() if key != "preview_sha256"}
    assert result["preview_sha256"] == hashlib.sha256(
        json.dumps(unsigned, ensure_ascii=False, sort_keys=True, separators=(",", ":"), allow_nan=False).encode()
    ).hexdigest()
    assert _preview() == result
    assert _preview(moves=(ComponentMoveTarget("A1", (101.0, 200.0, 0.0)),))["preview_sha256"] != result["preview_sha256"]


@pytest.mark.parametrize("point", [(float("nan"), 0, 0), (float("inf"), 0, 0),
                                   (True, 0, 0), (1_000_001, 0, 0), (10**400, 0, 0), ("1", 0, 0), (1, 2)])
def test_targets_reject_invalid_coordinates(point) -> None:
    with pytest.raises(AutoCADContextError):
        ComponentMoveTarget("A1", point)


@pytest.mark.parametrize("handle", ["a1", "", "../A1", "A" * 33, None])
def test_targets_require_exact_handles(handle) -> None:
    with pytest.raises(AutoCADContextError):
        ComponentMoveTarget(handle, (1, 2, 0))


@pytest.mark.parametrize("changes", [{"drawing_units": 0}, {"layout": "Layout1"},
                                     {"truncated": True}, {"truncation_reasons": ("time",)},
                                     {"database_modified": 1}, {"document_name": "other.dwg"}])
def test_context_guards(changes) -> None:
    imported = _imported()
    with pytest.raises(AutoCADContextError):
        _preview(replace(imported, context=replace(imported.context, **changes)))


@pytest.mark.parametrize("changes", [{"entity_type": "LINE"}, {"original_type": "ProxyEntity"},
                                     {"visible": False}, {"layout": "Layout1"}, {"block_name": "*U1"},
                                     {"scale": None}, {"rotation_degrees": None}, {"points": ()}])
def test_entity_guards(changes) -> None:
    imported = _imported()
    entity = replace(imported.context.entities[0], **changes)
    with pytest.raises(AutoCADContextError):
        _preview(replace(imported, context=replace(imported.context, entities=(entity,))))


@pytest.mark.parametrize("flag", ["is_locked", "is_off", "is_frozen"])
def test_layer_guards(flag) -> None:
    imported = _imported()
    layer = replace(imported.context.layers[0], **{flag: True})
    with pytest.raises(AutoCADContextError):
        _preview(replace(imported, context=replace(imported.context, layers=(layer,))))


def test_preview_rejects_missing_duplicate_noop_and_depth_changes() -> None:
    for targets in [(), (ComponentMoveTarget("A2", (100, 200, 0)),),
                    (ComponentMoveTarget("A1", (10, 20, 0)),),
                    (ComponentMoveTarget("A1", (100, 200, 1)),),
                    (ComponentMoveTarget("A1", (100, 200, 0)),) * 2]:
        with pytest.raises(AutoCADContextError):
            _preview(moves=targets)


def _payload() -> dict:
    return {"path": "sample.dwg", "expected_source_sha256": "c" * 64,
            "context_id": "a" * 32, "evidence_sha256": "b" * 64,
            "moves": [{"object_id": "A1", "target_mm": [100, 200, 0]}]}


def test_command_validator_forbids_raw_commands_and_unknown_move_fields() -> None:
    assert _autocad_component_move_preview_payload(_payload()) == _payload()
    for key in ("command", "execute", "approved", "shell"):
        with pytest.raises(ValueError):
            _autocad_component_move_preview_payload({**_payload(), key: True})
    payload = _payload()
    payload["moves"][0]["rotation"] = 10
    with pytest.raises(ValueError):
        _autocad_component_move_preview_payload(payload)


def test_command_handler_checks_source_hash_and_sandbox_without_mutation(tmp_path: Path) -> None:
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    source = workspace / "sample.dwg"
    source.write_bytes(b"AC1032 fixture - never native-executed")
    original = source.read_bytes()
    services = SimpleNamespace(files=FileManager([workspace], tmp_path / "backup"), autocad=Mock())
    services.autocad.import_drawing_context.return_value = _imported()
    payload = {**_payload(), "expected_source_sha256": hashlib.sha256(original).hexdigest()}
    result = _preview_component_moves(services, payload)
    assert result["execution_ready"] is False
    assert source.read_bytes() == original
    assert services.autocad.mock_calls == [
        call.import_drawing_context(
            context_id="a" * 32, evidence_sha256="b" * 64, confirmed=True
        )
    ]
    with pytest.raises(AutoCADContextError, match="SHA-256"):
        _preview_component_moves(services, _payload())
    with pytest.raises(PathPolicyError):
        _preview_component_moves(services, {**payload, "path": "../outside.dwg"})


def test_source_change_during_import_invalidates_preview(tmp_path: Path) -> None:
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    source = workspace / "sample.dwg"
    source.write_bytes(b"AC1032 before fixture")
    digest = hashlib.sha256(source.read_bytes()).hexdigest()
    services = SimpleNamespace(files=FileManager([workspace], tmp_path / "backup"), autocad=Mock())

    def changed(**kwargs):
        source.write_bytes(b"AC1032 modified fixture")
        return _imported()

    services.autocad.import_drawing_context.side_effect = changed
    with pytest.raises(AutoCADContextError, match="changed during"):
        _preview_component_moves(services, {**_payload(), "expected_source_sha256": digest})
