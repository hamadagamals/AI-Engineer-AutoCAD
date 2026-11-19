"""Create a 1:1 3D envelope model for the preliminary Schneider panel GA."""

from __future__ import annotations

import argparse
from copy import deepcopy
from io import StringIO
import json
from pathlib import Path
from typing import Any

import ezdxf
from ezdxf import units
from PIL import Image, ImageDraw, ImageFont

from integrations.build_schneider_panel_ga_preview import _canonical, _digest, build_preview


LAYER_COLOURS = {
    "3D-ENCLOSURE": 7,
    "3D-EQUIPMENT": 5,
    "3D-TBD": 1,
    "3D-DOOR": 3,
    "3D-DUCT": 8,
    "3D-BUSBAR": 2,
    "3D-TEXT": 7,
}


def _id(reference: str) -> str:
    exact = {
        "C25B3TM250? + LV431541": "QF1",
        "C25B3TM160": "QF2",
        "LV510305": "QF3",
        "A9F77306": "QF4",
        "PM2100 FAMILY": "PM1",
    }
    if reference in exact:
        return exact[reference]
    if reference.startswith("CT"):
        return reference.split()[0]
    prefixes = {
        "GV2P32-": "MPCB",
        "A9F77316-": "MCB",
        "RE22R1MYMR-": "KT",
        "RM22TR33-": "KV",
        "NSYTRV42GAT-": "XT",
        "XB4BVB3-GREEN-": "HG",
        "XB4BVB4-RED-": "HR",
        "XB4BVB5-AMBER-": "HA",
        "XB4BD33-": "SS",
    }
    for prefix, result in prefixes.items():
        if reference.startswith(prefix):
            return result + reference.rsplit("-", 1)[-1].replace("A", "")
    starter_map = {
        "SD1-LC1D25-LINE": "KM1",
        "SD1-LC1D25-DELTA": "KM2",
        "SD1-LC1D18-STAR": "KM3",
        "SD1-LRD21": "FR1",
        "SD2-LC1D25-LINE": "KM4",
        "SD2-LC1D25-DELTA": "KM5",
        "SD2-LC1D18-STAR": "KM6",
        "SD2-LRD21": "FR2",
    }
    return starter_map.get(reference, reference[:12])


def _depth(reference: str) -> float:
    if reference == "C25B3TM250? + LV431541": return 110.0
    if reference == "C25B3TM160": return 86.0
    if reference == "LV510305": return 86.0
    if reference.startswith("CT"): return 50.0
    if reference.startswith("GV2P32"): return 97.0
    if reference.startswith("A9F"): return 78.5
    if reference.startswith("RE22"): return 79.5
    if reference.startswith("RM22"): return 73.0
    if reference.startswith("NSYTRV"): return 47.7
    if "LC1D25" in reference: return 101.0
    if "LC1D18" in reference: return 95.0
    if "LRD21" in reference: return 70.0
    if reference == "PM2100 FAMILY": return 73.0
    if reference.startswith("XB4BVB"): return 54.0
    if reference.startswith("XB4BD"): return 68.0
    return 40.0


