"""Tests for complete chunked AutoCAD drawing-index validation."""

from __future__ import annotations

import hashlib
import json
from dataclasses import asdict, replace
from pathlib import Path

import pytest

from agent.exceptions import AutoCADContextError
import integrations.autocad_inventory_refresh as refresh_module
from integrations.autocad_context import ContextLimits
from integrations.autocad_drawing_index import load_drawing_index
from integrations.autocad_drawing_inventory import (
    DrawingInventory,
    DrawingInventoryEntity,
    SelectiveDrawingInventory,
    build_drawing_inventory,
    compare_drawing_inventories,
    compose_drawing_inventory_delta,
    load_drawing_inventory,
    write_drawing_inventory,
)
from integrations.autocad_drawing_delta import (
    DrawingDeltaLoadResult,
    DrawingDeltaManifest,
)
from integrations.autocad_drawing_index import DrawingIndexChunk
from integrations.autocad_project_conventions import (
    BlockConvention,
    RouteConvention,
    create_project_convention_profile,
)
from integrations.autocad_project_topology import create_reviewed_route_membership
from integrations.autocad_operator_channel import (
    ActiveAutoCADDocument,
    AutoCADOperatorResult,
)


DOCUMENT_ID = "12345678-1234-1234-1234-1234567890ab"
SOURCE_SHA = "d" * 64
CATALOG = Path(__file__).resolve().parents[1] / "plugins" / "electrical_symbols" / "catalog.json"


def _context(context_id: str, handle: str) -> dict[str, object]:
    return {
        "schema_version": 1,
        "context_id": context_id,
        "captured_at": "2026-08-23T10:00:00.000000Z",
        "source_mode": "drawing",
        "privacy": {
            "classification": "project_confidential",
            "provider_egress_allowed": False,
        },
        "document": {
            "document_id": DOCUMENT_ID,
            "identity_source": "fingerprint_guid",
            "name": "lighting.dwg",
            "drawing_units": 4,
            "layout": "Model",
            "database_modified": 0,
        },
        "limits": ContextLimits().to_dict(),
        "truncated": False,
        "truncation_reasons": [],
        "layers": [
            {
                "name": "E-Lite-Circ",
                "color_index": 3,
                "linetype": "Continuous",
                "is_off": False,
                "is_frozen": False,
                "is_locked": False,
            }
        ],
        "entities": [
            {
                "object_id": handle,
                "entity_type": "LINE",
                "original_type": "Autodesk.AutoCAD.DatabaseServices.Line",
                "layer": "E-Lite-Circ",
                "layout": "Model",
                "visible": True,
                "points": [[0.0, 0.0, 0.0], [1.0, 0.0, 0.0]],
                "radius": None,
                "rotation_degrees": None,
                "scale": None,
                "block_name": None,
                "text": None,
                "attributes": [],
            }
        ],
    }


def _write_index(
    workspace: Path, handles: tuple[str, ...], *, source_sha: str = SOURCE_SHA
) -> tuple[str, str]:
    context_root = workspace / ".ai-engineer" / "autocad" / "context" / "inbox"
    index_root = workspace / ".ai-engineer" / "autocad" / "drawing-index" / "inbox"
    context_root.mkdir(parents=True, exist_ok=True)
    index_root.mkdir(parents=True, exist_ok=True)
    chunks: list[dict[str, object]] = []
    for ordinal, handle in enumerate(handles):
        context_id = f"{ordinal + 1:032x}"
        content = json.dumps(
            _context(context_id, handle), separators=(",", ":")
        ).encode()
        (context_root / f"{context_id}.json").write_bytes(content)
        chunks.append(
            {
                "ordinal": ordinal,
                "context_id": context_id,
                "context_sha256": hashlib.sha256(content).hexdigest(),
                "entity_count": 1,
            }
        )
    index_id = "a" * 32
    manifest = {
        "schema_version": 1,
        "index_version": "autocad.drawing_index.v1",
        "index_id": index_id,
        "captured_at": "2026-08-23T10:00:01.000000Z",
        "document": {
            "document_id": DOCUMENT_ID,
            "identity_source": "fingerprint_guid",
            "name": "lighting.dwg",
            "source_drawing_sha256": source_sha,
            "drawing_units": 4,
            "database_modified_before": 0,
            "database_modified_after": 0,
        },
        "scope": {
            "model_space": True,
            "paper_space": True,
            "nested_block_definitions": False,
            "external_references": False,
        },
        "limits": {
            "maximum_entities_per_chunk": 64,
            "maximum_total_entities": 200_000,
            "maximum_chunks": 4_096,
            "maximum_duration_seconds": 120,
        },
        "complete": True,
        "chunk_count": len(chunks),
        "entity_count": len(chunks),
        "chunks": chunks,
        "drawing_mutation_performed": False,
    }
    encoded = json.dumps(manifest, separators=(",", ":")).encode()
    (index_root / f"{index_id}.json").write_bytes(encoded)
    return index_id, hashlib.sha256(encoded).hexdigest()


