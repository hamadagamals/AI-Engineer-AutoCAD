"""Tests for exact reviewed AutoCAD circuit-membership sidecars."""

from __future__ import annotations

import hashlib
import json
from dataclasses import replace
from pathlib import Path

import pytest

from agent.exceptions import AutoCADContextError
from integrations.autocad_context import (
    ContextLimits,
    map_context_to_electrical_model,
    parse_autocad_context,
)
from integrations.autocad_project_conventions import (
    BlockConvention,
    TagConvention,
    create_project_convention_profile,
)
from integrations.autocad_project_topology import (
    ConnectionAnchor,
    RouteEndpointConnection,
    TagObservation,
    UnresolvedRouteEndpoint,
    apply_reviewed_topology,
    assemble_reviewed_connection_anchors,
    create_reviewed_connection_anchors,
    create_reviewed_topology,
    load_connection_anchor_capture,
    parse_connection_anchor_capture,
    parse_reviewed_connection_anchors,
    parse_reviewed_topology,
    create_reviewed_route_membership,
    parse_reviewed_route_membership,
    validate_reviewed_connection_anchors,
    validate_reviewed_route_membership,
)


ROOT = Path(__file__).resolve().parents[1]
CATALOG = ROOT / "plugins" / "electrical_symbols" / "catalog.json"


def _capture_raw() -> dict[str, object]:
    return {
        "schema_version": 1,
        "capture_version": "autocad.connection_anchor_capture.v1",
        "capture_id": "c" * 32,
        "captured_at": "2026-08-16T10:00:00.0000000Z",
        "document": {
            "document_id": "12345678-1234-1234-1234-1234567890ab",
            "identity_source": "fingerprint_guid",
            "name": "lighting.dwg",
            "drawing_units": 4,
            "layout": "Model",
            "database_modified_before": 16,
            "database_modified_after": 16,
        },
        "selection": {
            "device_object_id": "A1",
            "block_name": "L02",
            "route_object_id": "C1",
            "route_entity_type": "LWPOLYLINE",
            "endpoint": "start",
            "world_position": [10.0, 20.0, 0.0],
            "endpoint_pick_position": [11.0, 20.0, 0.0],
            "endpoint_pick_distance": 1.0,
            "alternate_endpoint_distance": 100.0,
            "endpoint_selection_method": "engineer_pick_nearest_endpoint",
            "terminal_name": "L",
        },
        "drawing_mutation_performed": False,
    }


def test_connection_anchor_capture_is_exact_hash_bound_and_read_only(tmp_path: Path) -> None:
    raw = _capture_raw()
    parsed = parse_connection_anchor_capture(raw)
    assert parsed.selection.world_position == (10.0, 20.0, 0.0)
    encoded = json.dumps(raw, separators=(",", ":")).encode("utf-8")
    path = tmp_path / "capture.json"
    path.write_bytes(encoded)
    digest = hashlib.sha256(encoded).hexdigest()
    assert load_connection_anchor_capture(path, expected_sha256=digest) == parsed
    with pytest.raises(AutoCADContextError, match="SHA-256"):
        load_connection_anchor_capture(path, expected_sha256="0" * 64)
    changed = json.loads(json.dumps(raw))
    changed["drawing_mutation_performed"] = True
    with pytest.raises(AutoCADContextError, match="must not mutate"):
        parse_connection_anchor_capture(changed)
    changed = json.loads(json.dumps(raw))
    changed["document"]["database_modified_before"] = 20
    changed["document"]["database_modified_after"] = 20
    with pytest.raises(AutoCADContextError, match="unsupported DBMOD"):
        parse_connection_anchor_capture(changed)