def build_3d_model() -> dict[str, Any]:
    base = build_preview()
    solids: list[dict[str, Any]] = []
    legend: list[dict[str, Any]] = []
    for item in base["layout_items"]:
        if item["status"] == "ASSEMBLY":
            continue
        depth = _depth(item["reference"])
        is_door = item["view"] == "DOOR"
        local_x = float(item["x_mm"]) - (1000.0 if is_door else 0.0)
        y = 400.0 - depth if is_door else 25.0
        solid = {
            "id": _id(item["reference"]),
            "reference": item["reference"],
            "x_mm": local_x,
            "y_mm": y,
            "z_mm": float(item["y_mm"]),
            "width_mm": float(item["width_mm"]),
            "depth_mm": depth,
            "height_mm": float(item["height_mm"]),
            "mounting": "DOOR" if is_door else "BACKPLATE",
            "status": item["status"],
            "layer": "3D-TBD" if item["status"] in {"VERIFY", "TBD"} else ("3D-DOOR" if is_door else "3D-EQUIPMENT"),
        }
        solids.append(solid)
        legend.append({"id": solid["id"], "reference": solid["reference"], "dimensions_mm": [solid["width_mm"], solid["height_mm"], solid["depth_mm"]], "status": solid["status"]})

    # Physical wiring ducts, proposed mounting plane and proposed busbars.
    solids.extend(
        [
            {"id": "WD1", "reference": "WIRING DUCT LEFT", "x_mm": 50.0, "y_mm": 20.0, "z_mm": 240.0, "width_mm": 40.0, "depth_mm": 60.0, "height_mm": 1120.0, "mounting": "BACKPLATE", "status": "PROPOSED", "layer": "3D-DUCT"},
            {"id": "WD2", "reference": "WIRING DUCT RIGHT", "x_mm": 710.0, "y_mm": 20.0, "z_mm": 240.0, "width_mm": 40.0, "depth_mm": 60.0, "height_mm": 1120.0, "mounting": "BACKPLATE", "status": "PROPOSED", "layer": "3D-DUCT"},
        ]
    )
    for index, phase in enumerate(("R", "Y", "B", "N")):
        solids.append({"id": f"BB-{phase}", "reference": f"PROPOSED {phase} BUSBAR 40x5", "x_mm": 110.0, "y_mm": 18.0, "z_mm": 1210.0 + index * 50.0, "width_mm": 580.0, "depth_mm": 5.0, "height_mm": 40.0, "mounting": "BACKPLATE", "status": "TBD", "layer": "3D-BUSBAR"})

    model: dict[str, Any] = {
        "schema_version": 1,
        "model_type": "schneider-panel-general-arrangement-3d-envelope-preview",
        "system": base["system"],
        "units": "mm",
        "scale": "1:1",
        "enclosure": {"width_mm": 800.0, "depth_mm": 400.0, "height_mm": 1600.0, "reference": "NSYSM16840"},
        "solids": solids,
        "legend": legend,
        "limitations": [
            "Dimensional envelopes are shown, not detailed manufacturer casing geometry.",
            "Red solids are unresolved or reference-conflicted and are not fabrication-approved.",
            "The enclosure mounting plate is not supplied and remains a required selection.",
            "Thermal, clearance/creepage, busbar fault-duty and cable-bending verification remain open.",
        ],
        "source_2d_preview_sha256": base["preview_sha256"],
        "existing_project_drawing_mutation_performed": False,
    }
    model["preview_sha256"] = _digest(model)
    return model


def omit_solids_for_external_geometry(
    model: dict[str, Any], solid_ids: set[str]
) -> dict[str, Any]:
    """Return a hash-bound base model with selected envelopes reserved for exact CAD."""
    output = deepcopy(model)
    existing_ids = {solid["id"] for solid in output["solids"]}
    missing = solid_ids - existing_ids
    if missing:
        raise ValueError(f"Unknown solid IDs requested for external replacement: {sorted(missing)}")
    output["solids"] = [solid for solid in output["solids"] if solid["id"] not in solid_ids]
    output["external_geometry_replacements"] = sorted(solid_ids)
    output.pop("preview_sha256", None)
    output["preview_sha256"] = _digest(output)
    return output


def omit_solid_layers(model: dict[str, Any], layers: set[str]) -> dict[str, Any]:
    """Return a hash-bound model without optional preliminary solid layers."""
    output = deepcopy(model)
    known_layers = {solid["layer"] for solid in output["solids"]}
    missing = layers - known_layers
    if missing:
        raise ValueError(f"Unknown solid layers requested for omission: {sorted(missing)}")
    output["solids"] = [solid for solid in output["solids"] if solid["layer"] not in layers]
    output["omitted_solid_layers"] = sorted(layers)
    output.pop("preview_sha256", None)
    output["preview_sha256"] = _digest(output)
    return output


