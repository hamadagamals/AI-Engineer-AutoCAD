from __future__ import annotations

import json
from pathlib import Path

import pytest

import launcher
from agent.exceptions import RevitIntegrationError
from core.revit_release_readiness import (
    REVIT_PRODUCT_FILES,
    inspect_revit_release_readiness,
)
from integrations.revit_inventory import load_revit_inventory
from integrations.revit_request_bridge import (
    submit_category_request,
    submit_element_visual_request,
    submit_host_face_request,
    submit_inventory_request,
    submit_lighting_circuit_request,
    submit_electrical_system_request,
    wait_for_response,
)
from integrations.revit_category_query import load_category_query
from integrations.revit_host_face_query import load_host_face_query
from integrations.revit_element_visual_query import load_element_visual_query
from integrations.revit_lighting_circuit_query import load_lighting_circuit_query
from integrations.revit_electrical_system_query import load_electrical_system_query


def _electrical_system_entry(system_id: int = 2001) -> dict[str, object]:
    return {
        "circuit": {
            "system_id": system_id, "system_type": "PowerCircuit", "circuit_number": "5",
            "panel_name": "P109", "load_name": "LIGHTING", "voltage_v": 120.0,
            "apparent_load_va": 200.0, "true_load_w": 180.0, "power_factor": 0.9,
            "poles": 1, "rating_a": 20.0, "frame_a": 20.0,
            "apparent_current_a": 1.666667, "true_current_a": 1.5,
            "voltage_drop_percent": 0.5, "phase_label": "A", "balanced_load": False,
            "start_slot": 5, "wire_size": "1-#12, 1-#12, 1-#12",
            "hot_conductors": 1, "neutral_conductors": 1, "ground_conductors": 1,
            "apparent_current_phase_a": 1.666667, "apparent_current_phase_b": 0.0,
            "apparent_current_phase_c": 0.0, "length_mm": 25000.0, "runs": 1,
            "ways": 1, "circuit_notes": "",
        },
        "load_classifications": "Lighting",
        "member_count": 2,
        "member_category_counts": [{"category_key": "lighting_fixtures", "count": 2}],
    }


def _readiness_fixture(tmp_path: Path) -> tuple[Path, Path, dict[str, str]]:
    project = tmp_path / "project"
    project.mkdir()
    for relative in REVIT_PRODUCT_FILES:
        path = project / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(f"fixture:{relative}\n", encoding="utf-8")
    revit = tmp_path / "Revit 2024"
    revit.mkdir()
    for name in ("Revit.exe", "RevitAPI.dll", "RevitAPIUI.dll"):
        (revit / name).write_bytes(b"fixture")
    windows = tmp_path / "Windows"
    compiler = windows / "Microsoft.NET" / "Framework64" / "v4.0.30319" / "csc.exe"
    compiler.parent.mkdir(parents=True)
    compiler.write_bytes(b"fixture")
    program_files_x86 = tmp_path / "Program Files (x86)"
    framework = (
        program_files_x86 / "Reference Assemblies" / "Microsoft" / "Framework"
        / ".NETFramework" / "v4.8"
    )
    framework.mkdir(parents=True)
    (framework / "System.Runtime.Serialization.dll").write_bytes(b"fixture")
    return project, revit, {
        "WINDIR": str(windows),
        "ProgramFiles(x86)": str(program_files_x86),
    }


def _inventory() -> dict[str, object]:
    return {
        "schema_version": 1,
        "captured_utc": "2026-09-26T00:00:00.0000000Z",
        "revit_version": "2024",
        "command_name": "AIENGINEER_REVIT_READONLY_INVENTORY",
        "status": "captured",
        "document_present": True,
        "document_title": "Sample",
        "is_family_document": False,
        "is_workshared": False,
        "active_view_name": "Level 1",
        "active_view_type": "FloorPlan",
        "counts": {"walls": 4, "rooms": 1, "lighting_fixtures": 6},
        "provider_egress_performed": False,
        "document_mutation_performed": False,
        "limitations": ["Read-only fixture."],
    }