def test_autocad_plugin_exposes_explicit_read_only_connection_anchor_capture() -> None:
    source = (ROOT / "integrations" / "autocad_context_plugin" / "ContextCommands.cs").read_text(encoding="utf-8")
    assert '[CommandMethod("AIENGCONNECTIONANCHOR"' in source
    assert '"drawing_mutation_performed", false' in source
    assert 'int commandStartDbmod = GetDbmod();' in source
    assert 'DBMOD changed during read-only anchor capture from ' in source
    assert 'requires clean or presentation-only DBMOD (8/16); current DBMOD=' in source
    assert '"route_object_id", route.Handle.ToString()' in source
    assert '"endpoint_selection_method", "engineer_pick_nearest_endpoint"' in source
    assert "endpointResult.Value.DistanceTo(start)" in source


def _values() -> tuple[object, str, object, object]:
    raw = {
        "schema_version": 1,
        "context_id": "a" * 32,
        "captured_at": "2026-08-13T10:00:00.0000000Z",
        "source_mode": "selection",
        "privacy": {"classification": "project_confidential", "provider_egress_allowed": False},
        "document": {"document_id": "12345678-1234-1234-1234-1234567890ab", "identity_source": "fingerprint_guid", "name": "lighting.dwg", "drawing_units": 4, "layout": "Model", "database_modified": 16},
        "limits": ContextLimits().to_dict(), "truncated": False, "truncation_reasons": [],
        "layers": [{"name": "E-LITE", "color_index": 3, "linetype": "Continuous", "is_off": False, "is_frozen": False, "is_locked": False}, {"name": "TAGS", "color_index": 7, "linetype": "Continuous", "is_off": False, "is_frozen": False, "is_locked": False}],
        "entities": [
            {"object_id": "A1", "entity_type": "INSERT", "original_type": "Autodesk.AutoCAD.DatabaseServices.BlockReference", "layer": "E-LITE", "layout": "Model", "visible": True, "points": [[0, 0, 0]], "radius": None, "rotation_degrees": 0, "scale": [1, 1, 1], "block_name": "L05", "text": None, "attributes": []},
            {"object_id": "B1", "entity_type": "MTEXT", "original_type": "Autodesk.AutoCAD.DatabaseServices.MText", "layer": "TAGS", "layout": "Model", "visible": True, "points": [[1, 0, 0]], "radius": None, "rotation_degrees": None, "scale": None, "block_name": None, "text": "4/ DB3-GA2", "attributes": []},
            {"object_id": "B2", "entity_type": "MTEXT", "original_type": "Autodesk.AutoCAD.DatabaseServices.MText", "layer": "TAGS", "layout": "Model", "visible": True, "points": [[2, 0, 0]], "radius": None, "rotation_degrees": None, "scale": None, "block_name": None, "text": "a", "attributes": []},
        ],
    }
    evidence = hashlib.sha256(json.dumps(raw).encode()).hexdigest()
    context = parse_autocad_context(raw, expected_limits=ContextLimits())
    profile = create_project_convention_profile(
        profile_id="profile:test", document_id=context.document_id,
        context_id=context.context_id, context_evidence_sha256=evidence,
        source_drawing_sha256="d" * 64, reviewed_by="engineer:test",
        reviewed_on="2026-08-13",
        block_mappings=(BlockConvention("L05", "E-LITE", "lighting_l05", "Reviewed L05."),),
        route_mappings=(),
        tag_mappings=(TagConvention("TAGS", "circuit_reference", "circuit_number_slash_panel_id"), TagConvention("TAGS", "control_group", "comma_separated_control_ids")),
        assumptions=("No spatial association.",),
    )
    model = map_context_to_electrical_model(context, evidence_sha256=evidence, symbol_catalog_path=CATALOG, conventions=profile)
    topology = create_reviewed_topology(
        topology_id="topology:test", document_id=context.document_id,
        context_id=context.context_id, context_evidence_sha256=evidence,
        source_drawing_sha256="d" * 64,
        convention_profile_sha256=profile.profile_sha256,
        panel_name="DB3-GA2", circuit_name="4", circuit_tag_object_id="B1",
        device_object_ids=("A1",), control_tags=(TagObservation("B2", "a"),),
        environmental_qualifiers=(), control_associations_performed=False,
        route_connectivity_performed=False, reviewed_by="engineer:test",
        reviewed_on="2026-08-13", assumptions=("All selected devices belong to circuit 4.",),
    )
    return model, evidence, profile, topology


