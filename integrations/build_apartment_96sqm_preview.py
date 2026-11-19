"""Create an isolated, editable 96 m2 apartment concept plan for AutoCAD review."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

import ezdxf
from ezdxf.enums import TextEntityAlignment


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while chunk := stream.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


def _line(msp, start, end, layer: str) -> None:
    msp.add_line(start, end, dxfattribs={"layer": layer})


def _rect(msp, x1: float, y1: float, x2: float, y2: float, layer: str) -> None:
    msp.add_lwpolyline(
        [(x1, y1), (x2, y1), (x2, y2), (x1, y2)],
        close=True,
        dxfattribs={"layer": layer},
    )


def _label(msp, text: str, point: tuple[float, float], height: float = 220) -> None:
    entity = msp.add_text(text, dxfattribs={"layer": "A-TEXT", "height": height})
    entity.set_placement(point, align=TextEntityAlignment.MIDDLE_CENTER)


def _door(msp, hinge: tuple[float, float], end: tuple[float, float], radius: float, start: float, end_angle: float) -> None:
    _line(msp, hinge, end, "A-DOOR")
    msp.add_arc(hinge, radius, start, end_angle, dxfattribs={"layer": "A-DOOR"})


def _window_h(msp, x1: float, x2: float, y: float) -> None:
    for offset in (-45, 45):
        _line(msp, (x1, y + offset), (x2, y + offset), "A-WINDOW")


def _window_v(msp, x: float, y1: float, y2: float) -> None:
    for offset in (-45, 45):
        _line(msp, (x + offset, y1), (x + offset, y2), "A-WINDOW")


def _dimension(msp, base, p1, p2, angle: float) -> None:
    dim = msp.add_linear_dim(
        base=base,
        p1=p1,
        p2=p2,
        angle=angle,
        dimstyle="EZDXF",
        dxfattribs={"layer": "A-DIMS"},
    )
    dim.render()


def build(output_dir: Path) -> dict[str, object]:
    output_dir = output_dir.resolve()
    output_dir.mkdir(parents=True, exist_ok=True)
    output = output_dir / "apartment-96sqm-concept-review.dxf"

    doc = ezdxf.new("R2013", setup=True)
    doc.header["$INSUNITS"] = 4
    layers = {
        "A-WALL": 7,
        "A-DOOR": 1,
        "A-WINDOW": 4,
        "A-TEXT": 3,
        "A-DIMS": 2,
        "A-FIXTURE": 6,
    }
    for name, color in layers.items():
        doc.layers.add(name, color=color)
    msp = doc.modelspace()

    # External 12,000 x 8,000 mm envelope, nominal 200 mm walls.
    _rect(msp, 0, 0, 12000, 8000, "A-WALL")
    _rect(msp, 200, 200, 11800, 7800, "A-WALL")

    # Internal arrangement: living room below, central corridor above it.
    for y in (3000, 5000):
        _line(msp, (200, y - 100), (5200, y - 100), "A-WALL")
        _line(msp, (200, y + 100), (5200, y + 100), "A-WALL")
    _line(msp, (6500, 2900), (11800, 2900), "A-WALL")
    _line(msp, (6500, 3100), (11800, 3100), "A-WALL")
    _line(msp, (6500, 4900), (11800, 4900), "A-WALL")
    _line(msp, (6500, 5100), (11800, 5100), "A-WALL")
    for x in (5200, 6500):
        _line(msp, (x - 100, 3000), (x - 100, 7800), "A-WALL")
        _line(msp, (x + 100, 3000), (x + 100, 7800), "A-WALL")
    _line(msp, (8400, 3100), (8400, 4900), "A-WALL")
    _line(msp, (8600, 3100), (8600, 4900), "A-WALL")

    # Doors: front, three bedrooms, bath, kitchen and corridor/living opening.
    _door(msp, (5400, 200), (5400, 1200), 1000, 0, 90)
    _door(msp, (5100, 3500), (4200, 3500), 900, 90, 180)
    _door(msp, (5100, 5600), (4200, 5600), 900, 90, 180)
    _door(msp, (6600, 5600), (7500, 5600), 900, 0, 90)
    _door(msp, (6600, 3500), (7400, 3500), 800, 270, 360)
    _door(msp, (8600, 3500), (9400, 3500), 800, 0, 90)
    _door(msp, (5400, 3000), (5400, 3900), 900, 0, 90)

    # Windows on external walls.
    _window_h(msp, 900, 2600, 7800)
    _window_h(msp, 8700, 10600, 7800)
    _window_h(msp, 900, 2700, 200)
    _window_h(msp, 9300, 11100, 200)
    _window_v(msp, 200, 3500, 4500)
    _window_v(msp, 11800, 3500, 4500)

    # Simple editable fixtures for spatial comprehension.
    _rect(msp, 9000, 3350, 11500, 3900, "A-FIXTURE")
    _rect(msp, 10900, 4100, 11500, 4700, "A-FIXTURE")
    _rect(msp, 6900, 3950, 7500, 4700, "A-FIXTURE")
    msp.add_circle((7900, 4450), 260, dxfattribs={"layer": "A-FIXTURE"})

    labels = (
        ("LIVING / RECEPTION", (6000, 1650), "36.0 m2 gross zone"),
        ("BEDROOM 1", (2700, 6500), "15.6 m2 gross zone"),
        ("BEDROOM 2", (2700, 4000), "10.4 m2 gross zone"),
        ("BEDROOM 3", (9250, 6500), "16.5 m2 gross zone"),
        ("CORRIDOR", (5850, 5600), "1.30 m wide"),
        ("BATH", (7500, 4000), "4.0 m2 gross zone"),
        ("KITCHEN", (10250, 4000), "7.0 m2 gross zone"),
    )
    for name, point, note in labels:
        _label(msp, name, point)
        _label(msp, note, (point[0], point[1] - 300), 130)

    _label(msp, "96 m2 APARTMENT - CONCEPT REVIEW", (6000, 8550), 300)
    _label(msp, "Assumption: external footprint 12.00 x 8.00 m; dimensions in mm", (6000, -900), 160)

    _dimension(msp, (6000, -500), (0, 0), (12000, 0), 0)
    _dimension(msp, (-500, 4000), (0, 0), (0, 8000), 90)
    _dimension(msp, (2600, 8350), (200, 8000), (5200, 8000), 0)
    _dimension(msp, (5850, 8350), (5200, 8000), (6500, 8000), 0)
    _dimension(msp, (9250, 8350), (6500, 8000), (11800, 8000), 0)

    doc.set_modelspace_vport(height=10500, center=(6000, 4000))
    doc.saveas(output)
    result = {
        "schema_version": 1,
        "result": "architectural-concept-review-only",
        "output_dxf": str(output),
        "output_sha256": _sha256(output),
        "units": "mm",
        "external_dimensions_mm": [12000, 8000],
        "gross_area_m2": 96.0,
        "rooms": [item[0] for item in labels],
        "existing_drawing_mutation_performed": False,
        "limitations": [
            "Concept layout only; not an architectural, structural, MEP, or permit release.",
            "Wall thicknesses, openings, orientation, columns, shafts, and local code constraints require review.",
        ],
    }
    evidence = output_dir / "apartment-96sqm-concept-review.json"
    evidence.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return result


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output-dir", type=Path, required=True)
    args = parser.parse_args()
    print(json.dumps(build(args.output_dir), ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
