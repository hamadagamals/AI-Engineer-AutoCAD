"""Build a reviewed lumen-method lighting overlay for the 150 m2 house concept."""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
import math
from collections import Counter
from dataclasses import asdict, dataclass
from pathlib import Path

import ezdxf
from ezdxf.enums import TextEntityAlignment


MF = 0.80
CEILING_HEIGHT_M = 2.80
WORKPLANE_HEIGHT_M = 0.80


@dataclass(frozen=True, slots=True)
class LuminaireType:
    code: str
    description: str
    power_w: float
    lumens: float
    cct_k: int
    cri_min: int
    ip_rating: str
    utilization_factor: float


@dataclass(frozen=True, slots=True)
class RoomLighting:
    room: str
    area_m2: float
    target_lux: float
    luminaire_code: str
    quantity: int
    achieved_lux: float
    positions_mm: tuple[tuple[float, float], ...]
    maximum_spacing_m: float
    spacing_limit_m: float
    spacing_check: str
    note: str


LUMINAIRES = {
    "L1": LuminaireType("L1", "Wide-beam recessed/surface LED downlight", 12, 1200, 3000, 80, "IP20", 0.60),
    "L2": LuminaireType("L2", "Low-glare 300x300 LED panel", 24, 2400, 3000, 80, "IP20", 0.65),
    "L3": LuminaireType("L3", "Moisture-resistant surface LED", 15, 1500, 4000, 80, "IP44 min", 0.55),
    "T1": LuminaireType("T1", "Under-cabinet linear LED, 1 m", 10, 1000, 4000, 90, "IP44", 0.70),
    "M1": LuminaireType("M1", "Mirror LED with diffused front light", 8, 800, 4000, 90, "IP44", 0.60),
}


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _maximum_spacing(positions: tuple[tuple[float, float], ...]) -> float:
    if len(positions) < 2:
        return 0.0
    nearest = []
    for index, point in enumerate(positions):
        nearest.append(
            min(
                math.dist(point, other)
                for other_index, other in enumerate(positions)
                if other_index != index
            )
            / 1000
        )
    return round(max(nearest), 2)


def _room(name: str, area: float, target: float, luminaire_code: str,
          positions: tuple[tuple[float, float], ...], note: str, *,
          circulation: bool = False) -> RoomLighting:
    luminaire = LUMINAIRES[luminaire_code]
    required = math.ceil(target * area / (luminaire.lumens * luminaire.utilization_factor * MF))
    if required != len(positions):
        raise ValueError(f"{name}: {len(positions)} positions do not match calculated quantity {required}.")
    achieved = required * luminaire.lumens * luminaire.utilization_factor * MF / area
    mounting_height = CEILING_HEIGHT_M if circulation else CEILING_HEIGHT_M - WORKPLANE_HEIGHT_M
    spacing_limit = round(1.5 * mounting_height, 2)
    maximum_spacing = _maximum_spacing(positions)
    return RoomLighting(name, area, target, luminaire_code, required, round(achieved, 1), positions,
                        maximum_spacing, spacing_limit,
                        "PASS" if maximum_spacing <= spacing_limit else "REVIEW", note)


def room_schedule() -> tuple[RoomLighting, ...]:
    return (
        _room("LIVING / RECEPTION", 24.80, 200, "L2", ((1800, 1400), (4700, 1400), (1800, 3300), (4700, 3300)), "General ambient; add local reading light where needed."),
        _room("PRIVATE FOYER", 5.20, 100, "L1", ((7250, 2500),), "Entrance and transition lighting.", circulation=True),
        _room("KITCHEN", 12.00, 300, "L2", ((8600, 1200), (10600, 1200), (9600, 3300)), "General lighting; T1 separately raises worktop illumination."),
        _room("UTILITY", 4.80, 200, "L2", ((11900, 2300),), "General service lighting."),
        _room("BATHROOM", 8.40, 200, "L3", ((13750, 900), (13750, 2200), (13750, 3500)), "IP-rated general lighting; M1 serves the mirror."),
        _room("CORRIDOR", 18.98, 100, "L1", ((1800, 5050), (5500, 5050), (9500, 5050), (13200, 5050)), "Linear circulation distribution.", circulation=True),
        _room("BEDROOM 1", 18.33, 150, "L1", ((1400, 6800), (3700, 6800), (2550, 7750), (1400, 8700), (3700, 8700)), "Warm general lighting; local desk/reading light is task lighting."),
        _room("BEDROOM 2", 18.72, 150, "L1", ((6200, 6800), (8800, 6800), (7500, 7750), (6200, 8700), (8800, 8700)), "Warm general lighting; local desk/reading light is task lighting."),
        _room("BEDROOM 3", 18.33, 150, "L1", ((11300, 6800), (13600, 6800), (12450, 7750), (11300, 8700), (13600, 8700)), "Warm general lighting; local desk/reading light is task lighting."),
    )


