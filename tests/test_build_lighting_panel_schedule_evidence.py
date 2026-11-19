import json

from integrations.build_lighting_panel_schedule_evidence import build_candidate_evidence


def _entity(handle, entity_type, *, points, layer="0", text=None, block_name=None):
    return {
        "object_id": handle,
        "entity_type": entity_type,
        "layer": layer,
        "points": [[x, y, 0.0] for x, y in points],
        "text": text,
        "block_name": block_name,
    }


def test_candidate_membership_keeps_crossings_disconnected_and_calculates_load():
    manifest = {
        "index_id": "index-1",
        "entity_count": 5,
        "document": {"source_drawing_sha256": "a" * 64},
    }
    entities = [
        _entity("R1", "LINE", points=[(0, 0), (1000, 0)], layer="BLOCK003-G$0$E-Lite-Circ"),
        _entity("R2", "LINE", points=[(500, -500), (500, 500)], layer="BLOCK003-G$0$E-Lite-Circ"),
        _entity("T1", "MTEXT", points=[(0, 10)], text="4/ DB3-GA2"),
        _entity(
            "B1",
            "INSERT",
            points=[(900, 50)],
            block_name="EHAF-ID-LIGHTING FIXTURE-REGTANGULAR SPOT_L01_ROOMS",
        ),
        _entity("B2", "INSERT", points=[(500, 450)], block_name="unclassified"),
    ]

    result = build_candidate_evidence(manifest, entities, device_snap_tolerance=100.0)

    assert result["method"]["crossing_routes_connected"] is False
    row = result["circuits"][0]
    assert row["panel"] == "DB3-GA2"
    assert row["circuit"] == "4"
    assert row["candidate_route_count"] == 1
    assert row["classified_load_count"] == 1
    assert row["provisional_connected_w"] == 35.0
    assert row["provisional_connected_va"] == 38.888889
    assert row["membership_status"] == "CANDIDATE-SPATIAL-NOT-REVIEWED"


def test_tag_prefers_nearby_load_component_over_annotation_only_route():
    manifest = {
        "index_id": "index-2",
        "entity_count": 4,
        "document": {"source_drawing_sha256": "b" * 64},
    }
    entities = [
        _entity("ARROW", "LINE", points=[(0, 0), (20, 0)], layer="BLOCK003-G$0$E-Lite-Circ"),
        _entity("WIRE", "LINE", points=[(0, 100), (1000, 100)], layer="BLOCK003-G$0$E-Lite-Circ"),
        _entity("TAG", "MTEXT", points=[(10, 10)], text="4/ DB3-GA2"),
        _entity(
            "LOAD",
            "INSERT",
            points=[(900, 100)],
            block_name="EHAF-ID-LIGHTING FIXTURE-REGTANGULAR SPOT_L01_ROOMS",
        ),
    ]

    result = build_candidate_evidence(manifest, entities, device_snap_tolerance=20.0)

    row = result["circuits"][0]
    assert row["candidate_route_count"] == 1
    assert row["classified_load_count"] == 1
    assert result["tag_observations"][0]["nearest_route_handle"] == "WIRE"


def test_equivalent_duplicate_occurrences_are_not_summed():
    manifest = {
        "index_id": "index-3",
        "entity_count": 6,
        "document": {"source_drawing_sha256": "c" * 64},
    }
    entities = [
        _entity("R1", "LINE", points=[(0, 0), (1000, 0)], layer="BLOCK003-G$0$E-Lite-Circ"),
        _entity("R2", "LINE", points=[(5000, 0), (6000, 0)], layer="BLOCK003-G$0$E-Lite-Circ"),
        _entity("T1", "MTEXT", points=[(0, 10)], text="4/ DB3-GA2"),
        _entity("T2", "MTEXT", points=[(5000, 10)], text="4/ DB3-GA2"),
        _entity("B1", "INSERT", points=[(900, 0)], block_name="SPOT Toilet"),
        _entity("B2", "INSERT", points=[(5900, 0)], block_name="SPOT Toilet"),
    ]

    result = build_candidate_evidence(manifest, entities, device_snap_tolerance=20.0)

    row = result["circuits"][0]
    assert row["load_bearing_occurrence_count"] == 2
    assert row["duplicate_equivalent"] is True
    assert row["occurrence_conflict"] is False
    assert row["provisional_connected_w"] == 28.0
    assert row["membership_status"] == "CANDIDATE-DUPLICATE-EQUIVALENT-NOT-REVIEWED"


def test_schedule_rows_leave_unobserved_ways_blank_not_spare():
    manifest = {
        "index_id": "index-4",
        "entity_count": 3,
        "document": {"source_drawing_sha256": "d" * 64},
    }
    entities = [
        _entity("R1", "LINE", points=[(0, 0), (1000, 0)], layer="BLOCK003-G$0$E-Lite-Circ"),
        _entity("T1", "MTEXT", points=[(0, 10)], text="3/ DB2-B"),
        _entity("B1", "INSERT", points=[(900, 0)], block_name="SPOT Toilet"),
    ]

    result = build_candidate_evidence(manifest, entities, device_snap_tolerance=20.0)

    panel = result["schedule_panels"][0]
    assert [row["way"] for row in panel["ways"]] == [1, 2, 3]
    assert panel["ways"][0]["description"] is None
    assert panel["ways"][0]["status"] == "BLANK-NOT-OBSERVED-IN-LIGHTING-DRAWING"
    assert "spare" not in json.dumps(panel, ensure_ascii=False).casefold()
