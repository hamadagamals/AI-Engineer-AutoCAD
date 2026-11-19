"""Build a deterministic 1:1 preliminary Schneider panel GA as PNG/SVG/DXF."""

from __future__ import annotations

import argparse
import hashlib
import html
import json
from pathlib import Path
from typing import Any

from PIL import Image, ImageDraw, ImageFont


ENCLOSURE = {"reference": "NSYSM16840", "width_mm": 800.0, "height_mm": 1600.0, "depth_mm": 400.0}
SYSTEM = "380/220 V, 3 Phase, 4 Wire"
SOURCES = {
    "enclosure": "https://eshop.se.com/eg/panelset-sm-compact-enclosure-without-mounting-plate-1600x800x400-mm.html",
    "iec_61439_1": "https://webstore.iec.ch/en/publication/32338",
    "iec_61439_2": "https://webstore.iec.ch/en/publication/30043",
    "iec_tr_60890": "https://webstore.iec.ch/en/publication/66007",
}


def _canonical(value: Any) -> bytes:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")


def _digest(value: Any) -> str:
    return hashlib.sha256(_canonical(value)).hexdigest()


def _font(size: int, *, bold: bool = False) -> ImageFont.FreeTypeFont | ImageFont.ImageFont:
    path = Path("C:/Windows/Fonts") / ("arialbd.ttf" if bold else "arial.ttf")
    return ImageFont.truetype(str(path), size) if path.exists() else ImageFont.load_default()


def _component(
    reference: str,
    quantity: int,
    width: float | None,
    height: float | None,
    depth: float | None,
    source_status: str,
    note: str = "",
) -> dict[str, Any]:
    return {
        "reference": reference,
        "quantity": quantity,
        "width_mm": width,
        "height_mm": height,
        "depth_mm": depth,
        "source_status": source_status,
        "note": note,
    }


COMPONENTS = [
    _component("GV2P32", 3, 45, 89, 97, "OFFICIAL-PRODUCT-DATA"),
    _component("LC1D25MD", 4, 45, 85, 101, "OFFICIAL-PRODUCT-DATA", "Duplicate BOM rows consolidated: 2 + 2."),
    _component("LC1D18MD", 2, 45, 77, 95, "OFFICIAL-PRODUCT-DATA"),
    _component("LRD21", 2, 45, 66, 70, "OFFICIAL-PRODUCT-DATA"),
    _component("LAD91217", 2, None, None, None, "ASSEMBLY-ACCESSORY", "Represented inside each star-delta starter; exact envelope TBD."),
    _component("RE22R1MYMR", 2, 22.5, 90, 79.5, "OFFICIAL-FAMILY-DIMENSION"),
    _component("RM22TR33", 2, 22.5, 90, 73, "OFFICIAL-PRODUCT-DATA"),
    _component("PM2100", 1, 96, 96, 73, "FAMILY-ONLY-REFERENCE", "Exact METSEPM21xx reference and cut-out must be confirmed."),
    _component("CT 250/5 A", 3, None, None, None, "GENERIC-NO-REFERENCE", "Preview envelope only; window and overall dimensions TBD."),
    _component("A9F77306", 1, 54, 85, 78.5, "OFFICIAL-FAMILY-DIMENSION"),
    _component("NSYTRV42GAT", 2, 6.2, 47.5, 47.7, "REFERENCE-SUFFIX-UNVERIFIED", "Base NSYTRV42 family envelope used; exact GAT suffix TBD."),
    _component("C25B32TM250", 1, 105, 161, 86, "REFERENCE-CONFLICT", "Likely C25B3TM250; confirmation required."),
    _component("LV431541 + NSX accessories", 1, 105, 161, 110, "PARTIAL-BUNDLE", "Motor mechanism included; BSC/COM/cord/MX-MN exact refs TBD."),
    _component("C25B3TM160", 1, 105, 161, 86, "OFFICIAL-FAMILY-DIMENSION"),
    _component("LV510305", 1, 105, 161, 86, "OFFICIAL-PRODUCT-DATA"),
    _component("A9F77316", 3, 54, 85, 78.5, "OFFICIAL-FAMILY-DIMENSION"),
    _component("XB4BVB3", 2, 30, 47, 54, "OFFICIAL-FAMILY-DIMENSION", "Door cut-out Ø22.5 mm."),
    _component("XB4BVB4", 2, 30, 47, 54, "OFFICIAL-FAMILY-DIMENSION", "Door cut-out Ø22.5 mm."),
    _component("XB4BVB5", 2, 30, 47, 54, "OFFICIAL-FAMILY-DIMENSION", "Door cut-out Ø22.5 mm."),
    _component("XB4BD33", 3, 30, 47, 68, "OFFICIAL-PRODUCT-DATA", "Door cut-out Ø22.5 mm."),
    _component("Busbar 5x40 mm", 1, None, None, None, "INCOMPLETE-SPECIFICATION", "Material, phase count, length, supports, fault duty and PE/N arrangement TBD."),
]


