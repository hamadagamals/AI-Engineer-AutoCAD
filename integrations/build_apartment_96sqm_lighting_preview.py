"""Add a provisional lumen-method lighting layout to the privacy apartment plan."""

from __future__ import annotations

import argparse
import csv
import json
import math
from dataclasses import asdict, dataclass
from pathlib import Path

import ezdxf
from ezdxf.enums import TextEntityAlignment


@dataclass(frozen=True, slots=True)
class RoomLighting:
    room: str
    area_m2: float
    target_lux: float
    luminaire_lm: float
    luminaire_w: float
    quantity: int
    achieved_lux: float
    positions_mm: tuple[tuple[float, float], ...]


CU = 0.60
MF = 0.80


def _room(room: str, area: float, target: float, lumens: float, watts: float, positions) -> RoomLighting:
    required = math.ceil(target * area / (lumens * CU * MF))
    if required != len(positions):
        raise ValueError(f"{room}: {len(positions)} positions do not match calculated quantity {required}.")
    achieved = required * lumens * CU * MF / area
    return RoomLighting(room, area, target, lumens, watts, required, round(achieved, 1), tuple(positions))


def schedule() -> tuple[RoomLighting, ...]:
    return (
        _room("GUEST RECEPTION", 13.44, 200, 1800, 15, ((1500, 1050), (3700, 1050), (1500, 2250), (3700, 2250))),
        _room("FAMILY LOUNGE", 14.84, 200, 1800, 15, ((7700, 1050), (10500, 1050), (7700, 2250), (10500, 2250))),
        _room("BEDROOM 1", 13.16, 150, 1800, 15, ((1300, 6300), (2550, 6300), (3800, 6300))),
        _room("BEDROOM 2", 8.46, 150, 1800, 15, ((1800, 4200), (3600, 4200))),
        _room("BEDROOM 3", 14.84, 150, 1800, 15, ((7600, 6500), (9200, 6500), (10800, 6500))),
        _room("KITCHEN", 3.20, 300, 1200, 10, ((9500, 4450), (10800, 4450))),
        _room("BATH", 1.80, 200, 900, 8, ((7600, 4450),)),
        _room("PRIVATE FOYER", 3.64, 100, 900, 8, ((5750, 1650),)),
        _room("PRIVATE CORRIDOR", 6.24, 100, 900, 8, ((5750, 4300), (5750, 6500))),
        _room("SERVICE LOBBY", 3.18, 100, 900, 8, ((7800, 3400),)),
    )


def build(source: Path, output_dir: Path) -> dict[str, object]:
    source = source.resolve()
    output_dir = output_dir.resolve()
    output_dir.mkdir(parents=True, exist_ok=True)
    drawing = ezdxf.readfile(source)
    if "E-LIGHT" not in drawing.layers:
        drawing.layers.add("E-LIGHT", color=2)
    if "E-LIGHT-TEXT" not in drawing.layers:
        drawing.layers.add("E-LIGHT-TEXT", color=3)
    msp = drawing.modelspace()
    rows = schedule()
    number = 1
    for row in rows:
        for x, y in row.positions_mm:
            msp.add_circle((x, y), 140, dxfattribs={"layer": "E-LIGHT"})
            msp.add_line((x - 100, y), (x + 100, y), dxfattribs={"layer": "E-LIGHT"})
            msp.add_line((x, y - 100), (x, y + 100), dxfattribs={"layer": "E-LIGHT"})
            text = msp.add_text(f"L{number}", dxfattribs={"layer": "E-LIGHT-TEXT", "height": 110})
            text.set_placement((x + 190, y + 120), align=TextEntityAlignment.LEFT)
            number += 1

    output_dxf = output_dir / "apartment-96sqm-lighting-lumen-method-review.dxf"
    drawing.saveas(output_dxf)
    csv_path = output_dir / "apartment-96sqm-lighting-calculations.csv"
    with csv_path.open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.writer(stream)
        writer.writerow(("Room", "Area m2", "Target lux (provisional)", "Luminaire lm", "Luminaire W", "CU", "MF", "Quantity", "Calculated maintained lux", "Connected W"))
        for row in rows:
            writer.writerow((row.room, row.area_m2, row.target_lux, row.luminaire_lm, row.luminaire_w, CU, MF, row.quantity, row.achieved_lux, row.quantity * row.luminaire_w))
    result = {
        "result": "provisional-lumen-method-review-only",
        "source": str(source),
        "output_dxf": str(output_dxf),
        "calculation_csv": str(csv_path),
        "total_luminaires": sum(row.quantity for row in rows),
        "total_connected_w": sum(row.quantity * row.luminaire_w for row in rows),
        "cu": CU,
        "mf": MF,
        "rooms": [asdict(row) for row in rows],
        "drawing_mutation_performed": False,
        "limitations": [
            "Target lux values are provisional design assumptions pending a licensed Egyptian-code table review.",
            "Lumen method does not verify uniformity, glare, vertical illuminance, daylight, emergency lighting, or switching controls.",
            "Final validation requires ceiling height, reflectances, exact luminaire data and IES/LDT photometry in lighting software.",
        ],
    }
    (output_dir / "apartment-96sqm-lighting-calculations.json").write_text(
        json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    return result


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    args = parser.parse_args()
    print(json.dumps(build(args.source, args.output_dir), ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
