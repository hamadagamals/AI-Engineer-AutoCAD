"""Create an editable 150 m2 single-storey house concept for AutoCAD review."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

import ezdxf
from ezdxf.enums import TextEntityAlignment


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _segments(start: float, end: float, gaps):
    cursor = start
    for left, right in sorted(gaps):
        if left > cursor:
            yield cursor, left
        cursor = max(cursor, right)
    if cursor < end:
        yield cursor, end


def _hwall(msp, y: float, x1: float, x2: float, gaps=()) -> None:
    for left, right in _segments(x1, x2, tuple(gaps)):
        for offset in (-100, 100):
            msp.add_line((left, y + offset), (right, y + offset), dxfattribs={"layer": "A-WALL"})


def _vwall(msp, x: float, y1: float, y2: float, gaps=()) -> None:
    for bottom, top in _segments(y1, y2, tuple(gaps)):
        for offset in (-100, 100):
            msp.add_line((x + offset, bottom), (x + offset, top), dxfattribs={"layer": "A-WALL"})


def _rect(msp, x1: float, y1: float, x2: float, y2: float, layer: str) -> None:
    msp.add_lwpolyline(
        [(x1, y1), (x2, y1), (x2, y2), (x1, y2)],
        close=True,
        dxfattribs={"layer": layer},
    )


def _label(msp, text: str, point, height: float = 220, layer: str = "A-TEXT") -> None:
    entity = msp.add_text(text, dxfattribs={"layer": layer, "height": height})
    entity.set_placement(point, align=TextEntityAlignment.MIDDLE_CENTER)


def _door(msp, hinge, leaf, radius: float, start: float, end: float) -> None:
    msp.add_line(hinge, leaf, dxfattribs={"layer": "A-DOOR"})
    msp.add_arc(hinge, radius, start, end, dxfattribs={"layer": "A-DOOR"})


def _window_h(msp, x1: float, x2: float, y: float) -> None:
    for offset in (-45, 45):
        msp.add_line((x1, y + offset), (x2, y + offset), dxfattribs={"layer": "A-WINDOW"})


def _window_v(msp, x: float, y1: float, y2: float) -> None:
    for offset in (-45, 45):
        msp.add_line((x + offset, y1), (x + offset, y2), dxfattribs={"layer": "A-WINDOW"})


def _dimension(msp, base, p1, p2, angle: float) -> None:
    dim = msp.add_linear_dim(
        base=base, p1=p1, p2=p2, angle=angle, dimstyle="EZDXF",
        dxfattribs={"layer": "A-DIMS"},
    )
    dim.render()


def _room(name: str, width: int, depth: int, window_width: int, window_height: int,
          min_area: float, min_dimension: float, ratio: float) -> dict[str, object]:
    area = round(width * depth / 1_000_000, 2)
    opening = round(window_width * window_height / 1_000_000, 2)
    return {
        "name": name,
        "clear_dimensions_mm": [width, depth],
        "clear_area_m2": area,
        "window_area_m2": opening,
        "required_opening_area_m2": round(area * ratio, 2),
        "area_pass": area >= min_area,
        "dimension_pass": min(width, depth) / 1000 >= min_dimension,
        "opening_pass": opening >= area * ratio,
    }


def _report(result: dict[str, object]) -> str:
    rows = [
        "| Space | Clear size (m) | Area (m2) | Window (m2) | Review |",
        "|---|---:|---:|---:|---|",
    ]
    for room in result["room_schedule"]:
        width, depth = room["clear_dimensions_mm"]
        passed = room["area_pass"] and room["dimension_pass"] and room["opening_pass"]
        rows.append(
            f"| {room['name']} | {width / 1000:.2f} x {depth / 1000:.2f} | "
            f"{room['clear_area_m2']:.2f} | {room['window_area_m2']:.2f} | "
            f"{'PASS' if passed else 'REVIEW'} |"
        )
    return (
        "# 150 m2 single-storey house concept review\n\n"
        "## Assumptions\n\n"
        "- Gross footprint: 15.00 x 10.00 m; assumed south/front 15.00 m facade.\n"
        "- All walls are 200 mm; proposed clear height is 2.80 m.\n"
        "- Three bedrooms, bathroom, living/reception, kitchen, utility, foyer and corridor.\n\n"
        "## Room review\n\n" + "\n".join(rows) +
        "\n\n## Egyptian review basis\n\n"
        "- Unified Building Law 119/2008 and Executive Regulations 144/2009.\n"
        "- Article 93: 2.70 m habitable-room clear-height basis.\n"
        "- Article 94: room, kitchen and bathroom area/dimension minima.\n"
        "- Article 96: 8% habitable and 10% service natural-opening ratios.\n\n"
        "## Hold points\n\n"
        "Concept only. Confirm plot, north/street, setbacks, building ratio, local authority, "
        "structure, soil, drainage, fire, accessibility, MEP and licensed designer approval.\n"
    )


def build(output_dir: Path) -> dict[str, object]:
    output_dir = output_dir.resolve()
    output_dir.mkdir(parents=True, exist_ok=True)
    output = output_dir / "house-150sqm-egyptian-code-concept-review.dxf"
    document = ezdxf.new("R2013", setup=True)
    document.header["$INSUNITS"] = 4
    for name, color in (
        ("A-WALL", 7), ("A-DOOR", 1), ("A-WINDOW", 4), ("A-TEXT", 3),
        ("A-DIMS", 2), ("A-FIXTURE", 6), ("A-NOTE", 5),
    ):
        document.layers.add(name, color=color)
    msp = document.modelspace()

    # External envelope with real gaps for the reviewed openings.
    _hwall(msp, 100, 100, 14900, ((1800, 4500), (6900, 8100), (8800, 10300)))
    _hwall(msp, 9900, 100, 14900, ((1400, 3200), (6500, 8300), (11700, 13500)))
    _vwall(msp, 100, 100, 9900, ((1200, 3200), (4800, 5400)))
    _vwall(msp, 14900, 100, 9900, ((1000, 2400), (4800, 5400)))

    # Public/service zone, private corridor and three rear bedrooms.
    _vwall(msp, 6500, 200, 4300, ((1200, 2200),))
    _vwall(msp, 8000, 200, 4300)
    _vwall(msp, 11200, 200, 4300)
    _vwall(msp, 12600, 200, 4300)
    _hwall(msp, 4300, 200, 14800, ((5400, 6300), (6600, 7900), (8500, 9400),
                                           (11500, 12300), (13300, 14100)))
    _hwall(msp, 5800, 200, 14800, ((3800, 4700), (7000, 7900), (10300, 11200)))
    _vwall(msp, 5000, 5800, 9800)
    _vwall(msp, 10000, 5800, 9800)

    # Windows, entrance and room doors.
    for x1, x2, y in ((1800, 4500, 100), (8800, 10300, 100),
                       (1400, 3200, 9900), (6500, 8300, 9900), (11700, 13500, 9900)):
        _window_h(msp, x1, x2, y)
    for x, y1, y2 in ((100, 1200, 3200), (100, 4800, 5400),
                       (14900, 1000, 2400), (14900, 4800, 5400)):
        _window_v(msp, x, y1, y2)
    doors = (
        ((6900, 200), (6900, 1400), 1200, 0, 90),
        ((6400, 1200), (5500, 1200), 900, 90, 180),
        ((5400, 4200), (5400, 3300), 900, 270, 360),
        ((8500, 4200), (8500, 3300), 900, 270, 360),
        ((11500, 4200), (11500, 3400), 800, 270, 360),
        ((13300, 4200), (13300, 3400), 800, 270, 360),
        ((3800, 5900), (3800, 6800), 900, 0, 90),
        ((7000, 5900), (7000, 6800), 900, 0, 90),
        ((10300, 5900), (10300, 6800), 900, 0, 90),
    )
    for door in doors:
        _door(msp, *door)

    # Schematic fixtures and furniture support scale review.
    for fixture in ((8350, 450, 10850, 1000), (10300, 1000, 10850, 3100),
                    (12900, 500, 14500, 1150), (13600, 1700, 14500, 2450),
                    (900, 1000, 3300, 1900), (900, 6500, 2600, 8500),
                    (5800, 6500, 7500, 8500), (10800, 6500, 12500, 8500)):
        _rect(msp, *fixture, "A-FIXTURE")
    msp.add_circle((13250, 3100), 260, dxfattribs={"layer": "A-FIXTURE"})

    labels = (
        ("LIVING / RECEPTION", (3250, 2850), "6.20 x 4.00 m | 24.80 m2"),
        ("PRIVATE FOYER", (7250, 2500), "1.30 x 4.00 m"),
        ("KITCHEN", (9600, 2600), "3.00 x 4.00 m | 12.00 m2"),
        ("UTILITY", (11900, 2600), "1.20 x 4.00 m"),
        ("BATHROOM", (13750, 2850), "2.10 x 4.00 m | 8.40 m2"),
        ("CORRIDOR", (7500, 5050), "1.30 m clear"),
        ("BEDROOM 1", (2550, 7900), "4.70 x 3.90 m | 18.33 m2"),
        ("BEDROOM 2", (7500, 7900), "4.80 x 3.90 m | 18.72 m2"),
        ("BEDROOM 3", (12450, 7900), "4.70 x 3.90 m | 18.33 m2"),
    )
    for name, point, note in labels:
        _label(msp, name, point)
        _label(msp, note, (point[0], point[1] - 320), 140)

    _label(msp, "150 m2 SINGLE-STOREY HOUSE - CONCEPT REVIEW", (7500, 10750), 320)
    _label(msp, "Assumed south/front facade - dimensions in millimetres", (7500, -1050), 170)
    _dimension(msp, (7500, -450), (0, 0), (15000, 0), 0)
    _dimension(msp, (-450, 5000), (0, 0), (0, 10000), 90)

    schedule = [
        _room("Living / reception", 6200, 4000, 2700, 1200, 7.5, 2.5, 0.08),
        _room("Kitchen", 3000, 4000, 1500, 1200, 3.0, 1.5, 0.10),
        _room("Bathroom", 2100, 4000, 1400, 700, 1.5, 1.2, 0.10),
        _room("Bedroom 1", 4700, 3900, 1800, 1200, 7.5, 2.5, 0.08),
        _room("Bedroom 2", 4800, 3900, 1800, 1200, 7.5, 2.5, 0.08),
        _room("Bedroom 3", 4700, 3900, 1800, 1200, 7.5, 2.5, 0.08),
    ]
    checks_pass = all(
        room["area_pass"] and room["dimension_pass"] and room["opening_pass"]
        for room in schedule
    )
    result = {
        "schema_version": 1,
        "result": "architectural-concept-review-only",
        "output_dxf": str(output),
        "units": "mm",
        "external_dimensions_mm": [15000, 10000],
        "gross_area_m2": 150.0,
        "wall_thickness_mm": 200,
        "proposed_clear_height_m": 2.8,
        "assumed_front_orientation": "south",
        "room_schedule": schedule,
        "checked_articles": [93, 94, 96],
        "minimum_room_and_opening_checks_pass": checks_pass,
        "existing_drawing_mutation_performed": False,
        "limitations": [
            "Concept review only; not a permit, construction, structural, or MEP release.",
            "Plot, setbacks, building ratio, authority rules and final orientation are not supplied.",
            "Licensed architect and structural engineer review is required.",
        ],
    }

    notes = (
        "EGYPTIAN REVIEW BASIS",
        "Law 119/2008 + Executive Regulations 144/2009",
        "Art. 93: proposed clear height 2.80 m",
        "Art. 94: room/kitchen/bath minima checked in JSON",
        "Art. 96: natural-opening ratios checked in JSON",
        "HOLD: plot, street, north, setbacks and local authority not supplied",
        "HOLD: structural, soil, drainage, fire and MEP not included",
        "CONCEPT REVIEW ONLY - NOT FOR PERMIT OR CONSTRUCTION",
    )
    for index, note in enumerate(notes):
        entity = msp.add_text(note, dxfattribs={"layer": "A-NOTE", "height": 190})
        entity.set_placement((15800, 9300 - index * 480), align=TextEntityAlignment.MIDDLE_LEFT)

    document.set_modelspace_vport(height=13500, center=(11500, 5000))
    document.saveas(output)
    result["output_sha256"] = _sha256(output)
    (output_dir / "house-150sqm-egyptian-code-concept-review.json").write_text(
        json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    (output_dir / "house-150sqm-egyptian-code-concept-review.md").write_text(
        _report(result), encoding="utf-8"
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