def test_reviewed_topology_applies_exact_handle_membership() -> None:
    model, evidence, profile, topology = _values()
    rebound = parse_reviewed_topology(json.loads(json.dumps(topology.to_dict())))
    result = apply_reviewed_topology(
        model, rebound, context_evidence_sha256=evidence,
        source_drawing_sha256="d" * 64,
        convention_profile_sha256=profile.profile_sha256,
    )
    assert result.circuits[0].device_ids == (model.devices[0].device_id,)
    assert "control tags and route connectivity remain unassociated" in result.warnings[-1]


def test_reviewed_topology_rejects_tampering_mismatch_and_inferred_links() -> None:
    model, evidence, profile, topology = _values()
    tampered = json.loads(json.dumps(topology.to_dict()))
    tampered["device_object_ids"] = ["FFFF"]
    with pytest.raises(AutoCADContextError, match="SHA-256"):
        parse_reviewed_topology(tampered)
    foreign = create_reviewed_topology(
        topology_id=topology.topology_id, document_id=topology.document_id,
        context_id=topology.context_id,
        context_evidence_sha256=topology.context_evidence_sha256,
        source_drawing_sha256=topology.source_drawing_sha256,
        convention_profile_sha256=topology.convention_profile_sha256,
        panel_name=topology.panel_name, circuit_name=topology.circuit_name,
        circuit_tag_object_id=topology.circuit_tag_object_id,
        device_object_ids=("FFFF",), control_tags=topology.control_tags,
        environmental_qualifiers=topology.environmental_qualifiers,
        control_associations_performed=False, route_connectivity_performed=False,
        reviewed_by=topology.reviewed_by, reviewed_on=topology.reviewed_on,
        assumptions=topology.assumptions,
    )
    with pytest.raises(AutoCADContextError, match="device handles"):
        apply_reviewed_topology(
            model, foreign,
            context_evidence_sha256=evidence, source_drawing_sha256="d" * 64,
            convention_profile_sha256=profile.profile_sha256,
        )


def test_reviewed_route_membership_reproduces_geometry_without_connectivity_claim() -> None:
    model, evidence, profile, _ = _values()
    route_source = model.devices[0].source
    from core.electrical_model import Route
    route = Route("route:1", model.layers[0].layer_id, ((0.0, 0.0, 0.0), (3000.0, 4000.0, 0.0)), replace(route_source, object_id="C1"))
    route_model = replace(model, devices=(), circuits=(), panels=(), routes=(route,))
    membership = create_reviewed_route_membership(
        membership_id="topology:route-test", document_id="12345678-1234-1234-1234-1234567890ab",
        context_id="a" * 32, context_evidence_sha256=evidence,
        source_drawing_sha256="d" * 64, convention_profile_sha256=profile.profile_sha256,
        panel_name="DB3-GA2", circuit_name="4", route_object_ids=("C1",),
        annotation_route_object_ids=(), drawing_unit="millimeter", observed_centerline_meters=5.0,
        annotation_centerline_meters=0.0,
        connectivity_confirmed=False, installed_cable_length_confirmed=False,
        reviewed_by="engineer:test", reviewed_on="2026-08-13",
        assumptions=("Centerline only.",),
    )
    parsed = parse_reviewed_route_membership(json.loads(json.dumps(membership.to_dict())))
    assert validate_reviewed_route_membership(route_model, parsed, context_evidence_sha256=evidence, source_drawing_sha256="d" * 64, convention_profile_sha256=profile.profile_sha256) == 5.0
    with pytest.raises(AutoCADContextError, match="must remain unconfirmed"):
        create_reviewed_route_membership(
            membership_id="topology:route-test", document_id="12345678-1234-1234-1234-1234567890ab",
            context_id="a" * 32, context_evidence_sha256="e" * 64,
            source_drawing_sha256="d" * 64, convention_profile_sha256=profile.profile_sha256,
            panel_name="DB3-GA2", circuit_name="4", route_object_ids=("C1",),
            annotation_route_object_ids=(), drawing_unit="millimeter", observed_centerline_meters=5.0,
            annotation_centerline_meters=0.0,
            connectivity_confirmed=True, installed_cable_length_confirmed=False,
            reviewed_by="engineer:test", reviewed_on="2026-08-13",
            assumptions=("No inferred connectivity.",),
        )