def test_revit_2024_readiness_is_complete_and_non_mutating(tmp_path: Path) -> None:
    project, revit, environment = _readiness_fixture(tmp_path)
    report = inspect_revit_release_readiness(
        project,
        revit_root=revit,
        environment=environment,
        platform_name="Windows",
    )
    assert report.complete is True
    assert report.revit_year == 2024
    assert report.framework_reference_version == "v4.8"
    assert report.provider_egress_performed is False
    assert report.document_mutation_performed is False
    assert report.mutation_capability == (
        "approved_model_lines_new_workspace_rvt; "
        "approved_reference_bound_rotated_same_type_family_batch_new_workspace_rvt; "
        "approved_single_linked_ceiling_family_new_workspace_rvt; "
        "approved_single_lighting_circuit_assignment_new_workspace_rvt"
    )
    assert all(item.startswith("PASS") for item in report.findings)


def test_revit_inventory_loader_enforces_local_read_only_contract(tmp_path: Path) -> None:
    root = tmp_path / "revit"
    root.mkdir()
    path = root / "latest-inventory.json"
    path.write_text(json.dumps(_inventory()), encoding="utf-8")
    inventory = load_revit_inventory(path, allowed_root=root)
    assert inventory.revit_version == "2024"
    assert inventory.counts["lighting_fixtures"] == 6
    assert inventory.provider_egress_performed is False
    assert inventory.document_mutation_performed is False


def test_revit_inventory_rejects_mutation_and_path_escape(tmp_path: Path) -> None:
    root = tmp_path / "revit"
    root.mkdir()
    unsafe = _inventory()
    unsafe["document_mutation_performed"] = True
    inside = root / "unsafe.json"
    inside.write_text(json.dumps(unsafe), encoding="utf-8")
    with pytest.raises(RevitIntegrationError, match="mutation"):
        load_revit_inventory(inside, allowed_root=root)
    outside = tmp_path / "outside.json"
    outside.write_text(json.dumps(_inventory()), encoding="utf-8")
    with pytest.raises(RevitIntegrationError, match="escaped"):
        load_revit_inventory(outside, allowed_root=root)


def test_revit_plugin_declares_read_only_transaction_and_fixed_command() -> None:
    source = Path("integrations/revit_readonly_plugin/ReadOnlyInventoryCommand.cs").read_text(
        encoding="utf-8"
    )
    manifest = Path("integrations/revit_readonly_plugin/AIEngineer.Revit.addin.template").read_text(
        encoding="utf-8"
    )
    assert "[Transaction(TransactionMode.ReadOnly)]" in source
    assert "new Transaction(" not in source
    assert "document_mutation_performed" in source
    assert "AIENGINEER_REVIT_READONLY_INVENTORY" in source
    assert "AIEngineer.Revit.ReadOnlyApplication" in manifest
    assert "IExternalApplication" in source
    assert "Read-Only\\nInventory" in source
    assert "UseSimpleDictionaryFormat = true" in source
    assert "new ElementCategoryFilter(category)" in source
    assert "primary != verification" in source
    bridge = Path("integrations/revit_readonly_plugin/ReadOnlyRequestBridge.cs").read_text(
        encoding="utf-8"
    )
    assert 'string.Equals(request.Action, "inventory"' in bridge
    assert 'string.Equals(request.Action, "family_file_query"' in bridge
    assert "ReadOnlyFamilyFileQuery.Capture" in bridge
    assert "request.DocumentMutationPermitted" in bridge
    assert "request.ProviderEgressPermitted" in bridge
    assert "IExternalEventHandler" in bridge
    assert "ExternalEvent.Create" in source
    assert "FileSystemWatcher" in source
    assert "nextRequestRecoveryUtc" in source
    assert '"requests", "pending.json"' in source
    assert "ReadOnlyCategoryQuery.Capture" in bridge
    assert "ReadOnlyHostFaceQuery.Capture" in bridge
    assert "ReadOnlyElementVisualQuery.CaptureAndFocus" in bridge
    assert "ReadOnlyLightingCircuitQuery.Capture" in bridge
    assert "HostedFamilyPlacementHandoff.Execute" in bridge
    assert "HostedFamilyPlacementHandoff.Verify" in bridge
    category_source = Path(
        "integrations/revit_readonly_plugin/ReadOnlyCategoryQuery.cs"
    ).read_text(encoding="utf-8")
    assert "CreateReferenceInLink" in category_source
    assert "linked Revit face host could not be resolved" in category_source