def _item(reference: str, x: float, y: float, w: float, h: float, view: str, status: str = "CONFIRMED", note: str = "") -> dict[str, Any]:
    return {"reference": reference, "x_mm": x, "y_mm": y, "width_mm": w, "height_mm": h, "view": view, "status": status, "note": note}


def build_preview() -> dict[str, Any]:
    """Return the fixed preliminary layout; all coordinates are real millimetres."""
    items: list[dict[str, Any]] = []
    # Internal fixed-device row.
    items.extend(
        [
            _item("C25B3TM250? + LV431541", 80, 1100, 105, 161, "INTERNAL", "VERIFY", "Requested reference C25B32TM250 conflicts with official syntax."),
            _item("C25B3TM160", 270, 1100, 105, 161, "INTERNAL"),
            _item("LV510305", 460, 1100, 105, 161, "INTERNAL"),
        ]
    )
    # Generic CT envelopes intentionally oversized and marked TBD.
    for index, x in enumerate((590, 650, 710), 1):
        items.append(_item(f"CT{index} 250/5A", x, 1125, 50, 90, "INTERNAL", "TBD", "Exact CT reference/dimensions required."))
    # DIN rail motor protectors.
    for index, x in enumerate((85, 165, 245), 1):
        items.append(_item(f"GV2P32-{index}", x, 900, 45, 89, "INTERNAL"))
    # Protection/control DIN rail.
    items.append(_item("A9F77306", 380, 900, 54, 85, "INTERNAL"))
    for index, x in enumerate((455, 529, 603), 1):
        items.append(_item(f"A9F77316-{index}", x, 900, 54, 85, "INTERNAL"))
    for index, x in enumerate((85, 135), 1):
        items.append(_item(f"RE22R1MYMR-{index}", x, 760, 22.5, 90, "INTERNAL"))
    for index, x in enumerate((200, 250), 1):
        items.append(_item(f"RM22TR33-{index}", x, 760, 22.5, 90, "INTERNAL"))
    for index, x in enumerate((650, 670), 1):
        items.append(_item(f"NSYTRV42GAT-{index}", x, 1420, 6.2, 47.5, "INTERNAL", "VERIFY", "Exact suffix unresolved."))
    # Two star-delta starter groups: two D25 + one D18 and attached LRD each.
    for starter, base_x in ((1, 85), (2, 415)):
        items.append(_item(f"SD{starter}-LC1D25-LINE", base_x, 490, 45, 85, "INTERNAL"))
        items.append(_item(f"SD{starter}-LC1D25-DELTA", base_x + 75, 490, 45, 85, "INTERNAL"))
        items.append(_item(f"SD{starter}-LC1D18-STAR", base_x + 150, 490, 45, 77, "INTERNAL"))
        items.append(_item(f"SD{starter}-LRD21", base_x, 400, 45, 66, "INTERNAL"))
        items.append(_item(f"SD{starter}-LAD91217", base_x - 15, 380, 225, 220, "INTERNAL", "ASSEMBLY", "Accessory kit represented by group envelope."))
    # Door mounted meter, pilot lights, selectors.
    items.append(_item("PM2100 FAMILY", 1352, 1240, 96, 96, "DOOR", "VERIFY", "Exact METSEPM21xx and cut-out TBD."))
    colours = (("XB4BVB3-GREEN", 3), ("XB4BVB4-RED", 4), ("XB4BVB5-AMBER", 5))
    for row, y in enumerate((1010, 900)):
        for column, (name, _) in enumerate(colours):
            items.append(_item(f"{name}-{row + 1}", 1190 + column * 205, y, 30, 47, "DOOR"))
    for index, x in enumerate((1190, 1395, 1600), 1):
        items.append(_item(f"XB4BD33-{index}", x, 700, 30, 47, "DOOR"))

    preview: dict[str, Any] = {
        "schema_version": 1,
        "preview_type": "schneider-panel-general-arrangement-preliminary",
        "system": SYSTEM,
        "cable_entry": "BOTTOM",
        "cable_exit": "TOP",
        "units": "mm",
        "scale": "1:1 model space",
        "enclosure": {**ENCLOSURE, "mounting_plate_included": False, "ip_rating": "IP55", "standard": "IEC 62208"},
        "components": COMPONENTS,
        "layout_items": items,
        "zones": {
            "bottom_cable_entry": [50, 50, 700, 170],
            "top_cable_exit": [50, 1400, 700, 150],
            "side_wiring_ducts": [[50, 240, 40, 1120], [710, 240, 40, 1120]],
            "thermal_reserve": [[100, 250, 240, 100], [460, 250, 240, 100]],
        },
        "design_notes": [
            "Preliminary GA only; not fabrication or construction release.",
            "A compatible mounting plate is required because NSYSM16840 is supplied without one.",
            "No ventilation opening is approved; reserved zones require IEC 61439 temperature-rise verification while preserving IP55.",
            "Cable bending radii, gland sizes, conductor sizes and short-circuit withstand are TBD.",
            "Busbar 5x40 mm is not finalized without material, phase/neutral/PE arrangement, length, supports and fault-duty inputs.",
            "Clearance/creepage and temperature-rise verification remain assembly-level IEC 61439 work, not inferred from component rectangles.",
        ],
        "standards_basis": ["IEC 61439-1:2020", "IEC 61439-2:2020", "IEC TR 60890:2022", "IEC 62208 (enclosure)"],
        "sources": SOURCES,
        "drawing_mutation_performed": False,
    }
    preview["preview_sha256"] = _digest(preview)
    return preview


