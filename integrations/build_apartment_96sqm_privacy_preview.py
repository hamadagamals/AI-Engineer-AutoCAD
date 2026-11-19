"""Create a privacy-oriented 96 m2 apartment concept for AutoCAD review."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

import ezdxf
from ezdxf.enums import TextEntityAlignment


def _hash(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _segments(start: float, end: float, gaps: tuple[tuple[float, float], ...]):
    cursor = start
    for left, right in sorted(gaps):
        if left > cursor:
            yield cursor, left
        cursor = max(cursor, right)
    if cursor < end:
        yield cursor, end


def _hwall(msp, y: float, x1: float, x2: float, gaps=()) -> None:
    for a, b in _segments(x1, x2, tuple(gaps)):
        for offset in (-100, 100):
            msp.add_line((a, y + offset), (b, y + offset), dxfattribs={"layer": "A-WALL"})


def _vwall(msp, x: float, y1: float, y2: float, gaps=()) -> None:
    for a, b in _segments(y1, y2, tuple(gaps)):
        for offset in (-100, 100):
            msp.add_line((x + offset, a), (x + offset, b), dxfattribs={"layer": "A-WALL"})


def _label(msp, text: str, x: float, y: float, height=190) -> None:
    item = msp.add_text(text, dxfattribs={"layer": "A-TEXT", "height": height})
    item.set_placement((x, y), align=TextEntityAlignment.MIDDLE_CENTER)


def _door(msp, hinge, leaf, radius, start_angle, end_angle) -> None:
    msp.add_line(hinge, leaf, dxfattribs={"layer": "A-DOOR"})
    msp.add_arc(hinge, radius, start_angle, end_angle, dxfattribs={"layer": "A-DOOR"})


def _window_h(msp, x1: float, x2: float, y: float) -> None:
    for offset in (-45, 45):
        msp.add_line((x1, y + offset), (x2, y + offset), dxfattribs={"layer": "A-WINDOW"})


def _window_v(msp, x: float, y1: float, y2: float) -> None:
    for offset in (-45, 45):
        msp.add_line((x + offset, y1), (x + offset, y2), dxfattribs={"layer": "A-WINDOW"})


def build(output_dir: Path) -> dict[str, object]:
    output_dir = output_dir.resolve()
    output_dir.mkdir(parents=True, exist_ok=True)
    output = output_dir / "apartment-96sqm-privacy-concept-review.dxf"
    doc = ezdxf.new("R2013", setup=True)
    doc.header["$INSUNITS"] = 4
    for name, color in (("A-WALL", 7), ("A-DOOR", 1), ("A-WINDOW", 4), ("A-TEXT", 3), ("A-DIMS", 2), ("A-FIXTURE", 6)):
        doc.layers.add(name, color=color)
    msp = doc.modelspace()

    # 12 x 8 m external footprint. The entrance opens only into the private foyer.
    _hwall(msp, 100, 100, 11900, ((5500, 6500),))
    _hwall(msp, 7900, 100, 11900)
    _vwall(msp, 100, 100, 7900)
    _vwall(msp, 11900, 100, 7900)

    # Central private spine. Reception and family lounge have separate foyer doors.
    _vwall(msp, 5000, 100, 7900, ((900, 1800), (3500, 4400), (5700, 6600)))
    _vwall(msp, 6500, 100, 7900, ((900, 1800), (3100, 3900), (5700, 6600)))
    _hwall(msp, 3000, 100, 5000)
    _hwall(msp, 5000, 100, 5000)
    _hwall(msp, 5000, 6500, 11900)

    # Right-side private service corridor: kitchen and bath doors both face it.
    _hwall(msp, 3000, 6500, 11900, ((6500, 7300),))
    _hwall(msp, 3800, 6500, 11900, ((7000, 7800), (9000, 9900)))
    _vwall(msp, 8500, 3800, 5000)

    # Correctly oriented doors located in actual wall openings.
    _door(msp, (5500, 200), (5500, 1200), 1000, 0, 90)       # Main entrance -> foyer
    _door(msp, (4900, 900), (4000, 900), 900, 90, 180)       # Foyer -> reception
    _door(msp, (6600, 900), (7500, 900), 900, 0, 90)         # Foyer -> family lounge
    _door(msp, (4900, 3500), (4000, 3500), 900, 90, 180)     # Bedroom 2
    _door(msp, (4900, 5700), (4000, 5700), 900, 90, 180)     # Bedroom 1
    _door(msp, (6600, 5700), (7500, 5700), 900, 0, 90)       # Bedroom 3
    _door(msp, (6600, 3100), (7400, 3100), 800, 270, 360)    # Spine -> service lobby
    _door(msp, (7000, 3900), (7000, 4700), 800, 0, 90)       # Bath from service lobby
    _door(msp, (9000, 3900), (9000, 4800), 900, 0, 90)       # Kitchen from service lobby

    # External windows; no window opens onto the private spine.
    _window_h(msp, 900, 2700, 200)
    _window_h(msp, 8500, 10800, 200)
    _window_h(msp, 900, 2600, 7800)
    _window_h(msp, 8600, 10600, 7800)
    _window_v(msp, 200, 3500, 4400)
    _window_v(msp, 11800, 4100, 4700)

    # Schematic sanitary and kitchen counters.
    msp.add_lwpolyline([(8700, 4000), (11600, 4000), (11600, 4550), (8700, 4550)], close=True, dxfattribs={"layer": "A-FIXTURE"})
    msp.add_lwpolyline([(6800, 4100), (7450, 4100), (7450, 4800), (6800, 4800)], close=True, dxfattribs={"layer": "A-FIXTURE"})
    msp.add_circle((7950, 4500), 230, dxfattribs={"layer": "A-FIXTURE"})

    labels = (
        ("GUEST RECEPTION", 2550, 1550),
        ("PRIVATE FOYER", 5750, 1600),
        ("FAMILY LOUNGE", 9200, 1550),
        ("BEDROOM 2", 2550, 4000),
        ("BEDROOM 1", 2550, 6500),
        ("PRIVATE CORRIDOR", 5750, 5200),
        ("SERVICE LOBBY", 9200, 3400),
        ("BATH", 7500, 4450),
        ("KITCHEN", 10200, 4450),
        ("BEDROOM 3", 9200, 6500),
    )
    for text, x, y in labels:
        _label(msp, text, x, y)
    _label(msp, "96 m2 PRIVACY-ORIENTED CONCEPT - 12.00 x 8.00 m", 6000, 8450, 260)
    _label(msp, "Kitchen and bath open only to the private/service circulation", 6000, -650, 150)

    dim = msp.add_linear_dim(base=(6000, -350), p1=(0, 0), p2=(12000, 0), angle=0, dimstyle="EZDXF", dxfattribs={"layer": "A-DIMS"})
    dim.render()
    dim = msp.add_linear_dim(base=(-350, 4000), p1=(0, 0), p2=(0, 8000), angle=90, dimstyle="EZDXF", dxfattribs={"layer": "A-DIMS"})
    dim.render()
    doc.set_modelspace_vport(height=10200, center=(6000, 4000))
    doc.saveas(output)

    result = {
        "result": "privacy-oriented-concept-review-only",
        "output_dxf": str(output),
        "output_sha256": _hash(output),
        "gross_area_m2": 96.0,
        "units": "mm",
        "privacy_features": [
            "Entrance terminates in a private foyer.",
            "Guest reception has a dedicated foyer door.",
            "Family lounge is separated from guest reception.",
            "Kitchen and bathroom open to private/service circulation only.",
            "All three bedrooms open to the private central corridor.",
        ],
        "existing_drawing_mutation_performed": False,
        "limitations": ["Concept only; architect and authority review required before construction."],
    }
    (output_dir / "apartment-96sqm-privacy-concept-review.json").write_text(
        json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    return result


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output-dir", type=Path, required=True)
    args = parser.parse_args()
    print(json.dumps(build(args.output_dir), ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