TASK_POSITIONS = {"T1": ((8850, 750), (10150, 750)), "M1": ((14350, 3100),)}


def _text(msp, value: str, point, height: float = 150, layer: str = "E-LIGHT-TEXT") -> None:
    entity = msp.add_text(value, dxfattribs={"layer": layer, "height": height})
    entity.set_placement(point, align=TextEntityAlignment.MIDDLE_LEFT)


def _draw_symbol(msp, code: str, x: float, y: float, *, add_label: bool = True,
                 scale: float = 1.0) -> None:
    layer = "E-LIGHT-TASK" if code in {"T1", "M1"} else "E-LIGHT-GENERAL"
    if code == "L2":
        msp.add_lwpolyline([(x - 180 * scale, y - 180 * scale), (x + 180 * scale, y - 180 * scale), (x + 180 * scale, y + 180 * scale), (x - 180 * scale, y + 180 * scale)], close=True, dxfattribs={"layer": layer})
        msp.add_line((x - 150 * scale, y - 150 * scale), (x + 150 * scale, y + 150 * scale), dxfattribs={"layer": layer})
        msp.add_line((x - 150 * scale, y + 150 * scale), (x + 150 * scale, y - 150 * scale), dxfattribs={"layer": layer})
    elif code == "T1":
        msp.add_lwpolyline([(x - 500 * scale, y - 60 * scale), (x + 500 * scale, y - 60 * scale), (x + 500 * scale, y + 60 * scale), (x - 500 * scale, y + 60 * scale)], close=True, dxfattribs={"layer": layer})
    elif code == "M1":
        msp.add_line((x, y - 350 * scale), (x, y + 350 * scale), dxfattribs={"layer": layer})
        msp.add_line((x - 80 * scale, y - 350 * scale), (x - 80 * scale, y + 350 * scale), dxfattribs={"layer": layer})
    else:
        msp.add_circle((x, y), 140 * scale, dxfattribs={"layer": layer})
        if code == "L3":
            msp.add_circle((x, y), 100 * scale, dxfattribs={"layer": layer})
        msp.add_line((x - 100 * scale, y), (x + 100 * scale, y), dxfattribs={"layer": layer})
        msp.add_line((x, y - 100 * scale), (x, y + 100 * scale), dxfattribs={"layer": layer})
    if add_label:
        _text(msp, code, (x + 220 * scale, y + 170 * scale), 105 * scale)


def _table_text(msp, value: str, center: tuple[float, float], height: float = 115) -> None:
    entity = msp.add_text(value, dxfattribs={"layer": "E-LIGHT-TABLE-TEXT", "height": height})
    entity.set_placement(center, align=TextEntityAlignment.MIDDLE_CENTER)


def _draw_table(msp, *, title: str, origin: tuple[float, float], widths: tuple[float, ...],
                headers: tuple[str, ...], rows: tuple[tuple[str, ...], ...],
                symbol_column: int | None = None) -> None:
    if len(widths) != len(headers) or any(len(row) != len(headers) for row in rows):
        raise ValueError(f"{title}: table shape does not match the column definition.")
    x, top = origin
    title_height = 520
    row_height = 410
    total_width = sum(widths)
    bottom = top - title_height - row_height * (len(rows) + 1)
    for y in (top, top - title_height, *(top - title_height - row_height * index for index in range(1, len(rows) + 2))):
        msp.add_line((x, y), (x + total_width, y), dxfattribs={"layer": "E-LIGHT-TABLE"})
    msp.add_line((x, top), (x, bottom), dxfattribs={"layer": "E-LIGHT-TABLE"})
    msp.add_line((x + total_width, top), (x + total_width, bottom), dxfattribs={"layer": "E-LIGHT-TABLE"})
    cursor = x
    for width in widths[:-1]:
        cursor += width
        msp.add_line((cursor, top - title_height), (cursor, bottom), dxfattribs={"layer": "E-LIGHT-TABLE"})
    _table_text(msp, title, (x + total_width / 2, top - title_height / 2), 190)
    cursor = x
    header_y = top - title_height - row_height / 2
    for width, header in zip(widths, headers, strict=True):
        _table_text(msp, header, (cursor + width / 2, header_y), 115)
        cursor += width
    for row_index, row in enumerate(rows):
        cursor = x
        center_y = top - title_height - row_height * (row_index + 1.5)
        for column_index, (width, value) in enumerate(zip(widths, row, strict=True)):
            center = (cursor + width / 2, center_y)
            if symbol_column == column_index:
                _draw_symbol(msp, value, *center, add_label=False, scale=0.45)
            else:
                _table_text(msp, value, center, 100 if len(value) > 24 else 115)
            cursor += width