def test_revit_category_request_and_strict_loader(tmp_path: Path) -> None:
    request_id, response_path = submit_category_request(
        "Fixture", "walls", maximum_elements=2,
        ceiling_hosted_only=True, bridge_root=tmp_path
    )
    pending = json.loads((tmp_path / "requests" / "pending.json").read_text())
    assert pending["action"] == "category_query"
    assert pending["max_elements"] == 2
    assert pending["start_after_element_id"] == 0
    assert pending["host_filter"] == "ceiling_hosted"
    query_root = tmp_path / "queries"
    query_root.mkdir()
    query_path = query_root / f"{request_id}.json"
    query_path.write_text(json.dumps({
        "schema_version": 1, "request_id": request_id, "document_title": "Fixture",
        "category_key": "walls", "host_filter": "ceiling_hosted",
        "total_count": 1, "returned_count": 1,
        "truncated": False, "elements": [{
            "element_id": 1, "category": "Walls", "name": "Wall",
            "type_name": "Generic", "family_name": "Basic Wall", "level_name": "Level 1",
            "type_id": 2, "level_id": 3,
            "curve_length_mm": 1250.0, "diameter_mm": 20.0,
            "width_mm": None, "height_mm": None,
            "bbox_min": {"x_mm": 0, "y_mm": 0, "z_mm": 0},
            "bbox_max": {"x_mm": 1000, "y_mm": 200, "z_mm": 3000},
            "host": {
                "kind": "linked_face", "element_id": 20, "category": "RVT Links",
                "category_key": "revit_links",
                "name": "Architectural", "face_stable_reference": "RVTLINK:20:30:SURFACE",
                "is_linked": True, "linked_element_id": 30,
                "linked_document_title": "Architectural", "linked_category": "Ceilings",
                "linked_category_key": "ceilings",
                "linked_name": "Compound Ceiling",
            },
            "parameters": {"mark": "W-01", "manufacturer": "Fixture"},
        }], "provider_egress_performed": False, "document_mutation_performed": False,
    }), encoding="utf-8")
    query = load_category_query(query_path, allowed_root=tmp_path)
    assert query.total_count == 1
    assert query.elements[0]["element_id"] == 1
    assert query.elements[0]["parameters"]["mark"] == "W-01"
    assert query.elements[0]["host"]["linked_category"] == "Ceilings"
    assert query.host_filter == "ceiling_hosted"
    assert query.elements[0]["curve_length_mm"] == 1250.0
    assert query.start_after_element_id == 0
    assert query.next_start_after_element_id == 0