def _connection_values() -> tuple[object, object, object, object, object]:
    device_model, device_evidence, profile, topology = _values()
    from core.electrical_model import Route

    route_context_id = "b" * 32
    route_source = replace(
        device_model.devices[0].source,
        object_id="C1",
        context_id=route_context_id,
    )
    route = Route(
        "route:1",
        device_model.layers[0].layer_id,
        ((0.0, 0.0, 0.0), (3000.0, 0.0, 0.0)),
        route_source,
    )
    route_model = replace(
        device_model,
        context_id=route_context_id,
        devices=(),
        circuits=(),
        panels=(),
        routes=(route,),
    )
    route_evidence = "e" * 64
    membership = create_reviewed_route_membership(
        membership_id="topology:route-connections",
        document_id="12345678-1234-1234-1234-1234567890ab",
        context_id=route_context_id,
        context_evidence_sha256=route_evidence,
        source_drawing_sha256="d" * 64,
        convention_profile_sha256="f" * 64,
        panel_name="DB3-GA2",
        circuit_name="4",
        route_object_ids=("C1",),
        annotation_route_object_ids=(),
        drawing_unit="millimeter",
        observed_centerline_meters=3.0,
        annotation_centerline_meters=0.0,
        connectivity_confirmed=False,
        installed_cable_length_confirmed=False,
        reviewed_by="engineer:test",
        reviewed_on="2026-08-13",
        assumptions=("Centerline membership only.",),
    )
    review = create_reviewed_connection_anchors(
        review_id="topology:connection-test",
        document_id="12345678-1234-1234-1234-1234567890ab",
        source_drawing_sha256="d" * 64,
        device_context_id=device_model.context_id,
        device_context_evidence_sha256=device_evidence,
        device_convention_profile_sha256=profile.profile_sha256,
        device_topology_sha256=topology.topology_sha256,
        route_context_id=route_context_id,
        route_context_evidence_sha256=route_evidence,
        route_convention_profile_sha256="f" * 64,
        route_membership_sha256=membership.membership_sha256,
        capture_evidence_sha256s=("9" * 64,),
        panel_name="DB3-GA2",
        circuit_name="4",
        anchors=(ConnectionAnchor("anchor:test", "A1", "L", (0.0, 0.0, 0.0)),),
        connections=(RouteEndpointConnection("C1", "start", "anchor:test"),),
        unresolved_endpoints=(
            UnresolvedRouteEndpoint("C1", "end", "No reviewed downstream device."),
        ),
        all_route_endpoints_reviewed=True,
        reviewed_by="engineer:test",
        reviewed_on="2026-08-13",
        assumptions=("Only explicit engineer-reviewed endpoint choices are recorded.",),
    )
    return device_model, route_model, topology, membership, review


def test_reviewed_connection_anchors_validate_exact_endpoint_accounting() -> None:
    device_model, route_model, topology, membership, review = _connection_values()
    parsed = parse_reviewed_connection_anchors(json.loads(json.dumps(review.to_dict())))
    result = validate_reviewed_connection_anchors(
        device_model,
        route_model,
        parsed,
        topology=topology,
        route_membership=membership,
    )
    assert result.connected_endpoint_count == 1
    assert result.unresolved_endpoint_count == 1
    assert result.total_endpoint_count == 2
    assert result.complete_connectivity is False