def _add_schedule_to_drawing(msp, rooms: tuple[RoomLighting, ...], counts: Counter[str]) -> None:
    room_rows = tuple(
        (
            room.room,
            f"{room.area_m2:.2f}",
            f"{room.target_lux:.0f}",
            f"{room.achieved_lux:.1f}",
            room.luminaire_code,
            str(room.quantity),
            room.spacing_check,
        )
        for room in rooms
    )
    _draw_table(
        msp,
        title="ROOM ILLUMINANCE SCHEDULE - MAINTAINED LUX",
        origin=(15800, 5300),
        widths=(2700, 900, 1050, 1150, 750, 650, 950),
        headers=("ROOM", "AREA m2", "TARGET lx", "CALC. lx", "TYPE", "QTY", "SPACING"),
        rows=room_rows,
    )
    design_lux = {
        "L1": "100 circulation / 150 bedroom",
        "L2": "200 living/service / 300 kitchen",
        "L3": "200 bathroom",
        "T1": "500 worktop task criterion",
        "M1": "300 mirror vertical criterion",
    }
    legend_rows = tuple(
        (
            code,
            code,
            spec.description,
            f"{spec.power_w:.0f}",
            f"{spec.lumens:.0f}",
            str(spec.cct_k),
            f">={spec.cri_min}",
            spec.ip_rating,
            str(counts[code]),
            design_lux[code],
        )
        for code, spec in LUMINAIRES.items()
    )
    _draw_table(
        msp,
        title="LUMINAIRE LEGEND AND SPECIFICATION",
        origin=(25600, 5300),
        widths=(750, 750, 3150, 650, 850, 800, 700, 950, 650, 2500),
        headers=("SYMBOL", "TYPE", "DESCRIPTION", "W", "lm", "CCT K", "CRI", "IP", "QTY", "DESIGN ILLUMINANCE"),
        rows=legend_rows,
        symbol_column=0,
    )
    _text(msp, "MF=0.80 | CU by type | ceiling=2.80 m | calculated lux is maintained illuminance", (15800, 150), 135, "E-LIGHT-NOTE")
    _text(msp, "T1 and M1 criteria require final point-by-point IES/LDT verification", (25600, 1800), 135, "E-LIGHT-NOTE")
    _text(msp, "REVIEW ONLY - FINAL EGYPTIAN CODE AND LICENSED ENGINEER APPROVAL REQUIRED", (25600, 1500), 135, "E-LIGHT-NOTE")


def _markdown(result: dict[str, object]) -> str:
    room_lines = ["| Space | Target lux | Calculated lux | Type | Qty | Spacing |", "|---|---:|---:|---|---:|---|"]
    for row in result["rooms"]:
        room_lines.append(f"| {row['room']} | {row['target_lux']:.0f} | {row['achieved_lux']:.1f} | {row['luminaire_code']} | {row['quantity']} | {row['spacing_check']} |")
    type_lines = ["| Type | Description | W | lm | CCT | CRI | IP | Qty |", "|---|---|---:|---:|---:|---:|---|---:|"]
    for row in result["luminaire_schedule"]:
        type_lines.append(f"| {row['code']} | {row['description']} | {row['power_w']:.0f} | {row['lumens']:.0f} | {row['cct_k']} K | >= {row['cri_min']} | {row['ip_rating']} | {row['quantity']} |")
    return (
        "# 150 m2 house lighting review\n\n## Maintained illuminance schedule\n\n" + "\n".join(room_lines) +
        "\n\n## Luminaire schedule\n\n" + "\n".join(type_lines) +
        "\n\n## Calculation basis\n\n"
        "- Maintained illuminance: E = N x luminaire lumens x CU x MF / area.\n"
        "- Maintenance factor MF = 0.80; CU is type-specific and shown in the JSON/CSV.\n"
        "- General targets are explicit design criteria pending clause-level licensed Egyptian-code confirmation.\n"
        "- Kitchen worktop: T1 task lights supplement the 300 lux general lighting toward a 500 lux task criterion.\n"
        "- Bathroom mirror: M1 provides vertical facial lighting separately from the 200 lux general lighting.\n"
        "- Final release requires actual product IES/LDT files, reflectances, glare and uniformity simulation, circuit/protection design, bathroom zones, emergency-lighting review and licensed engineer approval.\n"
    )


