"""Build an isolated Schneider panel hybrid preview from reviewed STEP assets."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import subprocess
from dataclasses import dataclass
from pathlib import Path

from integrations.build_schneider_panel_ga_3d import (
    build_3d_model,
    omit_solid_layers,
    omit_solids_for_external_geometry,
    render_dxf,
)


_BOUNDS = re.compile(
    r"AIENG_EXTMIN=\(([-+0-9.eE]+) ([-+0-9.eE]+) ([-+0-9.eE]+)\).*?"
    r"AIENG_EXTMAX=\(([-+0-9.eE]+) ([-+0-9.eE]+) ([-+0-9.eE]+)\).*?"
    r"AIENG_INSUNITS=(\d+)",
    re.DOTALL,
)


@dataclass(frozen=True, slots=True)
class Placement:
    instance_id: str
    minimum_mm: tuple[float, float, float]


@dataclass(frozen=True, slots=True)
class AssetPlacement:
    part_number: str
    source_step: Path
    placements: tuple[Placement, ...]


def _hash(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        while chunk := handle.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


def _run_core_console(core: Path, *, script: Path, drawing: Path | None = None) -> str:
    command = [str(core)]
    if drawing is not None:
        command.extend(("/i", str(drawing)))
    command.extend(("/s", str(script), "/l", "en-US"))
    result = subprocess.run(command, capture_output=True, timeout=600, check=False)
    output = (result.stdout + result.stderr).decode("utf-16-le", errors="replace").replace("\x00", "")
    if result.returncode != 0 or any(marker in output.casefold() for marker in ("fatal error", "unhandled exception", "function cancelled")):
        raise RuntimeError(f"AutoCAD Core Console failed for {script.name}: {output[-2000:]}")
    return output


def _quote(path: Path) -> str:
    text = path.resolve().as_posix()
    if '"' in text or "\n" in text or "\r" in text:
        raise ValueError("Unsafe path for AutoCAD script.")
    return f'"{text}"'


def _convert_step(core: Path, source: Path, output: Path, script: Path) -> str:
    script.write_text(
        "\n".join(
            (
                "_.FILEDIA", "0", "_.IMPORT", _quote(source), "_.DELAY", "5000",
                "_.SAVEAS", "2018", _quote(output), "_.QUIT", "",
            )
        ),
        encoding="ascii",
    )
    console = _run_core_console(core, script=script)
    if "import job complete" not in console.casefold() or not output.is_file():
        raise RuntimeError(f"STEP import did not complete for {source.name}.")
    return console


def _inspect_bounds(core: Path, drawing: Path, script: Path) -> tuple[tuple[float, float, float], tuple[float, float, float], int]:
    script.write_text(
        "\n".join(
            (
                "_.ZOOM", "_E",
                '(princ "\\nAIENG_EXTMIN=")(princ (getvar "EXTMIN"))(princ "\\nAIENG_EXTMAX=")(princ (getvar "EXTMAX"))(princ "\\nAIENG_INSUNITS=")(princ (getvar "INSUNITS"))(princ "\\nAIENG_BOUNDS_DONE=1\\n")',
                "_.QUIT", "_N", "",
            )
        ),
        encoding="ascii",
    )
    output = _run_core_console(core, script=script, drawing=drawing)
    match = _BOUNDS.search(output)
    if match is None or "AIENG_BOUNDS_DONE=1" not in output:
        raise RuntimeError(f"Could not read imported bounds for {drawing.name}.")
    values = tuple(float(value) for value in match.groups()[:6])
    return values[:3], values[3:], int(match.group(7))


def _transformed_minimum(source_min: tuple[float, float, float], source_max: tuple[float, float, float], units: int) -> tuple[float, float, float]:
    factor = 25.4 if units == 1 else 1.0
    # Schneider assets arrive X=width, Y=height, Z=depth. Rotate +90 degrees around X.
    return (factor * source_min[0], -factor * source_max[2], factor * source_min[1])


def _assembly_script(
    *,
    assets: tuple[AssetPlacement, ...],
    imported: dict[str, Path],
    bounds: dict[str, tuple[tuple[float, float, float], tuple[float, float, float], int]],
    output: Path,
) -> str:
    lines = ["_.FILEDIA", "0", "_.-LAYER", "_M", "3D-EXACT-STEP", "_C", "4", "", ""]
    for asset in assets:
        source_min, source_max, units = bounds[asset.part_number]
        transformed_min = _transformed_minimum(source_min, source_max, units)
        for placement in asset.placements:
            move_to = tuple(placement.minimum_mm[index] - transformed_min[index] for index in range(3))
            lines.extend(
                (
                    "_.-INSERT", _quote(imported[asset.part_number]), "0,0,0", "1", "", "0",
                    "_.ROTATE3D", "_L", "", "_X", "0,0,0", "90",
                    "_.MOVE", "_L", "", "0,0,0", ",".join(f"{value:.9f}" for value in move_to),
                    "_.CHPROP", "_L", "", "_LA", "3D-EXACT-STEP", "",
                )
            )
    lines.extend(("_.SAVEAS", "2018", _quote(output), "_.QUIT", ""))
    return "\n".join(lines)


def build(root: Path, core: Path, output_dir: Path) -> dict[str, object]:
    root = root.resolve()
    output_dir = output_dir.resolve()
    output_dir.mkdir(parents=True, exist_ok=True)
    batch = root / "workspace" / "manufacturer-assets-batches" / "schneider-step-20260826"
    review = batch / "review"
    accepted = batch / "accepted"
    model = build_3d_model()
    positions = {solid["id"]: tuple(float(solid[key]) for key in ("x_mm", "y_mm", "z_mm")) for solid in model["solids"]}
    replacements = {
        "MPCB1", "MPCB2", "MPCB3", "QF4", "MCB1", "MCB2", "MCB3", "KT1", "KT2",
        "KV1", "KV2", "KM1", "KM2", "KM3", "KM4", "KM5", "KM6", "FR1", "FR2",
        "HG1", "HR1", "HA1", "HG2", "HR2", "HA2", "SS1", "SS2", "SS3",
    }
    base_model = omit_solid_layers(omit_solids_for_external_geometry(model, replacements), {"3D-BUSBAR"})
    base_dxf = output_dir / "schneider-panel-step-hybrid-base-20260830.dxf"
    base_dxf.write_text(render_dxf(base_model), encoding="ascii")

    def placements(*ids: str) -> tuple[Placement, ...]:
        return tuple(Placement(item, positions[item]) for item in ids)

    assets = (
        AssetPlacement("GV2P32", review / "GV2P32" / "GV2P08_3D-simplified.stp", placements("MPCB1", "MPCB2", "MPCB3")),
        AssetPlacement("LC1D25MD", review / "LC1D25MD" / "MCADPP0003729_3D-simplified.stp", placements("KM1", "KM2", "KM4", "KM5")),
        AssetPlacement("LC1D18MD", review / "LC1D18MD" / "MCADPP0003371_3D-simplified.stp", placements("KM3", "KM6")),
        AssetPlacement("LRD21", review / "LRD21" / "MCADPD0004675_3D-simplified.stp", placements("FR1", "FR2")),
        AssetPlacement("RE22R1MYMR", accepted / "RE22R1MYMR" / "RE22R1MYMR.stp", placements("KT1", "KT2")),
        AssetPlacement("RM22TR33", review / "RM22TR33" / "MCADID0001773_3D-CAD.stp", placements("KV1", "KV2")),
        AssetPlacement("A9F77", review / "A9F77316" / "MCADFD0000999_3D-simplified.stp", placements("QF4", "MCB1", "MCB2", "MCB3")),
        AssetPlacement("XB4BVB", review / "XB4BVB3" / "XB4BVB1.stp", placements("HG1", "HR1", "HA1", "HG2", "HR2", "HA2")),
        AssetPlacement("XB4BD33", review / "XB4BD33" / "XB4BD__3DCAD.stp", placements("SS1", "SS2", "SS3")),
        AssetPlacement("LAD91217", review / "LAD91217" / "MCADPD0004355_3D-simplified.stp", (Placement("LA1", (70.0, 25.0, 380.0)), Placement("LA2", (400.0, 25.0, 380.0)))),
        AssetPlacement("LV431541", review / "LV431541" / "MCADPP0000219_3D-simplified.stp", (Placement("MT1", positions["QF1"]),)),
    )
    for asset in assets:
        if not asset.source_step.is_file():
            raise FileNotFoundError(asset.source_step)

    imported: dict[str, Path] = {}
    bounds: dict[str, tuple[tuple[float, float, float], tuple[float, float, float], int]] = {}
    import_evidence: list[dict[str, object]] = []
    for asset in assets:
        safe = re.sub(r"[^A-Za-z0-9_-]", "_", asset.part_number)
        drawing = output_dir / f"asset-{safe}.dwg"
        import_script = output_dir / f"asset-{safe}-import.scr"
        inspect_script = output_dir / f"asset-{safe}-inspect.scr"
        if not drawing.is_file():
            _convert_step(core, asset.source_step, drawing, import_script)
        observed = _inspect_bounds(core, drawing, inspect_script)
        imported[asset.part_number] = drawing
        bounds[asset.part_number] = observed
        import_evidence.append(
            {
                "part_number": asset.part_number,
                "source_step": str(asset.source_step),
                "source_sha256": _hash(asset.source_step),
                "imported_dwg": str(drawing),
                "imported_dwg_sha256": _hash(drawing),
                "source_bounds": [list(observed[0]), list(observed[1])],
                "source_insunits": observed[2],
                "instance_ids": [placement.instance_id for placement in asset.placements],
            }
        )

    output_dwg = output_dir / "schneider-panel-step-hybrid-review-20260830.dwg"
    assembly = output_dir / "schneider-panel-step-hybrid-assemble-20260830.scr"
    assembly.write_text(_assembly_script(assets=assets, imported=imported, bounds=bounds, output=output_dwg), encoding="ascii")
    console = _run_core_console(core, script=assembly, drawing=base_dxf)
    if not output_dwg.is_file():
        raise RuntimeError("Hybrid output DWG was not created.")
    result: dict[str, object] = {
        "schema_version": 1,
        "result": "review-preview-only",
        "base_preview_sha256": base_model["preview_sha256"],
        "output_dwg": str(output_dwg),
        "output_dwg_sha256": _hash(output_dwg),
        "exact_step_instance_count": sum(len(asset.placements) for asset in assets),
        "temporary_envelopes_retained": ["QF1", "QF2", "QF3", "CT1", "CT2", "CT3", "PM1", "XT1", "XT2"],
        "busbars_included": False,
        "existing_project_drawing_mutation_performed": False,
        "imports": import_evidence,
        "console_completed": "fatal error" not in console.casefold(),
        "limitations": [
            "Review preview only; not released for fabrication.",
            "Review-bound family STEP assets are shown under the engineer's explicit visualization approval.",
            "Temporary envelopes remain for missing or unresolved exact CAD.",
            "Busbars are intentionally omitted pending later design.",
        ],
    }
    evidence = output_dir / "schneider-panel-step-hybrid-review-20260830.json"
    evidence.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return result


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--project-root", type=Path, default=Path.cwd())
    parser.add_argument("--core-console", type=Path, required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    arguments = parser.parse_args()
    print(json.dumps(build(arguments.project_root, arguments.core_console, arguments.output_dir), ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