def test_complete_index_loads_all_non_overlapping_chunks(tmp_path: Path) -> None:
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    index_id, digest = _write_index(workspace, ("A1", "B2"))

    result = load_drawing_index(
        workspace,
        index_id=index_id,
        evidence_sha256=digest,
        expected_document_id=DOCUMENT_ID,
        expected_source_drawing_sha256=SOURCE_SHA,
    )

    assert result.manifest.entity_count == 2
    assert result.manifest.chunk_count == 2
    assert result.unique_entity_count == 2
    assert len(result.context_paths) == 2
    assert result.read_only is True
    assert result.provider_egress_performed is False


def test_index_rejects_overlapping_entity_handles(tmp_path: Path) -> None:
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    index_id, digest = _write_index(workspace, ("A1", "A1"))

    with pytest.raises(AutoCADContextError, match="overlapping"):
        load_drawing_index(workspace, index_id=index_id, evidence_sha256=digest)


def test_index_rejects_manifest_or_chunk_tampering(tmp_path: Path) -> None:
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    index_id, digest = _write_index(workspace, ("A1",))
    index_path = (
        workspace
        / ".ai-engineer"
        / "autocad"
        / "drawing-index"
        / "inbox"
        / f"{index_id}.json"
    )
    index_path.write_bytes(index_path.read_bytes() + b" ")
    with pytest.raises(AutoCADContextError, match="SHA-256"):
        load_drawing_index(workspace, index_id=index_id, evidence_sha256=digest)

    index_id, digest = _write_index(workspace, ("A1",))
    context_path = next(
        (workspace / ".ai-engineer" / "autocad" / "context" / "inbox").glob("*.json")
    )
    context_path.write_bytes(context_path.read_bytes() + b" ")
    with pytest.raises(AutoCADContextError, match="SHA-256"):
        load_drawing_index(workspace, index_id=index_id, evidence_sha256=digest)


def test_inventory_aggregates_all_chunks_with_reviewed_semantics(
    tmp_path: Path,
) -> None:
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    index_id, digest = _write_index(workspace, ("A1", "B2"))
    index = load_drawing_index(
        workspace, index_id=index_id, evidence_sha256=digest
    )
    profile = create_project_convention_profile(
        profile_id="profile:test-index",
        document_id=DOCUMENT_ID,
        context_id="f" * 32,
        context_evidence_sha256="e" * 64,
        source_drawing_sha256=SOURCE_SHA,
        reviewed_by="engineer:test",
        reviewed_on="2026-08-23",
        block_mappings=(
            BlockConvention("DUMMY", "0", "dummy_symbol", "Required reviewed fixture."),
        ),
        route_mappings=(
            RouteConvention(
                "E-Lite-Circ", "lighting_circuit_route", "explicit_endpoints_only"
            ),
        ),
        tag_mappings=(),
        assumptions=("Exact source-bound test convention.",),
    )

    inventory = build_drawing_inventory(
        index,
        symbol_catalog_path=CATALOG,
        reviewed_conventions=profile,
    )
    artifact = write_drawing_inventory(workspace, inventory)

    assert inventory.entity_count == 2
    assert inventory.raw_route_primitive_count == 2
    assert inventory.classified_route_count == 0
    assert inventory.route_candidate_count == 2
    assert inventory.unclassified_count == 0
    assert inventory.semantic_counts == (("reviewed_route_candidate", 2),)
    assert artifact.is_file()
    assert write_drawing_inventory(workspace, inventory) == artifact
    loaded = load_drawing_inventory(
        artifact,
        expected_inventory_sha256=inventory.inventory_sha256,
        expected_document_id=DOCUMENT_ID,
        expected_source_drawing_sha256=SOURCE_SHA,
    )
    assert loaded == inventory

    artifact.write_text(
        artifact.read_text(encoding="utf-8").replace(
            f'"source_drawing_sha256":"{SOURCE_SHA}"',
            f'"source_drawing_sha256":"{"c" * 64}"',
            1,
        ),
        encoding="utf-8",
    )
    with pytest.raises(AutoCADContextError, match="SHA-256"):
        load_drawing_inventory(artifact)