def test_revit_category_pagination_is_strict(tmp_path: Path) -> None:
    request_id, _ = submit_category_request(
        "Fixture", "electrical_fixtures", maximum_elements=2,
        start_after_element_id=10, bridge_root=tmp_path,
    )
    pending = json.loads((tmp_path / "requests" / "pending.json").read_text())
    assert pending["start_after_element_id"] == 10
    page = tmp_path / "page.json"
    element = {
        "element_id": 11, "category": "Electrical Fixtures", "name": "Outlet",
        "type_name": "Duplex", "family_name": "Receptacle", "level_name": "L1",
        "type_id": 2, "level_id": 3, "parameters": {}, "host": None,
    }
    page.write_text(json.dumps({
        "schema_version": 1, "request_id": request_id, "document_title": "Fixture",
        "category_key": "electrical_fixtures", "host_filter": "all",
        "total_count": 3, "returned_count": 1, "truncated": True,
        "start_after_element_id": 10, "next_start_after_element_id": 11,
        "elements": [element], "provider_egress_performed": False,
        "document_mutation_performed": False,
    }), encoding="utf-8")
    query = load_category_query(page, allowed_root=tmp_path)
    assert query.next_start_after_element_id == 11

    value = json.loads(page.read_text(encoding="utf-8"))
    value["next_start_after_element_id"] = 10
    page.write_text(json.dumps(value), encoding="utf-8")
    with pytest.raises(RevitIntegrationError, match="continuation"):
        load_category_query(page, allowed_root=tmp_path)

    with pytest.raises(RevitIntegrationError, match="cursor"):
        submit_category_request(
            "Fixture", "electrical_fixtures", start_after_element_id=-1,
            bridge_root=tmp_path / "invalid",
        )

    element["curve_length_mm"] = -1
    value = {
        "schema_version": 1, "request_id": request_id, "document_title": "Fixture",
        "category_key": "conduit_fittings", "host_filter": "all",
        "total_count": 1, "returned_count": 1, "truncated": False,
        "start_after_element_id": 0, "next_start_after_element_id": 0,
        "elements": [element], "provider_egress_performed": False,
        "document_mutation_performed": False,
    }
    element["category"] = "Conduit Fittings"
    page.write_text(json.dumps(value), encoding="utf-8")
    with pytest.raises(RevitIntegrationError, match="curve_length_mm"):
        load_category_query(page, allowed_root=tmp_path)


def test_revit_category_loader_rejects_unhosted_element_in_hosted_result(tmp_path: Path) -> None:
    query_path = tmp_path / "query.json"
    query_path.write_text(json.dumps({
        "schema_version": 1, "request_id": "fixture", "document_title": "Fixture",
        "category_key": "lighting_fixtures", "host_filter": "hosted",
        "total_count": 1, "returned_count": 1, "truncated": False,
        "elements": [{
            "element_id": 1, "category": "Lighting Fixtures", "name": "Fixture",
            "type_name": "Type", "family_name": "Family", "level_name": "L2",
            "type_id": 2, "level_id": 3, "parameters": {}, "host": None,
        }], "provider_egress_performed": False, "document_mutation_performed": False,
    }), encoding="utf-8")
    with pytest.raises(RevitIntegrationError, match="unhosted"):
        load_category_query(query_path, allowed_root=tmp_path)


def test_revit_category_loader_rejects_incomplete_linked_host(tmp_path: Path) -> None:
    query_root = tmp_path / "queries"
    query_root.mkdir()
    query_path = query_root / "unsafe.json"
    query_path.write_text(json.dumps({
        "schema_version": 1, "request_id": "request", "document_title": "Fixture",
        "category_key": "lighting_fixtures", "total_count": 1, "returned_count": 1,
        "truncated": False, "elements": [{
            "element_id": 1, "category": "Lighting Fixtures", "name": "Fixture",
            "type_name": "Type", "family_name": "Family", "level_name": "L2",
            "type_id": 2, "level_id": 3, "parameters": {},
            "host": {
                "kind": "linked_face", "element_id": 20, "category": "RVT Links",
                "category_key": "revit_links",
                "name": "Architectural", "face_stable_reference": "",
                "is_linked": True, "linked_element_id": -1,
                "linked_document_title": "", "linked_category": "",
                "linked_category_key": "", "linked_name": "",
            },
        }], "provider_egress_performed": False, "document_mutation_performed": False,
    }), encoding="utf-8")
    with pytest.raises(RevitIntegrationError, match="incomplete"):
        load_category_query(query_path, allowed_root=tmp_path)


