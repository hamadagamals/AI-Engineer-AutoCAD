"""Tests for the 1:1 Schneider panel 3D envelope model."""

from io import StringIO

import ezdxf

from integrations.build_schneider_panel_ga_3d import (
    build_3d_model,
    omit_solid_layers,
    omit_solids_for_external_geometry,
    render_dxf,
)


def test_3d_model_uses_true_axes_and_excludes_assembly_outline_as_a_solid():
    model = build_3d_model()
    qf1 = next(item for item in model["solids"] if item["id"] == "QF1")
    pm1 = next(item for item in model["solids"] if item["id"] == "PM1")

    assert model["units"] == "mm"
    assert model["scale"] == "1:1"
    assert [qf1["width_mm"], qf1["height_mm"], qf1["depth_mm"]] == [105.0, 161.0, 110.0]
    assert pm1["mounting"] == "DOOR"
    assert pm1["y_mm"] == 327.0
    assert not any(item["reference"].endswith("LAD91217") for item in model["solids"])
    assert model["existing_project_drawing_mutation_performed"] is False

    document = ezdxf.read(StringIO(render_dxf(model)))
    modelspace = document.modelspace()
    assert sum(entity.dxftype() == "3DFACE" for entity in modelspace) >= len(model["solids"]) * 6
    assert tuple(document.viewports.get("*Active")[0].dxf.direction) == (1.0, -1.0, 1.0)
    assert document.header["$INSUNITS"] == 4


def test_exact_step_replacement_removes_only_requested_envelopes():
    base = build_3d_model()
    hybrid = omit_solids_for_external_geometry(base, {"KT1", "KT2"})

    assert not {"KT1", "KT2"} & {solid["id"] for solid in hybrid["solids"]}
    assert len(hybrid["solids"]) == len(base["solids"]) - 2
    assert hybrid["external_geometry_replacements"] == ["KT1", "KT2"]
    assert hybrid["preview_sha256"] != base["preview_sha256"]


def test_optional_solid_layer_omission_is_exact_and_hash_bound():
    base = build_3d_model()
    without_busbars = omit_solid_layers(base, {"3D-BUSBAR"})

    assert not any(solid["layer"] == "3D-BUSBAR" for solid in without_busbars["solids"])
    assert without_busbars["omitted_solid_layers"] == ["3D-BUSBAR"]
    assert without_busbars["preview_sha256"] != base["preview_sha256"]