def test_loaded_anchor_capture_assembles_into_hash_bound_review() -> None:
    device_model, route_model, topology, membership, _ = _connection_values()
    raw = _capture_raw()
    selection = raw["selection"]
    assert isinstance(selection, dict)
    selection["block_name"] = "L05"
    selection["world_position"] = [0.0, 0.0, 0.0]
    selection["endpoint_pick_position"] = [10.0, 0.0, 0.0]
    selection["endpoint_pick_distance"] = 10.0
    selection["alternate_endpoint_distance"] = 2990.0
    capture = parse_connection_anchor_capture(raw)
    review = assemble_reviewed_connection_anchors(
        device_model=device_model,
        route_model=route_model,
        topology=topology,
        route_membership=membership,
        captures=((capture, "8" * 64),),
        unresolved_endpoints=(
            UnresolvedRouteEndpoint("C1", "end", "No reviewed downstream device."),
        ),
        review_id="topology:assembled-test",
        reviewed_by="engineer:test",
        reviewed_on="2026-08-16",
        assumptions=("Only loaded exact capture evidence was assembled.",),
    )
    assert review.capture_evidence_sha256s == ("8" * 64,)
    assert review.connections == (
        RouteEndpointConnection("C1", "start", "anchor:" + "c" * 32),
    )
    with pytest.raises(AutoCADContextError, match="repeats"):
        assemble_reviewed_connection_anchors(
            device_model=device_model,
            route_model=route_model,
            topology=topology,
            route_membership=membership,
            captures=((capture, "8" * 64), (capture, "7" * 64)),
            unresolved_endpoints=(),
            review_id="topology:duplicate-test",
            reviewed_by="engineer:test",
            reviewed_on="2026-08-16",
            assumptions=("Duplicate endpoint must fail.",),
        )


def test_reviewed_connection_anchors_reject_tampering_gaps_and_geometry_mismatch() -> None:
    device_model, route_model, topology, membership, review = _connection_values()
    tampered = json.loads(json.dumps(review.to_dict()))
    tampered["connections"][0]["anchor_id"] = "anchor:foreign"
    with pytest.raises(AutoCADContextError, match="reviewed anchor"):
        parse_reviewed_connection_anchors(tampered)

    values = review.to_dict()
    values.pop("review_sha256")
    values.pop("schema_version")
    values.pop("connection_version")
    values["anchors"] = review.anchors
    values["connections"] = review.connections
    values["assumptions"] = review.assumptions
    values["unresolved_endpoints"] = ()
    incomplete = create_reviewed_connection_anchors(**values)
    with pytest.raises(AutoCADContextError, match="every exact route endpoint"):
        validate_reviewed_connection_anchors(
            device_model,
            route_model,
            incomplete,
            topology=topology,
            route_membership=membership,
        )

    values["unresolved_endpoints"] = review.unresolved_endpoints
    values["anchors"] = (
        ConnectionAnchor("anchor:test", "A1", "L", (1.0, 0.0, 0.0)),
    )
    displaced = create_reviewed_connection_anchors(**values)
    with pytest.raises(AutoCADContextError, match="exactly match"):
        validate_reviewed_connection_anchors(
            device_model,
            route_model,
            displaced,
            topology=topology,
            route_membership=membership,
        )
    with pytest.raises(AutoCADContextError, match="must remain false"):
        create_reviewed_topology(
            topology_id="topology:test", document_id="12345678-1234-1234-1234-1234567890ab",
            context_id="a" * 32, context_evidence_sha256="e" * 64,
            source_drawing_sha256="d" * 64, convention_profile_sha256="e" * 64,
            panel_name="DB3-GA2", circuit_name="4", circuit_tag_object_id="B1",
            device_object_ids=("A1",), control_tags=(), environmental_qualifiers=(),
            control_associations_performed=True, route_connectivity_performed=False,
            reviewed_by="engineer:test", reviewed_on="2026-08-13",
            assumptions=("No inferred controls.",),
        )
