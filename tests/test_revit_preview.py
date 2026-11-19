from __future__ import annotations

import hashlib
import json
from pathlib import Path

import pytest

from agent.exceptions import RevitIntegrationError
from integrations.revit_preview import create_placement_preview, load_placement_preview


def test_revit_placement_preview_is_hash_bound_and_non_mutating(tmp_path: Path) -> None:
    model = tmp_path / "fixture.rvt"
    model.write_bytes(b"revit-fixture")
    digest = hashlib.sha256(model.read_bytes()).hexdigest().upper()
    output = tmp_path / "preview.json"
    value = create_placement_preview(
        workspace_root=tmp_path,
        source_model=model,
        expected_source_sha256=digest,
        document_title="Fixture",
        family_name="Lighting Fixture",
        type_name="LED 20W",
        symbol_id=101,
        family_placement_type="OneLevelBased",
        host_requirement="level",
        level_name="Level 1",
        level_id=201,
        reference_element_id=301,
        elevation_from_level_mm=0,
        points_mm=[(1000, 2000, 3000), (4000, 5000, 3000)],
        rotations_degrees=[0, 90],
        output_path=output,
    )
    assert value["execution_allowed"] is False
    assert value["document_mutation_performed"] is False
    loaded = load_placement_preview(output, workspace_root=tmp_path)
    assert loaded["placements"][1]["x_mm"] == 4000
    assert loaded["placements"][1]["rotation_degrees"] == 90


def test_revit_placement_preview_rejects_tampering_and_source_mismatch(tmp_path: Path) -> None:
    model = tmp_path / "fixture.rvt"
    model.write_bytes(b"fixture")
    digest = hashlib.sha256(model.read_bytes()).hexdigest().upper()
    output = tmp_path / "preview.json"
    create_placement_preview(
        workspace_root=tmp_path, source_model=model, expected_source_sha256=digest,
        document_title="Fixture", family_name="Family", type_name="Type",
        symbol_id=101, family_placement_type="OneLevelBased", host_requirement="level",
        level_name="Level 1", level_id=201, reference_element_id=301,
        elevation_from_level_mm=0,
        points_mm=[(0, 0, 0)], output_path=output,
    )
    value = json.loads(output.read_text())
    value["placements"][0]["x_mm"] = 5
    output.write_text(json.dumps(value), encoding="utf-8")
    with pytest.raises(RevitIntegrationError, match="hash"):
        load_placement_preview(output, workspace_root=tmp_path)
    with pytest.raises(RevitIntegrationError, match="SHA-256"):
        create_placement_preview(
            workspace_root=tmp_path, source_model=model, expected_source_sha256="0" * 64,
            document_title="Fixture", family_name="Family", type_name="Type",
            symbol_id=101, family_placement_type="OneLevelBased", host_requirement="level",
            level_name="Level 1", level_id=201, reference_element_id=301,
            elevation_from_level_mm=0,
            points_mm=[(0, 0, 0)], output_path=output,
        )


def test_revit_placement_preview_rejects_mismatched_placement_metadata(tmp_path: Path) -> None:
    model = tmp_path / "fixture.rvt"
    model.write_bytes(b"fixture")
    digest = hashlib.sha256(model.read_bytes()).hexdigest().upper()
    with pytest.raises(RevitIntegrationError, match="do not match"):
        create_placement_preview(
            workspace_root=tmp_path, source_model=model, expected_source_sha256=digest,
            document_title="Fixture", family_name="Family", type_name="Type",
            symbol_id=101, family_placement_type="OneLevelBased",
            host_requirement="work_plane_or_face", level_name="Level 1",
            level_id=201, reference_element_id=301, elevation_from_level_mm=0,
            points_mm=[(0, 0, 0)], output_path=tmp_path / "preview.json",
        )