def test_revit_host_face_request_and_strict_loader(tmp_path: Path) -> None:
    stable = "fixture:0:RVTLINK:1566775:1:SURFACE"
    request_id, response_path = submit_host_face_request(
        "Fixture", 1622255, stable, maximum_candidates=2, bridge_root=tmp_path
    )
    pending = json.loads((tmp_path / "requests" / "pending.json").read_text())
    assert pending["action"] == "host_face_query"
    assert pending["reference_element_id"] == 1622255
    assert pending["max_elements"] == 2
    result_root = tmp_path / "face-queries"
    result_root.mkdir()
    result = result_root / f"{request_id}.json"
    result.write_text(json.dumps({
        "schema_version": 1, "request_id": request_id, "document_title": "Fixture",
        "reference_element_id": 1622255, "link_instance_id": 1362762,
        "linked_element_id": 1566775,
        "linked_document_title": "Snowdon Towers Sample Architectural",
        "linked_category_key": "ceilings", "face_stable_reference": stable,
        "face_area_mm2": 12000000, "mesh_vertices": 4, "mesh_triangles": 2,
        "candidate_count": 1, "candidates": [{
            "x_mm": 1000, "y_mm": 2000, "z_mm": 2743.2,
            "normal_x": 0, "normal_y": 0, "normal_z": -1,
            "nearest_fixture_clearance_mm": 1800,
        }], "provider_egress_performed": False, "document_mutation_performed": False,
    }), encoding="utf-8")
    query = load_host_face_query(result, allowed_root=tmp_path)
    assert query.linked_element_id == 1566775
    assert query.candidates[0]["nearest_fixture_clearance_mm"] == 1800
    assert response_path.name == f"{request_id}.json"


def test_revit_host_face_loader_rejects_non_unit_normal(tmp_path: Path) -> None:
    result = tmp_path / "unsafe.json"
    result.write_text(json.dumps({
        "schema_version": 1, "request_id": "fixture", "document_title": "Fixture",
        "reference_element_id": 1, "link_instance_id": 2, "linked_element_id": 3,
        "linked_document_title": "Architectural", "linked_category_key": "ceilings",
        "face_stable_reference": "stable", "face_area_mm2": 1,
        "mesh_vertices": 3, "mesh_triangles": 1, "candidate_count": 1,
        "candidates": [{"x_mm": 0, "y_mm": 0, "z_mm": 0, "normal_x": 0,
            "normal_y": 0, "normal_z": 2, "nearest_fixture_clearance_mm": 1}],
        "provider_egress_performed": False, "document_mutation_performed": False,
    }), encoding="utf-8")
    with pytest.raises(RevitIntegrationError, match="normal"):
        load_host_face_query(result, allowed_root=tmp_path)


def test_revit_element_visual_request_and_strict_loader(tmp_path: Path) -> None:
    request_id, response_path = submit_element_visual_request(
        "Fixture", 1735503, bridge_root=tmp_path
    )
    pending = json.loads((tmp_path / "requests" / "pending.json").read_text(encoding="utf-8"))
    assert pending["action"] == "element_visual_query"
    assert pending["reference_element_id"] == 1735503
    assert pending["document_mutation_permitted"] is False
    assert response_path == tmp_path / "responses" / f"{request_id}.json"
    result = tmp_path / "visual.json"
    result.write_text(json.dumps({
        "schema_version": 1,
        "request_id": request_id,
        "document_title": "Fixture",
        "element_id": 1735503,
        "category_key": "lighting_fixtures",
        "family_name": "Recessed Lamp - Round - LED",
        "type_name": "4 inch",
        "active_view_id": 99,
        "active_view_name": "L2 - Lighting",
        "active_view_type": "CeilingPlan",
        "temporary_hide_isolate_was_active": True,
        "visible_in_view": True,
        "model_bounding_box_mm": [0, 0, 0, 200, 200, 100],
        "view_bounding_box_mm": [0, 0, 0, 200, 200, 100],
        "image_file": f"visual-exports/{request_id}.png",
        "facing_orientation": [0, 1, 0],
        "hand_orientation": [1, 0, 0],
        "mirrored": False,
        "facing_flipped": False,
        "hand_flipped": False,
        "hidden_in_view": False,
        "category_hidden_in_view": False,
        "visible_geometry_objects": 8,
        "location_point_mm": [100, 100, 50],
        "elevation_from_level_mm": 50,
        "matching_view_filters": [{
            "filter_id": 8,
            "filter_name": "Uncircuited",
            "visible": True,
            "projection_line_color": [255, 255, 255],
        }],
        "element_projection_line_color": [],
        "provider_egress_performed": False,
        "document_mutation_performed": False,
    }), encoding="utf-8")
    query = load_element_visual_query(result, allowed_root=tmp_path)
    assert query.element_id == 1735503
    assert query.visible_in_view is True
    assert query.active_view_name == "L2 - Lighting"
    assert query.image_file.endswith(".png")
    assert query.visible_geometry_objects == 8
    assert query.location_point_mm == (100.0, 100.0, 50.0)
    assert query.elevation_from_level_mm == 50.0
    assert query.matching_view_filters[0]["filter_name"] == "Uncircuited"