def _screen(x: float, y: float, *, view_offset: float, scale: float, origin: tuple[int, int]) -> tuple[int, int]:
    return (round(origin[0] + (x - view_offset) * scale), round(origin[1] - y * scale))


def render_png(preview: dict[str, Any], output: Path) -> None:
    image = Image.new("RGB", (1900, 1050), "white")
    draw = ImageDraw.Draw(image)
    scale, origin = 0.48, (100, 900)
    navy, blue, red, amber, gray, green = "#0f2854", "#2266aa", "#d00000", "#b45309", "#687386", "#169447"
    draw.text((950, 25), "PRELIMINARY PANEL GENERAL ARRANGEMENT", font=_font(26, bold=True), fill=navy, anchor="mm")
    draw.text((950, 57), f"{preview['system']} | BOTTOM ENTRY | TOP EXIT | MODEL 1:1 mm", font=_font(14), fill=gray, anchor="mm")
    for offset, title in ((0, "INTERNAL ARRANGEMENT"), (1000, "DOOR LAYOUT")):
        x1, y1 = _screen(offset, 1600, view_offset=offset, scale=scale, origin=(100 if offset == 0 else 1030, 900))
        x2, y2 = _screen(offset + 800, 0, view_offset=offset, scale=scale, origin=(100 if offset == 0 else 1030, 900))
        draw.rectangle((x1, y1, x2, y2), outline=navy, width=4)
        draw.text(((x1 + x2) / 2, y1 - 25), title, font=_font(16, bold=True), fill=navy, anchor="mm")
    internal_origin, door_origin = (100, 900), (1030, 900)
    # Reserved cable zones and wiring ducts.
    for rect, label in (((50, 50, 700, 170), "BOTTOM CABLE ENTRY"), ((50, 1400, 700, 150), "TOP CABLE EXIT")):
        x, y, w, h = rect
        p1 = _screen(x, y + h, view_offset=0, scale=scale, origin=internal_origin)
        p2 = _screen(x + w, y, view_offset=0, scale=scale, origin=internal_origin)
        draw.rectangle((*p1, *p2), outline=amber, width=2)
        draw.text(((p1[0] + p2[0]) / 2, (p1[1] + p2[1]) / 2), label, font=_font(10, bold=True), fill=amber, anchor="mm")
    for x in (50, 710):
        p1 = _screen(x, 1360, view_offset=0, scale=scale, origin=internal_origin)
        p2 = _screen(x + 40, 240, view_offset=0, scale=scale, origin=internal_origin)
        draw.rectangle((*p1, *p2), outline=gray, width=2)
    # Proposed busbars.
    for idx, (name, color) in enumerate((("R", "#d11"), ("Y", "#c9a500"), ("B", "#174ea6"), ("N", "#222"))):
        y = 1335 - idx * 15
        p1 = _screen(110, y, view_offset=0, scale=scale, origin=internal_origin)
        p2 = _screen(690, y, view_offset=0, scale=scale, origin=internal_origin)
        draw.line((*p1, *p2), fill=color, width=3)
        draw.text((p1[0] - 12, p1[1]), name, font=_font(9, bold=True), fill=color, anchor="rm")
    for item in preview["layout_items"]:
        is_door = item["view"] == "DOOR"
        offset, item_origin = (1000, door_origin) if is_door else (0, internal_origin)
        p1 = _screen(item["x_mm"], item["y_mm"] + item["height_mm"], view_offset=offset, scale=scale, origin=item_origin)
        p2 = _screen(item["x_mm"] + item["width_mm"], item["y_mm"], view_offset=offset, scale=scale, origin=item_origin)
        status = item["status"]
        colour = red if status in {"VERIFY", "TBD"} else (amber if status == "ASSEMBLY" else blue)
        if status == "ASSEMBLY":
            draw.rectangle((*p1, *p2), outline=colour, width=2)
        elif is_door and item["reference"].startswith("XB4"):
            cx, cy = (p1[0] + p2[0]) / 2, (p1[1] + p2[1]) / 2
            draw.ellipse((cx - 7, cy - 7, cx + 7, cy + 7), outline=colour, width=2)
        else:
            draw.rectangle((*p1, *p2), outline=colour, width=2)
        label = item["reference"] + ("\n" + status if status != "CONFIRMED" else "")
        draw.multiline_text(((p1[0] + p2[0]) / 2, (p1[1] + p2[1]) / 2), label, font=_font(7, bold=True), fill=colour, anchor="mm", align="center", spacing=1)
    draw.text((100, 940), "Red = VERIFY/TBD | Orange = assembly envelope | Blue = source-sized front envelope", font=_font(12), fill=gray)
    draw.text((100, 970), "Mounting plate, thermal verification, busbar/fault duty, cable bending and exact CT/meter references remain open.", font=_font(12, bold=True), fill=red)
    draw.text((100, 1000), f"Preview SHA-256: {preview['preview_sha256']}", font=_font(9), fill=green)
    output.parent.mkdir(parents=True, exist_ok=True)
    image.save(output)


