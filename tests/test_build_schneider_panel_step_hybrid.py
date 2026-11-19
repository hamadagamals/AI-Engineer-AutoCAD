"""Regression tests for the reviewed STEP-to-panel hybrid assembly."""

from pathlib import Path

from integrations.build_schneider_panel_step_hybrid import (
    AssetPlacement,
    Placement,
    _assembly_script,
    _transformed_minimum,
)


def test_schneider_inch_geometry_maps_to_panel_axes_in_millimetres():
    result = _transformed_minimum((-1.0, -2.0, -3.0), (4.0, 5.0, 6.0), 1)

    assert result == (-25.4, -152.39999999999998, -50.8)


def test_assembly_script_uses_autocad_xy_scale_prompt_sequence(tmp_path: Path):
    asset = AssetPlacement(
        "TEST",
        tmp_path / "test.stp",
        (Placement("X1", (10.0, 20.0, 30.0)),),
    )
    imported = tmp_path / "asset.dwg"
    output = tmp_path / "hybrid.dwg"

    script = _assembly_script(
        assets=(asset,),
        imported={"TEST": imported},
        bounds={"TEST": ((0.0, 0.0, 0.0), (1.0, 2.0, 3.0), 1)},
        output=output,
    )

    assert script.count("_.-INSERT") == 1
    assert f'"{imported.resolve().as_posix()}"\n0,0,0\n1\n\n0\n_.ROTATE3D' in script
    assert "_.ROTATE3D\n_L\n\n_X\n0,0,0\n90" in script
    assert "_.CHPROP\n_L\n\n_LA\n3D-EXACT-STEP" in script