def render_dxf(model: dict[str, Any]) -> str:
    document = ezdxf.new("R2010", setup=True)
    document.units = units.MM
    document.header["$MEASUREMENT"] = 1
    active_viewport = document.viewports.get("*Active")[0]
    active_viewport.dxf.direction = (1.0, -1.0, 1.0)
    active_viewport.dxf.target = (400.0, 200.0, 800.0)
    active_viewport.dxf.height = 2200.0
    active_viewport.dxf.render_mode = 6
    for name, colour in LAYER_COLOURS.items():
        if name not in document.layers:
            document.layers.add(name, color=colour)
    modelspace = document.modelspace()

    def line(a: tuple[float, float, float], b: tuple[float, float, float], layer: str) -> None:
        modelspace.add_line(a, b, dxfattribs={"layer": layer})

    def face(points: list[tuple[float, float, float]], layer: str) -> None:
        modelspace.add_3dface(points, dxfattribs={"layer": layer})

    def box(x: float, y: float, z: float, w: float, d: float, h: float, layer: str) -> None:
        p000, p100, p110, p010 = (x,y,z), (x+w,y,z), (x+w,y+d,z), (x,y+d,z)
        p001, p101, p111, p011 = (x,y,z+h), (x+w,y,z+h), (x+w,y+d,z+h), (x,y+d,z+h)
        for pts in ([p000,p100,p110,p010], [p001,p011,p111,p101], [p000,p001,p101,p100], [p100,p101,p111,p110], [p110,p111,p011,p010], [p010,p011,p001,p000]):
            face(list(pts), layer)

    def wire_box(x: float, y: float, z: float, w: float, d: float, h: float, layer: str) -> None:
        lower = [(x,y,z),(x+w,y,z),(x+w,y+d,z),(x,y+d,z)]
        upper = [(a,b,c+h) for a,b,c in lower]
        for loop in (lower, upper):
            for i in range(4): line(loop[i], loop[(i+1)%4], layer)
        for i in range(4): line(lower[i], upper[i], layer)

    def text(x: float, y: float, z: float, height: float, value: str, layer: str = "3D-TEXT") -> None:
        modelspace.add_text(
            value[:200],
            dxfattribs={"layer": layer, "height": height, "insert": (x, y, z)},
        )

    wire_box(0, 0, 0, 800, 400, 1600, "3D-ENCLOSURE")
    # Proposed but absent mounting plate plane.
    wire_box(40, 12, 80, 720, 3, 1440, "3D-TBD")
    for solid in model["solids"]:
        box(solid["x_mm"], solid["y_mm"], solid["z_mm"], solid["width_mm"], solid["depth_mm"], solid["height_mm"], solid["layer"])
        text(solid["x_mm"] + 2, solid["y_mm"] + solid["depth_mm"] + 3, solid["z_mm"] + solid["height_mm"] / 2, 10, solid["id"], solid["layer"])
    text(0, 430, 1660, 30, "SCHNEIDER PANEL 3D ENVELOPE PREVIEW - 1:1 mm")
    text(0, 430, 1615, 18, "NSYSM16840 800W x 400D x 1600H - BOTTOM ENTRY / TOP EXIT")
    for index, item in enumerate(model["legend"]):
        dims = "x".join(f"{value:g}" for value in item["dimensions_mm"])
        text(900, 0, 1550 - index * 35, 12, f"{item['id']}  {item['reference']}  {dims} mm  {item['status']}")
    text(900, 0, 200, 14, "RED/TBD ITEMS REQUIRE EXACT REFERENCE OR DIMENSIONS")
    text(900, 0, 160, 14, "NOT FOR FABRICATION - THERMAL / CLEARANCE / FAULT DUTY OPEN")
    text(900, 0, 120, 10, f"PREVIEW SHA256 {model['preview_sha256']}")

    stream = StringIO()
    document.write(stream)
    return stream.getvalue()


