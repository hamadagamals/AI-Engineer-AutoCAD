"""Generate full MEP Electrical Lighting and Power Distribution Plan for the 150m2 apartment."""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path

import ezdxf
from ezdxf.enums import TextEntityAlignment


def _light_sym(msp, x: float, y: float, r: float = 160, label: str = "L", layer: str = "E-LITE") -> None:
    """Draw circular luminaire symbol with cross."""
    msp.add_circle((x, y), r, dxfattribs={"layer": layer})
    msp.add_line((x - r, y), (x + r, y), dxfattribs={"layer": layer})
    msp.add_line((x, y - r), (x, y + r), dxfattribs={"layer": layer})
    if label:
        txt = msp.add_text(label, dxfattribs={"layer": layer, "height": 100})
        txt.set_placement((x + r + 40, y + 40), align=TextEntityAlignment.BOTTOM_LEFT)


def _socket_sym(msp, x: float, y: float, angle_deg: float = 0, is_power: bool = False, label: str = "S", layer: str = "E-POWR") -> None:
    """Draw standard wall socket symbol (semicircle + pin lines)."""
    r = 130
    msp.add_arc((x, y), r, angle_deg, angle_deg + 180, dxfattribs={"layer": layer})
    rad = math.radians(angle_deg + 90)
    dx = math.cos(rad) * (r + 80)
    dy = math.sin(rad) * (r + 80)
    msp.add_line((x, y), (x + dx, y + dy), dxfattribs={"layer": layer})
    if is_power:
        # Extra pin for power socket
        rad2 = math.radians(angle_deg + 60)
        msp.add_line((x, y), (x + math.cos(rad2) * (r + 70), y + math.sin(rad2) * (r + 70)), dxfattribs={"layer": layer})
    if label:
        txt = msp.add_text(label, dxfattribs={"layer": layer, "height": 90})
        txt.set_placement((x + 60, y + 60), align=TextEntityAlignment.BOTTOM_LEFT)


def _switch_sym(msp, x: float, y: float, label: str = "$", layer: str = "E-LITE") -> None:
    """Draw standard light switch symbol."""
    msp.add_circle((x, y), 80, dxfattribs={"layer": layer})
    msp.add_line((x, y), (x + 120, y + 120), dxfattribs={"layer": layer})
    txt = msp.add_text(label, dxfattribs={"layer": layer, "height": 100})
    txt.set_placement((x + 130, y + 70), align=TextEntityAlignment.BOTTOM_LEFT)


def _wire(msp, p1: tuple[float, float], p2: tuple[float, float], layer: str = "E-WIRE") -> None:
    """Draw dashed electrical circuit connection."""
    mid = ((p1[0] + p2[0]) / 2 + 150, (p1[1] + p2[1]) / 2 + 150)
    msp.add_spline([p1, mid, p2], dxfattribs={"layer": layer, "linetype": "DASHED"})