def test_revit_lighting_circuit_request_and_strict_loader(tmp_path: Path) -> None:
    request_id, response_path = submit_lighting_circuit_request(
        "Fixture", maximum_elements=4, element_id=1735503, bridge_root=tmp_path
    )
    pending = json.loads((tmp_path / "requests" / "pending.json").read_text(encoding="utf-8"))
    assert pending["action"] == "lighting_circuit_query"
    assert pending["reference_element_id"] == 1735503
    assert pending["start_after_element_id"] == 0
    assert pending["max_elements"] == 4
    assert pending["document_mutation_permitted"] is False
    assert response_path == tmp_path / "responses" / f"{request_id}.json"
    result = tmp_path / "lighting-circuit.json"
    result.write_text(json.dumps({
        "schema_version": 1,
        "request_id": request_id,
        "document_title": "Fixture",
        "requested_element_id": 1735503,
        "total_fixture_count": 1,
        "returned_fixture_count": 1,
        "truncated": False,
        "fixtures": [{
            "element_id": 1735503,
            "family_name": "Recessed Lamp - Round - LED",
            "type_name": "4 inch",
            "mark": "712",
            "circuit_count": 1,
            "connector_count": 1,
            "connectors": [{
                "connector_id": 1,
                "domain": "DomainElectrical",
                "connector_type": "Logical",
                "electrical_system_type": "PowerCircuit",
                "mep_system_id": 2001,
            }],
            "voltage_v": 120.0,
            "apparent_load_va": 20.0,
            "circuits": [{
                "system_id": 2001,
                "system_type": "PowerCircuit",
                "circuit_number": "5",
                "panel_name": "P109",
                "load_name": "LIGHTING",
                "voltage_v": 120.0,
                "apparent_load_va": 20.0,
                "true_load_w": 18.0,
                "power_factor": 0.9,
                "poles": 1,
                "rating_a": 20.0,
                "frame_a": 20.0,
                "apparent_current_a": 0.166667,
                "true_current_a": 0.15,
                "voltage_drop_percent": 0.1,
                "phase_label": "A",
                "balanced_load": False,
                "start_slot": 5,
                "wire_size": "#12",
                "hot_conductors": 1,
                "neutral_conductors": 1,
                "ground_conductors": 1,
                "apparent_current_phase_a": 0.166667,
                "apparent_current_phase_b": 0.0,
                "apparent_current_phase_c": 0.0,
                "length_mm": 25000.0,
                "runs": 1,
                "ways": 1,
                "circuit_notes": "",
                "protection_parameters": [{
                    "source": "instance",
                    "name": "Rating",
                    "parameter_id": -1140137,
                    "storage_type": "Double",
                    "value_display": "20 A",
                }],
                "wire_type": {
                    "element_id": 4001,
                    "name": "THHN Copper",
                    "material": "Copper",
                    "temperature_rating": "75 C",
                    "insulation": "THHN",
                    "conduit": "Steel",
                    "maximum_size": "500 kcmil",
                    "maximum_size_ampacity_a": 380,
                    "neutral_required": True,
                    "neutral_mode": "HotConductorSize",
                },
                "panel": {
                    "element_id": 3001,
                    "family_name": "Panelboard",
                    "type_name": "120/208 V",
                    "mcb_rating_a": 225.0,
                    "voltage_v": 120.0,
                    "number_of_phases": 3,
                    "number_of_wires": 4,
                    "total_connected_current_a": 80.0,
                    "total_demand_current_a": 60.0,
                    "supply_from": "MDP",
                    "mains_type": "MCB",
                    "current_phase_a": 60.0,
                    "current_phase_b": 55.0,
                    "current_phase_c": 58.0,
                    "short_circuit_rating_display": "10 kA",
                    "protection_parameters": [{
                        "source": "type",
                        "name": "Short Circuit Rating",
                        "parameter_id": -1140106,
                        "storage_type": "String",
                        "value_display": "10 kA",
                    }],
                    "incoming_systems": [{
                        "system_id": 5001,
                        "system_type": "PowerCircuit",
                        "circuit_number": "12",
                        "panel_name": "MDP",
                        "load_name": "P109",
                        "voltage_v": 208.0,
                        "apparent_load_va": 12000.0,
                        "true_load_w": 10800.0,
                        "power_factor": 0.9,
                        "poles": 3,
                        "rating_a": 100.0,
                        "frame_a": 100.0,
                        "apparent_current_a": 33.3,
                        "voltage_drop_percent": 0.8,
                        "length_mm": 30000.0,
                        "wire_size": "3-#3, 1-#3, 1-#8",
                        "protection_parameters": [],
                        "upstream_panel": {
                            "element_id": 5002,
                            "family_name": "Switchboard",
                            "type_name": "800 A",
                            "mcb_rating_a": 800.0,
                            "short_circuit_rating_display": "35 kA",
                            "protection_parameters": [],
                        },
                    }],
                },
            }],
        }],
        "provider_egress_performed": False,
        "document_mutation_performed": False,
    }), encoding="utf-8")
    query = load_lighting_circuit_query(result, allowed_root=tmp_path)
    assert query.requested_element_id == 1735503
    assert query.start_after_element_id == 0
    assert query.next_start_after_element_id == 0
    assert query.fixtures[0]["circuits"][0]["panel_name"] == "P109"
    assert query.fixtures[0]["circuits"][0]["protection_parameters"][0]["value_display"] == "20 A"
    assert query.fixtures[0]["circuits"][0]["panel"]["incoming_systems"][0]["panel_name"] == "MDP"

    first_page_value = json.loads(result.read_text(encoding="utf-8"))
    first_page_value.update({
        "requested_element_id": 0,
        "total_fixture_count": 2,
        "returned_fixture_count": 1,
        "truncated": True,
        "start_after_element_id": 0,
        "next_start_after_element_id": 1735503,
    })
    first_page = tmp_path / "lighting-circuit-page-1.json"
    first_page.write_text(json.dumps(first_page_value), encoding="utf-8")
    page_one = load_lighting_circuit_query(first_page, allowed_root=tmp_path)
    assert page_one.next_start_after_element_id == 1735503

    second_page_value = dict(first_page_value)
    second_page_value.update({
        "truncated": False,
        "start_after_element_id": 1735503,
        "next_start_after_element_id": 0,
    })
    second_page_value["fixtures"] = [dict(first_page_value["fixtures"][0])]
    second_page_value["fixtures"][0]["element_id"] = 1735504
    second_page = tmp_path / "lighting-circuit-page-2.json"
    second_page.write_text(json.dumps(second_page_value), encoding="utf-8")
    page_two = load_lighting_circuit_query(second_page, allowed_root=tmp_path)
    assert page_two.start_after_element_id == 1735503
    assert page_two.next_start_after_element_id == 0

    second_page_value["next_start_after_element_id"] = 1735504
    second_page.write_text(json.dumps(second_page_value), encoding="utf-8")
    with pytest.raises(RevitIntegrationError, match="Final .* continuation"):
        load_lighting_circuit_query(second_page, allowed_root=tmp_path)

    with pytest.raises(RevitIntegrationError, match="request is invalid"):
        submit_lighting_circuit_request(
            "Fixture", element_id=1735503, start_after_element_id=10,
            bridge_root=tmp_path / "invalid-request",
        )