def render_svg(preview: dict[str, Any]) -> str:
    # Embed the same information in a compact vector sheet; all dimensions are labelled in mm.
    parts = ['<svg xmlns="http://www.w3.org/2000/svg" width="1900" height="1050" viewBox="0 0 1900 1050">', '<rect width="100%" height="100%" fill="white"/>']
    parts.append('<text x="950" y="35" text-anchor="middle" font-family="Arial" font-size="26" font-weight="bold" fill="#0f2854">PRELIMINARY PANEL GENERAL ARRANGEMENT</text>')
    parts.append(f'<text x="950" y="62" text-anchor="middle" font-family="Arial" font-size="14">{html.escape(preview["system"])} | BOTTOM ENTRY | TOP EXIT | MODEL 1:1 mm</text>')
    # The PNG is the reviewed presentation; SVG retains a machine-readable summary and linkable identity.
    y = 110
    for item in preview["components"]:
        dims = "TBD" if item["width_mm"] is None else f'{item["width_mm"]}x{item["height_mm"]}x{item["depth_mm"]} mm'
        parts.append(f'<text x="80" y="{y}" font-family="Arial" font-size="14">{html.escape(item["reference"])} x{item["quantity"]} — {html.escape(dims)} — {html.escape(item["source_status"])}</text>')
        y += 22
    parts.append(f'<text x="80" y="1010" font-family="Arial" font-size="11">Preview SHA-256: {preview["preview_sha256"]}</text></svg>')
    return "".join(parts)


def _dxf_pair(code: int, value: str | int | float) -> str:
    return f"{code}\n{value}\n"