def build(source: Path, output_dir: Path) -> dict[str, object]:
    source = source.resolve()
    output_dir = output_dir.resolve()
    output_dir.mkdir(parents=True, exist_ok=True)
    source_hash_before = _sha256(source)
    drawing = ezdxf.readfile(source)
    for name, color in (("E-LIGHT-GENERAL", 2), ("E-LIGHT-TASK", 6), ("E-LIGHT-TEXT", 3),
                        ("E-LIGHT-NOTE", 5), ("E-LIGHT-TABLE", 7), ("E-LIGHT-TABLE-TEXT", 3)):
        if name not in drawing.layers:
            drawing.layers.add(name, color=color)
    msp = drawing.modelspace()
    rooms = room_schedule()
    for room in rooms:
        for position in room.positions_mm:
            _draw_symbol(msp, room.luminaire_code, *position)
    for code, positions in TASK_POSITIONS.items():
        for position in positions:
            _draw_symbol(msp, code, *position)
    counts = Counter(room.luminaire_code for room in rooms for _ in range(room.quantity))
    counts.update({code: len(positions) for code, positions in TASK_POSITIONS.items()})
    _add_schedule_to_drawing(msp, rooms, counts)
    drawing.set_modelspace_vport(height=20000, center=(19500, 4500))
    output_dxf = output_dir / "house-150sqm-lighting-with-schedules-review.dxf"
    drawing.saveas(output_dxf)
    source_hash_after = _sha256(source)
    luminaire_schedule = [{**asdict(spec), "quantity": counts[code], "connected_w": counts[code] * spec.power_w} for code, spec in LUMINAIRES.items()]
    total_w = sum(item["connected_w"] for item in luminaire_schedule)
    result = {
        "schema_version": 1, "result": "lighting-lumen-method-review-only", "source": str(source),
        "source_sha256": source_hash_before, "source_unchanged": source_hash_before == source_hash_after,
        "output_dxf": str(output_dxf), "output_sha256": _sha256(output_dxf),
        "ceiling_height_m": CEILING_HEIGHT_M, "workplane_height_m": WORKPLANE_HEIGHT_M,
        "maintenance_factor": MF, "rooms": [asdict(room) for room in rooms],
        "luminaire_schedule": luminaire_schedule, "general_luminaires": sum(room.quantity for room in rooms),
        "task_luminaires": sum(len(value) for value in TASK_POSITIONS.values()), "total_luminaires": sum(counts.values()),
        "total_connected_w": total_w, "lighting_power_density_w_m2": round(total_w / 150, 2),
        "all_general_targets_met": all(room.achieved_lux >= room.target_lux for room in rooms),
        "all_spacing_checks_pass": all(room.spacing_check == "PASS" for room in rooms),
        "existing_drawing_mutation_performed": False,
        "limitations": [
            "Review design only; public sources did not expose a clause-level current Egyptian residential lux table.",
            "Targets are stated engineering design criteria aligned with the ISO/CIE indoor-lighting framework, pending licensed Egyptian-code confirmation.",
            "Point-by-point uniformity, UGR/glare, daylight, emergency lighting and controls are not verified by the lumen method.",
            "Final validation requires selected-product IES/LDT photometry, actual reflectances, bathroom-zone review and licensed electrical engineer approval.",
        ],
    }
    csv_path = output_dir / "house-150sqm-lighting-calculations.csv"
    with csv_path.open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.writer(stream)
        writer.writerow(("Room", "Area m2", "Target lux", "Type", "Lumens each", "CU", "MF", "Quantity", "Calculated maintained lux", "Connected W", "Spacing check", "Note"))
        for room in rooms:
            fixture = LUMINAIRES[room.luminaire_code]
            writer.writerow((room.room, room.area_m2, room.target_lux, room.luminaire_code, fixture.lumens, fixture.utilization_factor, MF, room.quantity, room.achieved_lux, room.quantity * fixture.power_w, room.spacing_check, room.note))
    result["calculation_csv"] = str(csv_path)
    json_path = output_dir / "house-150sqm-lighting-calculations.json"
    report_path = output_dir / "house-150sqm-lighting-review.md"
    json_path.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    report_path.write_text(_markdown(result), encoding="utf-8")
    result["calculation_json"] = str(json_path)
    result["review_report"] = str(report_path)
    return result


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    args = parser.parse_args()
    print(json.dumps(build(args.source, args.output_dir), ensure_ascii=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