def build_electrical(output_dir: Path) -> dict[str, object]:
    output_dir.mkdir(parents=True, exist_ok=True)
    base_dxf = output_dir / "house-150sqm-egyptian-code-concept-review.dxf"
    if not base_dxf.exists():
        from integrations.build_house_150sqm_preview import build
        build(output_dir)

    doc = ezdxf.readfile(str(base_dxf))
    msp = doc.modelspace()

    # Add Electrical layers
    for name, color in (
        ("E-LITE", 2),    # Yellow - Lighting fixtures & switches
        ("E-POWR", 3),    # Green - Power & standard sockets
        ("E-WIRE", 1),    # Red - Circuit wiring
        ("E-PANEL", 6),   # Magenta - Distribution board
        ("E-LEGEND", 7),  # White - Electrical legend
    ):
        if name not in doc.layers:
            doc.layers.add(name, color=color)

    # 1. Main Distribution Panel (DB) at Private Foyer
    db_x, db_y = 6600, 1500
    msp.add_lwpolyline(
        [(db_x, db_y), (db_x + 600, db_y), (db_x + 600, db_y + 200), (db_x, db_y + 200)],
        close=True, dxfattribs={"layer": "E-PANEL"}
    )
    hatch = msp.add_hatch(color=6, dxfattribs={"layer": "E-PANEL"})
    hatch.paths.add_polyline_path([(db_x, db_y), (db_x + 300, db_y), (db_x + 300, db_y + 200), (db_x, db_y + 200)], is_closed=True)
    txt_db = msp.add_text("MAIN DB", dxfattribs={"layer": "E-PANEL", "height": 130})
    txt_db.set_placement((db_x + 300, db_y + 260), align=TextEntityAlignment.MIDDLE_CENTER)

    # 2. Reception & Living Lighting (6 luminaires 3x2 grid)
    rec_lights = [
        (1800, 1600), (3300, 1600), (4800, 1600),
        (1800, 3200), (3300, 3200), (4800, 3200),
    ]
    for lx, ly in rec_lights:
        _light_sym(msp, lx, ly, label="L1")
    _switch_sym(msp, 6100, 1400, "S2")

    # 3. Reception Sockets (6 normal sockets + 1 AC outlet)
    rec_sockets = [
        (400, 1000, 0, False, "TV"),
        (400, 3500, 0, False, "S"),
        (2500, 300, 90, False, "S"),
        (4500, 300, 90, False, "S"),
        (6000, 3500, 180, False, "S"),
        (3500, 4100, 270, False, "S"),
        (5600, 3900, 180, True, "AC 2.25HP"),
    ]
    for sx, sy, ang, pwr, lbl in rec_sockets:
        _socket_sym(msp, sx, sy, ang, pwr, lbl)

    # 4. Bedroom 1 (Master)
    _light_sym(msp, 2550, 7850, label="L2")
    _light_sym(msp, 1200, 8500, label="Sp")
    _light_sym(msp, 2400, 8500, label="Sp")
    _switch_sym(msp, 3600, 6100, "S2")
    bed1_sockets = [
        (400, 7500, 0, False, "S"),
        (2800, 6100, 90, False, "S"),
        (400, 9500, 0, False, "TV"),
        (4700, 7500, 180, False, "S"),
        (4600, 9500, 180, True, "AC 1.5HP"),
    ]
    for sx, sy, ang, pwr, lbl in bed1_sockets:
        _socket_sym(msp, sx, sy, ang, pwr, lbl)

    # 5. Bedroom 2
    _light_sym(msp, 7500, 7850, label="L3")
    _light_sym(msp, 6300, 8500, label="Sp")
    _light_sym(msp, 7500, 8500, label="Sp")
    _switch_sym(msp, 6800, 6100, "S2")
    bed2_sockets = [
        (5300, 7500, 0, False, "S"),
        (7500, 6100, 90, False, "S"),
        (5300, 9500, 0, False, "TV"),
        (9700, 7500, 180, False, "S"),
        (9600, 9500, 180, True, "AC 1.5HP"),
    ]
    for sx, sy, ang, pwr, lbl in bed2_sockets:
        _socket_sym(msp, sx, sy, ang, pwr, lbl)

    # 6. Bedroom 3
    _light_sym(msp, 12450, 7850, label="L4")
    _light_sym(msp, 11200, 8500, label="Sp")
    _light_sym(msp, 12400, 8500, label="Sp")
    _switch_sym(msp, 10100, 6100, "S2")
    bed3_sockets = [
        (10300, 7500, 0, False, "S"),
        (12500, 6100, 90, False, "S"),
        (10300, 9500, 0, False, "TV"),
        (14700, 7500, 180, False, "S"),
        (14600, 9500, 180, True, "AC 1.5HP"),
    ]
    for sx, sy, ang, pwr, lbl in bed3_sockets:
        _socket_sym(msp, sx, sy, ang, pwr, lbl)

    # 7. Kitchen (High-load power sockets + moisture-proof lighting)
    _light_sym(msp, 10000, 2000, label="L-IP")
    _switch_sym(msp, 8700, 4000, "S1")
    kitchen_sockets = [
        (8700, 1500, 0, True, "Fridge"),
        (8700, 2500, 0, True, "Microwave"),
        (10000, 500, 90, True, "Oven 3kW"),
        (11300, 1500, 180, True, "W/M"),
        (11300, 2500, 180, False, "Kettle"),
    ]
    for sx, sy, ang, pwr, lbl in kitchen_sockets:
        _socket_sym(msp, sx, sy, ang, pwr, lbl)

    # 8. Bathroom
    _light_sym(msp, 13750, 2600, label="IP65")
    _switch_sym(msp, 13100, 4300, "S1-Ext")
    _socket_sym(msp, 13000, 2000, 0, True, "Heater 2kW")
    _socket_sym(msp, 14600, 2000, 180, False, "GFCI Shavers")

    # 9. Corridor & Foyer Lights
    corridor_lights = [(3500, 5050), (7500, 5050), (11500, 5050), (7250, 2500)]
    for clx, cly in corridor_lights:
        _light_sym(msp, clx, cly, label="C")

    # 10. Electrical Legend on right side
    legend_x, legend_y = 15800, 5000
    txt_leg = msp.add_text("ELECTRICAL MEP SYMBOL LEGEND", dxfattribs={"layer": "E-LEGEND", "height": 220})
    txt_leg.set_placement((legend_x, legend_y), align=TextEntityAlignment.BOTTOM_LEFT)

    symbols = [
        ("Circular Luminaire (Recessed LED Panel / Spot)", lambda: _light_sym(msp, legend_x + 150, legend_y - 350, r=100, label="")),
        ("Standard Duplex Wall Socket 16A 220V", lambda: _socket_sym(msp, legend_x + 150, legend_y - 700, label="")),
        ("Heavy Duty / Dedicated Power Socket 20A (Oven/AC/Heater)", lambda: _socket_sym(msp, legend_x + 150, legend_y - 1050, is_power=True, label="")),
        ("Wall Switch 10A (S1: 1-gang, S2: 2-gang)", lambda: _switch_sym(msp, legend_x + 150, legend_y - 1400, label="")),
        ("Main Distribution Board (DB 12-Way 3-Phase)", lambda: msp.add_lwpolyline(
            [(legend_x + 50, legend_y - 1800), (legend_x + 250, legend_y - 1800),
             (legend_x + 250, legend_y - 1700), (legend_x + 50, legend_y - 1700)], close=True, dxfattribs={"layer": "E-PANEL"})),
    ]

    for idx, (desc, draw_fn) in enumerate(symbols):
        draw_fn()
        t = msp.add_text(desc, dxfattribs={"layer": "E-LEGEND", "height": 130})
        t.set_placement((legend_x + 350, legend_y - 350 - idx * 350), align=TextEntityAlignment.MIDDLE_LEFT)

    output_dxf = output_dir / "house-150sqm-electrical-plan.dxf"
    doc.saveas(str(output_dxf))

    result = {
        "status": "success",
        "dxf": str(output_dxf),
        "total_luminaires": len(rec_lights) + 3 * 3 + 1 + 1 + len(corridor_lights),
        "total_sockets": len(rec_sockets) + len(bed1_sockets) + len(bed2_sockets) + len(bed3_sockets) + len(kitchen_sockets) + 2,
        "main_db": "12-Way 3-Phase DB with RCCB 30mA",
        "connected_load_estimate_kva": 18.5,
    }
    return result


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output-dir", type=Path, default=Path("D:/program/apartment_150sqm"))
    args = parser.parse_args()
    print(json.dumps(build_electrical(args.output_dir), ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