def test_inventory_only_promotes_exact_membership_handles_to_reviewed_routes(
    tmp_path: Path,
) -> None:
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    index_id, digest = _write_index(workspace, ("A1", "B2"))
    index = load_drawing_index(workspace, index_id=index_id, evidence_sha256=digest)
    profile = create_project_convention_profile(
        profile_id="profile:test-membership",
        document_id=DOCUMENT_ID,
        context_id="f" * 32,
        context_evidence_sha256="e" * 64,
        source_drawing_sha256=SOURCE_SHA,
        reviewed_by="engineer:test",
        reviewed_on="2026-08-23",
        block_mappings=(
            BlockConvention("DUMMY", "0", "dummy_symbol", "Required reviewed fixture."),
        ),
        route_mappings=(
            RouteConvention(
                "E-Lite-Circ", "lighting_circuit_route", "explicit_endpoints_only"
            ),
        ),
        tag_mappings=(),
        assumptions=("Exact source-bound test convention.",),
    )
    membership = create_reviewed_route_membership(
        membership_id="topology:test-membership",
        document_id=DOCUMENT_ID,
        context_id="1" * 32,
        context_evidence_sha256="2" * 64,
        source_drawing_sha256=SOURCE_SHA,
        convention_profile_sha256=profile.profile_sha256,
        panel_name="DB-1",
        circuit_name="1",
        route_object_ids=("A1",),
        annotation_route_object_ids=("B2",),
        drawing_unit="millimeter",
        observed_centerline_meters=0.001,
        annotation_centerline_meters=0.001,
        connectivity_confirmed=False,
        installed_cable_length_confirmed=False,
        reviewed_by="engineer:test",
        reviewed_on="2026-08-23",
        assumptions=("Exact test handles only.",),
    )

    inventory = build_drawing_inventory(
        index,
        symbol_catalog_path=CATALOG,
        reviewed_conventions=profile,
        reviewed_route_membership=membership,
    )

    assert inventory.classified_route_count == 1
    assert inventory.route_candidate_count == 0
    assert inventory.reviewed_annotation_count == 1
    assert inventory.semantic_counts == (
        ("reviewed_annotation", 1),
        ("reviewed_route", 1),
    )
    assert inventory.route_membership_sha256 == membership.membership_sha256


def test_incremental_diff_separates_geometry_and_semantic_changes(
    tmp_path: Path,
) -> None:
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    index_id, digest = _write_index(workspace, ("A1", "B2"))
    index = load_drawing_index(workspace, index_id=index_id, evidence_sha256=digest)
    before = build_drawing_inventory(index, symbol_catalog_path=CATALOG)

    changed = _reseal_inventory(
        before,
        entities=(
            _entity_copy(before.entities[0], entity_sha256="1" * 64),
            _entity_copy(before.entities[1], object_id="C3", entity_sha256="2" * 64),
        ),
        source_drawing_sha256="3" * 64,
    )
    delta = compare_drawing_inventories(before, changed)
    assert delta.added_object_ids == ("C3",)
    assert delta.removed_object_ids == ("B2",)
    assert delta.changed_object_ids == ("A1",)
    assert delta.reclassified_object_ids == ()
    assert delta.unchanged_count == 0
    assert delta.drawing_mutation_performed is False

    reclassified = _reseal_inventory(
        before,
        entities=(
            _entity_copy(
                before.entities[0],
                semantic_kind="reviewed_route_candidate",
                semantic_id="lighting_circuit_route",
            ),
            before.entities[1],
        ),
    )
    semantic_delta = compare_drawing_inventories(before, reclassified)
    assert semantic_delta.changed_object_ids == ()
    assert semantic_delta.reclassified_object_ids == ("A1",)
    assert semantic_delta.unchanged_count == 1