def test_revit_electrical_system_request_and_strict_loader(tmp_path: Path) -> None:
    request_id, response_path = submit_electrical_system_request(
        "Fixture", maximum_elements=4, bridge_root=tmp_path
    )
    pending = json.loads((tmp_path / "requests" / "pending.json").read_text(encoding="utf-8"))
    assert pending["action"] == "electrical_system_query"
    assert pending["start_after_element_id"] == 0
    assert pending["document_mutation_permitted"] is False
    assert response_path == tmp_path / "responses" / f"{request_id}.json"
    result = tmp_path / "electrical-system.json"
    result.write_text(json.dumps({
        "schema_version": 1, "request_id": request_id, "document_title": "Fixture",
        "total_system_count": 1, "returned_system_count": 1, "truncated": False,
        "systems": [_electrical_system_entry()], "start_after_element_id": 0,
        "next_start_after_element_id": 0, "provider_egress_performed": False,
        "document_mutation_performed": False,
    }), encoding="utf-8")
    query = load_electrical_system_query(result, allowed_root=tmp_path)
    assert query.total_system_count == 1
    assert query.systems[0]["circuit"]["panel_name"] == "P109"

    invalid = json.loads(result.read_text(encoding="utf-8"))
    invalid["systems"][0]["member_count"] = 3
    result.write_text(json.dumps(invalid), encoding="utf-8")
    with pytest.raises(RevitIntegrationError, match="do not reconcile"):
        load_electrical_system_query(result, allowed_root=tmp_path)

    with pytest.raises(RevitIntegrationError, match="request is invalid"):
        submit_electrical_system_request(
            "Fixture", maximum_elements=33, bridge_root=tmp_path / "invalid"
        )