def render_png(model: dict[str, Any], output: Path) -> None:
    image = Image.new("RGB", (1900, 1100), "#f7f8fa")
    draw = ImageDraw.Draw(image)
    font = ImageFont.truetype("C:/Windows/Fonts/arial.ttf", 13) if Path("C:/Windows/Fonts/arial.ttf").exists() else ImageFont.load_default()
    bold = ImageFont.truetype("C:/Windows/Fonts/arialbd.ttf", 24) if Path("C:/Windows/Fonts/arialbd.ttf").exists() else font
    origin = (300, 980)

    def proj(x: float, y: float, z: float) -> tuple[float, float]:
        return (origin[0] + x * 0.62 + y * 0.34, origin[1] - z * 0.53 + y * 0.16)

    def draw_box(s: dict[str, Any], fill: str, outline: str) -> None:
        x,y,z,w,d,h = (float(s[k]) for k in ("x_mm","y_mm","z_mm","width_mm","depth_mm","height_mm"))
        p000,p100,p110,p010 = proj(x,y,z),proj(x+w,y,z),proj(x+w,y+d,z),proj(x,y+d,z)
        p001,p101,p111,p011 = proj(x,y,z+h),proj(x+w,y,z+h),proj(x+w,y+d,z+h),proj(x,y+d,z+h)
        draw.polygon((p001,p101,p111,p011), fill=fill, outline=outline)
        draw.polygon((p100,p110,p111,p101), fill=fill, outline=outline)
        draw.polygon((p000,p100,p101,p001), fill=fill, outline=outline)
        draw.text(((p001[0]+p111[0])/2,(p001[1]+p111[1])/2), s["id"], font=font, fill=outline, anchor="mm")

    draw.text((950, 25), "3D PANEL GENERAL ARRANGEMENT - DIMENSIONAL ENVELOPES", font=bold, fill="#0f2854", anchor="mm")
    # Enclosure wireframe.
    enclosure = {"x_mm":0,"y_mm":0,"z_mm":0,"width_mm":800,"depth_mm":400,"height_mm":1600}
    x,y,z,w,d,h = (float(enclosure[k]) for k in ("x_mm","y_mm","z_mm","width_mm","depth_mm","height_mm"))
    lower=[proj(x,y,z),proj(x+w,y,z),proj(x+w,y+d,z),proj(x,y+d,z)]; upper=[proj(a,b,h) for a,b in ((x,y),(x+w,y),(x+w,y+d),(x,y+d))]
    for loop in (lower,upper):
        draw.line(loop+[loop[0]], fill="#0f2854", width=3)
    for a,b in zip(lower,upper): draw.line((a,b), fill="#0f2854", width=3)
    for solid in sorted(model["solids"], key=lambda s: (s["y_mm"], s["z_mm"])):
        if solid["layer"] == "3D-TBD": colours=("#ffd2d2","#c40000")
        elif solid["layer"] == "3D-DOOR": colours=("#d8f5df","#117a37")
        elif solid["layer"] == "3D-BUSBAR": colours=("#ffe4a6","#9a6200")
        elif solid["layer"] == "3D-DUCT": colours=("#e0e3e8","#687386")
        else: colours=("#cfe8ff","#145b9e")
        draw_box(solid, *colours)
    draw.text((1050, 90), "ID / REFERENCE / W x H x D mm", font=bold, fill="#0f2854")
    column_rows = (len(model["legend"]) + 1) // 2
    for index, item in enumerate(model["legend"]):
        column = index // column_rows
        row = index % column_rows
        x_text = 1050 + column * 430
        y_text = 135 + row * 32
        dims=" x ".join(f"{v:g}" for v in item["dimensions_mm"])
        colour="#c40000" if item["status"] in {"VERIFY","TBD"} else "#222"
        draw.text((x_text,y_text), f"{item['id']:5}  {item['reference']}", font=font, fill=colour)
        draw.text((x_text,y_text + 15), f"       {dims} mm  {item['status']}", font=font, fill=colour)
    draw.text((1050, 950), "Envelope geometry only; detailed casings, mounting holes and terminals require official CAD assets.", font=font, fill="#c40000")
    draw.text((1050, 980), "Red = exact reference/dimensions unresolved. Not for fabrication.", font=font, fill="#c40000")
    draw.text((1050, 1010), f"Preview SHA-256: {model['preview_sha256']}", font=font, fill="#117a37")
    output.parent.mkdir(parents=True, exist_ok=True)
    image.save(output)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output-json", type=Path, required=True)
    parser.add_argument("--output-dxf", type=Path, required=True)
    parser.add_argument("--output-png", type=Path, required=True)
    parser.add_argument("--omit-solid-id", action="append", default=[])
    parser.add_argument("--omit-layer", action="append", default=[])
    args = parser.parse_args()
    model = build_3d_model()
    if args.omit_solid_id:
        model = omit_solids_for_external_geometry(model, set(args.omit_solid_id))
    if args.omit_layer:
        model = omit_solid_layers(model, set(args.omit_layer))
    for path in (args.output_json,args.output_dxf,args.output_png): path.parent.mkdir(parents=True, exist_ok=True)
    args.output_json.write_bytes(_canonical(model)+b"\n")
    args.output_dxf.write_text(render_dxf(model), encoding="ascii")
    render_png(model,args.output_png)
    print(json.dumps({"preview_sha256":model["preview_sha256"],"solid_count":len(model["solids"]),"existing_project_drawing_mutation_performed":False}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