def test_selective_delta_composes_v2_and_invalidates_reviewed_semantics(
    tmp_path: Path,
) -> None:
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    index_id, index_sha = _write_index(workspace, ("A1", "B2", "D4"))
    index = load_drawing_index(workspace, index_id=index_id, evidence_sha256=index_sha)
    baseline = build_drawing_inventory(index, symbol_catalog_path=CATALOG)
    baseline = _reseal_inventory(
        baseline,
        entities=tuple(
            _entity_copy(
                item,
                semantic_kind="reviewed_route_candidate",
                semantic_id="lighting_circuit_route",
            )
            for item in baseline.entities
        ),
        convention_profile_sha256="8" * 64,
    )
    delta = _selective_delta_fixture(
        workspace, baseline, modified="A1", added="C3", removed="B2"
    )

    composed = compose_drawing_inventory_delta(
        baseline, delta, symbol_catalog_path=CATALOG
    )
    artifact = write_drawing_inventory(workspace, composed)
    loaded = load_drawing_inventory(
        artifact,
        expected_inventory_sha256=composed.inventory_sha256,
        expected_document_id=DOCUMENT_ID,
        expected_source_drawing_sha256="c" * 64,
    )

    assert isinstance(loaded, SelectiveDrawingInventory)
    assert loaded.schema_version == 2
    assert loaded.inventory_version == "autocad.drawing_inventory.v2"
    assert loaded.baseline_inventory_sha256 == baseline.inventory_sha256
    assert loaded.delta_id == delta.manifest.delta_id
    assert loaded.delta_sha256 == delta.manifest_sha256
    assert loaded.semantic_review_required is True
    assert loaded.convention_profile_sha256 is None
    assert loaded.route_membership_sha256 is None
    assert tuple(item.object_id for item in loaded.entities) == ("A1", "C3", "D4")
    assert loaded.entities[0].semantic_kind == "unclassified"
    assert loaded.entities[1].semantic_kind == "catalog_route_candidate"
    assert loaded.entities[2].semantic_kind == "unclassified"
    assert loaded.classified_route_count == 0
    assert loaded.route_candidate_count == 1
    difference = compare_drawing_inventories(baseline, loaded)
    assert difference.added_object_ids == ("C3",)
    assert difference.removed_object_ids == ("B2",)
    assert difference.changed_object_ids == ("A1",)
    assert difference.reclassified_object_ids == ("D4",)


def test_selective_delta_composition_rejects_lineage_mismatch_and_context_tamper(
    tmp_path: Path,
) -> None:
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    index_id, index_sha = _write_index(workspace, ("A1", "B2"))
    index = load_drawing_index(workspace, index_id=index_id, evidence_sha256=index_sha)
    baseline = build_drawing_inventory(index, symbol_catalog_path=CATALOG)
    delta = _selective_delta_fixture(
        workspace, baseline, modified="A1", added="C3", removed="B2"
    )
    mismatched = DrawingDeltaLoadResult(
        replace(delta.manifest, baseline_source_drawing_sha256="7" * 64),
        delta.manifest_sha256,
        delta.context_paths,
        delta.captured_object_ids,
    )
    with pytest.raises(AutoCADContextError, match="complete baseline"):
        compose_drawing_inventory_delta(
            baseline, mismatched, symbol_catalog_path=CATALOG
        )

    unsafe = replace(delta, drawing_mutation_performed=True)
    with pytest.raises(AutoCADContextError, match="complete validated delta"):
        compose_drawing_inventory_delta(
            baseline, unsafe, symbol_catalog_path=CATALOG
        )

    delta.context_paths[0].write_bytes(delta.context_paths[0].read_bytes() + b" ")
    with pytest.raises(AutoCADContextError, match="changed after delta validation"):
        compose_drawing_inventory_delta(
            baseline, delta, symbol_catalog_path=CATALOG
        )