def test_revit_request_bridge_correlates_a_read_only_response(tmp_path: Path) -> None:
    request_id, response_path = submit_inventory_request(
        "Fixture", bridge_root=tmp_path
    )
    pending = json.loads((tmp_path / "requests" / "pending.json").read_text())
    assert pending["request_id"] == request_id
    assert pending["action"] == "inventory"
    assert pending["provider_egress_permitted"] is False
    assert pending["document_mutation_permitted"] is False
    response_path.write_text(
        json.dumps(
            {
                "schema_version": 1,
                "request_id": request_id,
                "action": "inventory",
                "status": "completed",
                "document_title": "Fixture",
                "output_file": "latest-inventory.json",
                "provider_egress_performed": False,
                "document_mutation_performed": False,
                "error": "",
            }
        ),
        encoding="utf-8",
    )
    response = wait_for_response(request_id, response_path, timeout_seconds=1)
    assert response.status == "completed"
    assert response.document_title == "Fixture"


def test_revit_request_bridge_rejects_mutation_response(tmp_path: Path) -> None:
    request_id, response_path = submit_inventory_request(
        "Fixture", bridge_root=tmp_path
    )
    response_path.write_text(
        json.dumps(
            {
                "schema_version": 1,
                "request_id": request_id,
                "action": "inventory",
                "status": "completed",
                "document_title": "Fixture",
                "output_file": "latest-inventory.json",
                "provider_egress_performed": False,
                "document_mutation_performed": True,
                "error": "",
            }
        ),
        encoding="utf-8",
    )
    with pytest.raises(RevitIntegrationError, match="mutation"):
        wait_for_response(request_id, response_path, timeout_seconds=1)


def test_launcher_revit_readiness_is_machine_readable(capsys: pytest.CaptureFixture[str]) -> None:
    assert launcher.main(["revit-readiness"]) == 0
    value = json.loads(capsys.readouterr().out)
    assert value["readiness_version"] == "revit.release_readiness.v29"
    assert value["revit_year"] == 2024
    assert value["provider_egress_performed"] is False
    assert value["document_mutation_performed"] is False