def render_dxf(preview: dict[str, Any]) -> str:
    """Create an ASCII DXF R12 with 1:1 millimetre model-space geometry."""
    entities: list[str] = []

    def line(x1: float, y1: float, x2: float, y2: float, layer: str = "GA-OUTLINE") -> None:
        entities.append("0\nLINE\n" + _dxf_pair(8, layer) + _dxf_pair(10, x1) + _dxf_pair(20, y1) + _dxf_pair(30, 0) + _dxf_pair(11, x2) + _dxf_pair(21, y2) + _dxf_pair(31, 0))

    def rect(x: float, y: float, w: float, h: float, layer: str) -> None:
        line(x, y, x + w, y, layer); line(x + w, y, x + w, y + h, layer); line(x + w, y + h, x, y + h, layer); line(x, y + h, x, y, layer)

    def text(x: float, y: float, height: float, value: str, layer: str = "GA-TEXT") -> None:
        safe = value.replace("\n", " / ")[:240]
        entities.append("0\nTEXT\n" + _dxf_pair(8, layer) + _dxf_pair(10, x) + _dxf_pair(20, y) + _dxf_pair(30, 0) + _dxf_pair(40, height) + _dxf_pair(1, safe))

    rect(0, 0, 800, 1600, "GA-ENCLOSURE")
    rect(1000, 0, 800, 1600, "GA-ENCLOSURE")
    text(250, 1630, 28, "INTERNAL ARRANGEMENT")
    text(1250, 1630, 28, "DOOR LAYOUT")
    text(0, 1710, 32, "PRELIMINARY PANEL GENERAL ARRANGEMENT - 380/220 V - ENTRY BOTTOM / EXIT TOP")
    rect(50, 50, 700, 170, "GA-ZONE-TBD")
    rect(50, 1400, 700, 150, "GA-ZONE-TBD")
    rect(50, 240, 40, 1120, "GA-DUCT")
    rect(710, 240, 40, 1120, "GA-DUCT")
    text(260, 125, 20, "BOTTOM CABLE ENTRY", "GA-ZONE-TBD")
    text(285, 1470, 20, "TOP CABLE EXIT", "GA-ZONE-TBD")
    for index, name in enumerate(("R", "Y", "B", "N")):
        y = 1335 - index * 15
        line(110, y, 690, y, "GA-BUSBAR-TBD")
        text(80, y - 4, 12, name, "GA-BUSBAR-TBD")
    for item in preview["layout_items"]:
        layer = "GA-TBD" if item["status"] in {"VERIFY", "TBD"} else ("GA-ASSEMBLY" if item["status"] == "ASSEMBLY" else "GA-EQUIPMENT")
        rect(item["x_mm"], item["y_mm"], item["width_mm"], item["height_mm"], layer)
        text(item["x_mm"] + 3, item["y_mm"] + item["height_mm"] / 2, 8, item["reference"], layer)
    notes = [
        "ALL GEOMETRY IS 1:1 mm. PRELIMINARY GA - NOT FOR FABRICATION.",
        "NSYSM16840 IS WITHOUT MOUNTING PLATE; COMPATIBLE PLATE REQUIRED.",
        "NO VENT CUTOUT APPROVED. IEC 61439 / IEC TR 60890 THERMAL VERIFICATION REQUIRED.",
        "RED/TBD LAYER: PART REFERENCE OR PHYSICAL DATA REQUIRES CONFIRMATION.",
        f"PREVIEW SHA256 {preview['preview_sha256']}",
    ]
    for index, value in enumerate(notes):
        text(0, -80 - index * 28, 14, value, "GA-NOTES")
    header = "0\nSECTION\n2\nHEADER\n9\n$INSUNITS\n70\n4\n0\nENDSEC\n0\nSECTION\n2\nTABLES\n0\nENDSEC\n0\nSECTION\n2\nENTITIES\n"
    return header + "".join(entities) + "0\nENDSEC\n0\nEOF\n"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output-json", type=Path, required=True)
    parser.add_argument("--output-svg", type=Path, required=True)
    parser.add_argument("--output-png", type=Path, required=True)
    parser.add_argument("--output-dxf", type=Path, required=True)
    args = parser.parse_args()
    preview = build_preview()
    for path in (args.output_json, args.output_svg, args.output_png, args.output_dxf):
        path.parent.mkdir(parents=True, exist_ok=True)
    args.output_json.write_bytes(_canonical(preview) + b"\n")
    args.output_svg.write_text(render_svg(preview), encoding="utf-8")
    render_png(preview, args.output_png)
    args.output_dxf.write_text(render_dxf(preview), encoding="ascii")
    print(json.dumps({"preview_sha256": preview["preview_sha256"], "drawing_mutation_performed": False}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