def test_automatic_refresh_orchestrates_index_inventory_and_baseline_diff(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    index_id, index_sha = _write_index(workspace, ("A1", "B2"))
    profile = create_project_convention_profile(
        profile_id="profile:auto-refresh",
        document_id=DOCUMENT_ID,
        context_id="f" * 32,
        context_evidence_sha256="e" * 64,
        source_drawing_sha256=SOURCE_SHA,
        reviewed_by="engineer:test",
        reviewed_on="2026-08-23",
        block_mappings=(
            BlockConvention("DUMMY", "0", "dummy_symbol", "Required fixture."),
        ),
        route_mappings=(
            RouteConvention(
                "E-Lite-Circ", "lighting_circuit_route", "explicit_endpoints_only"
            ),
        ),
        tag_mappings=(),
        assumptions=("Exact refresh fixture.",),
    )
    membership = create_reviewed_route_membership(
        membership_id="topology:auto-refresh",
        document_id=DOCUMENT_ID,
        context_id="1" * 32,
        context_evidence_sha256="2" * 64,
        source_drawing_sha256=SOURCE_SHA,
        convention_profile_sha256=profile.profile_sha256,
        panel_name="DB-1",
        circuit_name="1",
        route_object_ids=("A1",),
        annotation_route_object_ids=("B2",),
        drawing_unit="millimeter",
        observed_centerline_meters=0.001,
        annotation_centerline_meters=0.001,
        connectivity_confirmed=False,
        installed_cable_length_confirmed=False,
        reviewed_by="engineer:test",
        reviewed_on="2026-08-23",
        assumptions=("Exact refresh handles.",),
    )
    profile_path = workspace / "profile.json"
    membership_path = workspace / "membership.json"
    profile_path.write_text(json.dumps(asdict(profile)), encoding="utf-8")
    membership_path.write_text(json.dumps(asdict(membership)), encoding="utf-8")
    status_path = workspace / "status.json"
    status_path.write_text("{}", encoding="utf-8")
    active = ActiveAutoCADDocument(
        "2026-08-23T10:00:00.000000Z",
        DOCUMENT_ID,
        "lighting.dwg",
        SOURCE_SHA,
        0,
        True,
        status_path,
        "4" * 64,
    )
    result_path = workspace / "operator-result.json"
    result_path.write_text("{}", encoding="utf-8")
    result_sha = hashlib.sha256(result_path.read_bytes()).hexdigest()
    operator_result = AutoCADOperatorResult(
        1,
        "autocad.operator_command_result.v1",
        "3" * 32,
        "4" * 32,
        "5" * 64,
        "2026-08-23T10:00:01.000000Z",
        DOCUMENT_ID,
        "lighting.dwg",
        SOURCE_SHA,
        "capture_drawing_index",
        "completed",
        0,
        0,
        index_id,
        index_sha,
        2,
        1.0,
        False,
        None,
    )
    monkeypatch.setattr(refresh_module, "discover_active_documents", lambda root: (active,))
    monkeypatch.setattr(refresh_module, "enqueue_operator_command", lambda *a, **k: object())
    monkeypatch.setattr(
        refresh_module,
        "wait_for_operator_result",
        lambda *a, **k: (operator_result, result_path, result_sha),
    )

    initial = refresh_module.refresh_active_drawing_inventory(
        workspace,
        symbol_catalog_path=CATALOG,
        convention_profile_path=profile_path,
        route_membership_path=membership_path,
        drawing_name="lighting.dwg",
    )
    compared = refresh_module.refresh_active_drawing_inventory(
        workspace,
        symbol_catalog_path=CATALOG,
        convention_profile_path=profile_path,
        route_membership_path=membership_path,
        baseline_inventory_path=initial.inventory_path,
        drawing_name="lighting.dwg",
    )

    assert initial.difference is None
    assert compared.difference is not None
    assert compared.difference.unchanged_count == 2
    assert compared.difference.added_object_ids == ()
    assert compared.report_path.is_file()
    report = json.loads(compared.report_path.read_text(encoding="utf-8"))
    assert report["unchanged_count"] == 2
    assert report["drawing_mutation_performed"] is False
    assert report["reviewed_semantics_applied"] is True
    assert report["semantic_review_required"] is False

    changed_source = "c" * 64
    _, changed_index_sha = _write_index(
        workspace, ("A1", "B2"), source_sha=changed_source
    )
    changed_active = ActiveAutoCADDocument(
        active.observed_at,
        active.document_id,
        active.drawing_name,
        changed_source,
        0,
        True,
        active.status_path,
        active.status_sha256,
    )
    changed_operator_result = AutoCADOperatorResult(
        operator_result.schema_version,
        operator_result.result_version,
        "6" * 32,
        "7" * 32,
        "8" * 64,
        operator_result.completed_at,
        operator_result.document_id,
        operator_result.drawing_name,
        changed_source,
        operator_result.requested_action,
        operator_result.status,
        0,
        0,
        index_id,
        changed_index_sha,
        2,
        1.0,
        False,
        None,
    )
    monkeypatch.setattr(
        refresh_module, "discover_active_documents", lambda root: (changed_active,)
    )
    monkeypatch.setattr(
        refresh_module,
        "wait_for_operator_result",
        lambda *a, **k: (changed_operator_result, result_path, result_sha),
    )
    structural = refresh_module.refresh_active_drawing_inventory(
        workspace,
        symbol_catalog_path=CATALOG,
        convention_profile_path=profile_path,
        route_membership_path=membership_path,
        baseline_inventory_path=initial.inventory_path,
        drawing_name="lighting.dwg",
    )
    structural_report = json.loads(
        structural.report_path.read_text(encoding="utf-8")
    )
    assert structural.reviewed_semantics_applied is False
    assert structural.semantic_review_required is True
    assert structural_report["semantic_review_required"] is True
    assert structural.difference is not None
    assert structural.difference.reclassified_object_ids == ("A1", "B2")


def test_automatic_refresh_fails_closed_without_one_exact_active_document() -> None:
    with pytest.raises(AutoCADContextError, match="no matching"):
        refresh_module._select_active_document((), drawing_name="lighting.dwg")


def _selective_delta_fixture(
    workspace: Path,
    baseline: DrawingInventory,
    *,
    modified: str,
    added: str,
    removed: str,
) -> DrawingDeltaLoadResult:
    context_id = "f" * 32
    context = _context(context_id, modified)
    modified_entity = dict(context["entities"][0])  # type: ignore[index]
    modified_entity["points"] = [[0.0, 0.0, 0.0], [2.0, 0.0, 0.0]]
    added_entity = dict(context["entities"][0])  # type: ignore[index]
    added_entity["object_id"] = added
    added_entity["layer"] = "E-WIRE"
    context["entities"] = [modified_entity, added_entity]
    context["layers"].append({  # type: ignore[union-attr]
        "name": "E-WIRE",
        "color_index": 3,
        "linetype": "Continuous",
        "is_off": False,
        "is_frozen": False,
        "is_locked": False,
    })
    context_root = workspace / ".ai-engineer/autocad/context/inbox"
    context_root.mkdir(parents=True, exist_ok=True)
    context_path = context_root / f"{context_id}.json"
    raw = json.dumps(context, separators=(",", ":")).encode()
    context_path.write_bytes(raw)
    context_sha = hashlib.sha256(raw).hexdigest()
    manifest = DrawingDeltaManifest(
        delta_id="b" * 32,
        captured_at="2026-08-23T12:00:00.000000Z",
        document_id=baseline.document_id,
        identity_source="fingerprint_guid",
        drawing_name="lighting.dwg",
        drawing_units=4,
        baseline_source_drawing_sha256=baseline.source_drawing_sha256,
        current_source_drawing_sha256="c" * 64,
        database_modified_before=0,
        database_modified_after=0,
        baseline_index_id=baseline.index_id,
        baseline_index_sha256=baseline.index_sha256,
        baseline_entity_count=baseline.entity_count,
        maximum_candidates=4096,
        added_object_ids=(added,),
        modified_object_ids=(modified,),
        removed_object_ids=(removed,),
        candidate_count=3,
        context_entity_count=2,
        chunks=(DrawingIndexChunk(0, context_id, context_sha, 2),),
    )
    return DrawingDeltaLoadResult(
        manifest=manifest,
        manifest_sha256="9" * 64,
        context_paths=(context_path,),
        captured_object_ids=tuple(sorted((modified, added))),
    )


def _entity_copy(
    entity: DrawingInventoryEntity, **changes: object
) -> DrawingInventoryEntity:
    values = asdict(entity)
    values.update(changes)
    return DrawingInventoryEntity(**values)


def _reseal_inventory(
    inventory: DrawingInventory, **changes: object
) -> DrawingInventory:
    values = asdict(inventory)
    values.update(changes)
    values["entity_count"] = len(values["entities"])
    values["supported_observation_count"] = values["entity_count"]
    values["unsupported_observation_count"] = 0
    semantic = [item.semantic_kind for item in values["entities"]]
    values["classified_device_count"] = sum(
        item in {"catalog_device", "reviewed_device"} for item in semantic
    )
    values["classified_route_count"] = sum(
        item == "reviewed_route" for item in semantic
    )
    values["route_candidate_count"] = sum(
        item in {"catalog_route_candidate", "reviewed_route_candidate"}
        for item in semantic
    )
    values["reviewed_annotation_count"] = semantic.count("reviewed_annotation")
    values["unclassified_count"] = semantic.count("unclassified")
    values["semantic_counts"] = tuple(sorted({item: semantic.count(item) for item in semantic}.items()))
    values.pop("inventory_sha256")
    encoded = json.dumps(
        values,
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
        default=asdict,
    ).encode()
    return DrawingInventory(
        **values, inventory_sha256=hashlib.sha256(encoded).hexdigest()
    )