def test_work_plane_preview_binds_each_placement_to_exact_linked_ceiling_face(
    tmp_path: Path,
) -> None:
    model = tmp_path / "fixture.rvt"
    model.write_bytes(b"fixture")
    digest = hashlib.sha256(model.read_bytes()).hexdigest().upper()
    output = tmp_path / "preview.json"
    host = {
        "kind": "linked_face",
        "link_instance_id": 1362762,
        "linked_element_id": 1566775,
        "linked_document_title": "Snowdon Towers Sample Architectural",
        "linked_category_key": "ceilings",
        "face_stable_reference": "fixture:0:RVTLINK:1566775:1:SURFACE",
    }
    value = create_placement_preview(
        workspace_root=tmp_path, source_model=model, expected_source_sha256=digest,
        document_title="Fixture", family_name="Recessed Lamp - Round - LED",
        type_name='4" Trimmed Downlight', symbol_id=1453791,
        family_placement_type="WorkPlaneBased", host_requirement="work_plane_or_face",
        level_name="", level_id=-1, reference_element_id=1622255,
        elevation_from_level_mm=2743.2, points_mm=[(13944.6, 11112.5, 2743.2)],
        host_faces=[host], output_path=output,
    )
    assert value["placements"][0]["host_face"] == host
    assert load_placement_preview(output, workspace_root=tmp_path)["level_id"] == -1


def test_work_plane_preview_rejects_missing_or_non_ceiling_host(tmp_path: Path) -> None:
    model = tmp_path / "fixture.rvt"
    model.write_bytes(b"fixture")
    digest = hashlib.sha256(model.read_bytes()).hexdigest().upper()
    arguments = {
        "workspace_root": tmp_path, "source_model": model,
        "expected_source_sha256": digest, "document_title": "Fixture",
        "family_name": "Family", "type_name": "Type", "symbol_id": 1,
        "family_placement_type": "WorkPlaneBased", "host_requirement": "work_plane_or_face",
        "level_name": "", "level_id": -1, "reference_element_id": 2,
        "elevation_from_level_mm": 0, "points_mm": [(0, 0, 0)],
        "output_path": tmp_path / "preview.json",
    }
    with pytest.raises(RevitIntegrationError, match="one exact host face"):
        create_placement_preview(**arguments)
    with pytest.raises(RevitIntegrationError, match="linked ceiling"):
        create_placement_preview(**arguments, host_faces=[{
            "kind": "linked_face", "link_instance_id": 3, "linked_element_id": 4,
            "linked_document_title": "Structural", "linked_category_key": "floors",
            "face_stable_reference": "fixture:0:RVTLINK:4:0:SURFACE",
        }])


def test_one_level_batch_requires_one_physical_elevation(tmp_path: Path) -> None:
    root = tmp_path / "project"
    workspace = root / "workspace"
    workspace.mkdir(parents=True)
    source = workspace / "source.rvt"
    source.write_bytes(b"source")
    digest = hashlib.sha256(source.read_bytes()).hexdigest().upper()
    with pytest.raises(RevitIntegrationError, match="physical elevation"):
        create_placement_preview(
            workspace_root=root,
            source_model=source,
            expected_source_sha256=digest,
            document_title="Source",
            family_name="Downlight",
            type_name="120V",
            symbol_id=101,
            family_placement_type="OneLevelBased",
            host_requirement="level",
            level_name="L2",
            level_id=9,
            reference_element_id=27,
            elevation_from_level_mm=0,
            points_mm=[(0, 0, 3000), (1000, 0, 3100)],
            output_path=workspace / "preview.json",
        )


@pytest.mark.parametrize("rotations", [[0], [360, 0], [-1, 0], [True, 0]])
def test_placement_preview_rejects_invalid_rotation_contract(
    tmp_path: Path,
    rotations: list[float],
) -> None:
    root = tmp_path / "project"
    workspace = root / "workspace"
    workspace.mkdir(parents=True)
    source = workspace / "source.rvt"
    source.write_bytes(b"source")
    digest = hashlib.sha256(source.read_bytes()).hexdigest().upper()
    with pytest.raises(RevitIntegrationError, match="rotation"):
        create_placement_preview(
            workspace_root=root,
            source_model=source,
            expected_source_sha256=digest,
            document_title="Source",
            family_name="Downlight",
            type_name="120V",
            symbol_id=101,
            family_placement_type="OneLevelBased",
            host_requirement="level",
            level_name="L2",
            level_id=9,
            reference_element_id=27,
            elevation_from_level_mm=0,
            points_mm=[(0, 0, 3000), (1000, 0, 3000)],
            rotations_degrees=rotations,
            output_path=workspace / "preview.json",
        )
