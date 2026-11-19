"""Tests for the preliminary Schneider panel general arrangement."""

from integrations.build_schneider_panel_ga_preview import build_preview, render_dxf


def test_preview_is_one_to_one_and_keeps_incomplete_references_explicit():
    preview = build_preview()

    assert preview["enclosure"]["width_mm"] == 800.0
    assert preview["enclosure"]["height_mm"] == 1600.0
    assert preview["enclosure"]["mounting_plate_included"] is False
    assert preview["cable_entry"] == "BOTTOM"
    assert preview["cable_exit"] == "TOP"
    assert any(item["reference"] == "C25B32TM250" and item["source_status"] == "REFERENCE-CONFLICT" for item in preview["components"])
    assert any(item["reference"] == "PM2100" and item["source_status"] == "FAMILY-ONLY-REFERENCE" for item in preview["components"])
    assert any(item["reference"] == "CT 250/5 A" and item["width_mm"] is None for item in preview["components"])
    assert preview["drawing_mutation_performed"] is False
    assert len(preview["preview_sha256"]) == 64

    dxf = render_dxf(preview)
    assert "$INSUNITS\n70\n4" in dxf
    assert "NOT FOR FABRICATION" in dxf
    assert "PREVIEW SHA256" in dxf
