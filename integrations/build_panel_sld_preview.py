"""Create a deterministic, no-mutation SLD preview from schedule evidence."""

from __future__ import annotations

import argparse
import hashlib
import html
import json
import textwrap
from pathlib import Path
from typing import Any

from PIL import Image, ImageDraw, ImageFont


WIDTH = 1900
HEIGHT = 1050
MARGIN = 55
BUS_Y = 260
BREAKER_Y = 310
LOAD_Y = 455


def _canonical(value: Any) -> bytes:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")


def _sha256(value: Any) -> str:
    return hashlib.sha256(_canonical(value)).hexdigest()


def _font(size: int, *, bold: bool = False) -> ImageFont.FreeTypeFont | ImageFont.ImageFont:
    name = "arialbd.ttf" if bold else "arial.ttf"
    path = Path("C:/Windows/Fonts") / name
    return ImageFont.truetype(str(path), size) if path.exists() else ImageFont.load_default()


def build_preview(evidence: dict[str, Any], panel_name: str) -> dict[str, Any]:
    panel = next((item for item in evidence["schedule_panels"] if item["panel"] == panel_name), None)
    if panel is None:
        raise ValueError(f"Panel not found in evidence: {panel_name}")
    observed = [way for way in panel["ways"] if way["observed_in_lighting_drawing"]]
    geometry = []
    count = len(panel["ways"])
    spacing = (WIDTH - 2 * MARGIN) / max(count - 1, 1)
    for way in panel["ways"]:
        x = MARGIN + (int(way["way"]) - 1) * spacing
        geometry.append(
            {
                "way": int(way["way"]),
                "x": round(x, 3),
                "observed": bool(way["observed_in_lighting_drawing"]),
                "description": way.get("description"),
                "connected_w": way.get("connected_w"),
                "connected_va": way.get("connected_va"),
                "current_a": way.get("current_a"),
                "known_partial_w": way.get("known_partial_w"),
                "known_partial_va": way.get("known_partial_va"),
                "unresolved_load_count": way.get("unresolved_load_count", 0),
                "status": way.get("status"),
            }
        )
    payload: dict[str, Any] = {
        "schema_version": 1,
        "preview_type": "panel-single-line-diagram-draft",
        "panel": panel_name,
        "system": panel["system"],
        "frequency_hz": panel["frequency_hz"],
        "rated_ways": panel["rated_ways"],
        "source_evidence_sha256": evidence["result_sha256"],
        "source_drawing_sha256": evidence["source"]["source_drawing_sha256"],
        "way_count_shown": count,
        "observed_way_count": len(observed),
        "blank_way_count": count - len(observed),
        "assumptions": [
            "400/230 V, 3 Phase, 4 Wire is engineer-assumed.",
            "Incomer, busbar rating, phase assignment, protective devices, and cables remain TBD.",
            "Red numeric values are provisional estimates.",
            "Blank ways are not designated as spare.",
            "This is a no-mutation preview, not an approved construction drawing.",
        ],
        "geometry": geometry,
        "drawing_mutation_performed": False,
    }
    payload["preview_sha256"] = _sha256(payload)
    return payload


def _lines(value: str | None, width: int = 17, maximum: int = 4) -> list[str]:
    if not value:
        return []
    lines = textwrap.wrap(value, width=width, break_long_words=False)
    if len(lines) > maximum:
        lines = lines[:maximum]
        lines[-1] = lines[-1].rstrip(".; ") + "..."
    return lines


def _compact_load_lines(value: str | None, maximum: int = 5) -> list[str]:
    if not value:
        return []
    replacements = (
        ("Rectangular lighting fixture", "Rect. light"),
        ("Toilet spot lighting fixture", "Toilet light"),
        ("L03 spot lighting fixture", "L03 light"),
        ("L04 outlet spot lighting fixture", "L04 light"),
        ("Pendant lighting fixture", "Pendant"),
        ("Extract fan", "EF"),
        ("LED tape segment", "LED tape"),
        ("lighting fixture", "light"),
    )
    items = [item.strip() for item in value.split(";") if item.strip()]
    compact: list[str] = []
    for item in items:
        for source, target in replacements:
            item = item.replace(source, target)
        compact.extend(textwrap.wrap(item, width=14, break_long_words=False))
    if len(compact) > maximum:
        compact = compact[:maximum]
        compact[-1] = compact[-1].rstrip(".; ") + "..."
    return compact


def render_svg(preview: dict[str, Any]) -> str:
    center = WIDTH / 2
    parts = [
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{WIDTH}" height="{HEIGHT}" viewBox="0 0 {WIDTH} {HEIGHT}">',
        '<rect width="100%" height="100%" fill="white"/>',
        '<style>text{font-family:Arial,sans-serif;fill:#111827}.title{font-size:30px;font-weight:700}.head{font-size:18px;font-weight:700}.small{font-size:12px}.tiny{font-size:10px}.estimated{fill:#d00000;font-weight:700}.unknown{fill:#b45309}.blank{fill:#9ca3af}</style>',
        f'<text x="{center}" y="45" text-anchor="middle" class="title">Single Line Diagram - {html.escape(preview["panel"])}</text>',
        f'<text x="55" y="78" class="head">System: {html.escape(preview["system"])}</text>',
        '<text x="55" y="103" class="small unknown">Frequency / incomer / busbar / protection / cable / phase: TBD</text>',
        f'<line x1="{center}" y1="115" x2="{center}" y2="145" stroke="#111827" stroke-width="4"/>',
        f'<polygon points="{center-8},115 {center+8},115 {center},100" fill="#111827"/>',
        f'<rect x="{center-92}" y="145" width="184" height="55" fill="white" stroke="#111827" stroke-width="3"/>',
        f'<text x="{center}" y="168" text-anchor="middle" class="head">INCOMER</text>',
        f'<text x="{center}" y="190" text-anchor="middle" class="small unknown">RATING / TYPE TBD</text>',
        f'<line x1="{center}" y1="200" x2="{center}" y2="{BUS_Y}" stroke="#111827" stroke-width="4"/>',
        f'<line x1="{MARGIN}" y1="{BUS_Y}" x2="{WIDTH-MARGIN}" y2="{BUS_Y}" stroke="#111827" stroke-width="8"/>',
        f'<text x="{WIDTH-MARGIN}" y="{BUS_Y-42}" text-anchor="end" class="small unknown">BUSBAR RATING TBD</text>',
    ]
    for item in preview["geometry"]:
        x = float(item["x"])
        way = item["way"]
        observed = item["observed"]
        color = "#111827" if observed else "#b8bec7"
        dash = "" if observed else ' stroke-dasharray="5,5"'
        parts.extend(
            [
                f'<text x="{x}" y="{BUS_Y-15}" text-anchor="middle" class="small">{way}</text>',
                f'<line x1="{x}" y1="{BUS_Y}" x2="{x}" y2="{BREAKER_Y}" stroke="{color}" stroke-width="2"{dash}/>',
                f'<rect x="{x-31}" y="{BREAKER_Y}" width="62" height="38" fill="white" stroke="{color}" stroke-width="2"{dash}/>',
                f'<text x="{x}" y="{BREAKER_Y+24}" text-anchor="middle" class="tiny {"unknown" if observed else "blank"}">{"CB TBD" if observed else ""}</text>',
                f'<line x1="{x}" y1="{BREAKER_Y+38}" x2="{x}" y2="{LOAD_Y-20}" stroke="{color}" stroke-width="2"{dash}/>',
                f'<circle cx="{x}" cy="{LOAD_Y}" r="19" fill="white" stroke="{color}" stroke-width="2"{dash}/>',
                f'<text x="{x}" y="{LOAD_Y+5}" text-anchor="middle" class="small">{way if observed else ""}</text>',
            ]
        )
        if observed:
            y = LOAD_Y + 45
            for line in _compact_load_lines(item.get("description")):
                parts.append(f'<text x="{x}" y="{y}" text-anchor="middle" class="tiny">{html.escape(line)}</text>')
                y += 14
            if item.get("connected_w") is not None:
                parts.append(f'<text x="{x}" y="{y+5}" text-anchor="middle" class="small estimated">{item["connected_w"]:.1f} W</text>')
                parts.append(f'<text x="{x}" y="{y+22}" text-anchor="middle" class="small estimated">{item["connected_va"]:.1f} VA</text>')
                parts.append(f'<text x="{x}" y="{y+39}" text-anchor="middle" class="small estimated">{item["current_a"]:.3f} A</text>')
            elif item.get("known_partial_w") is not None:
                parts.append(f'<text x="{x}" y="{y+5}" text-anchor="middle" class="small estimated">KNOWN {item["known_partial_w"]:.1f} W</text>')
                parts.append(f'<text x="{x}" y="{y+22}" text-anchor="middle" class="small estimated">KNOWN {item["known_partial_va"]:.1f} VA</text>')
                parts.append(f'<text x="{x}" y="{y+39}" text-anchor="middle" class="small unknown">TOTAL TBD ({item.get("unresolved_load_count", 0)} loads)</text>')
            else:
                parts.append(f'<text x="{x}" y="{y+8}" text-anchor="middle" class="small unknown">LOAD TBD</text>')
            parts.append(f'<text x="{x}" y="{y+59}" text-anchor="middle" class="tiny unknown">PHASE / CABLE TBD</text>')
    parts.extend(
        [
            f'<rect x="55" y="900" width="1790" height="105" fill="#f8fafc" stroke="#94a3b8"/>',
            '<text x="75" y="930" class="head">DRAFT NOTES</text>',
            '<text x="75" y="955" class="small estimated">Red numbers = provisional estimated load values.</text>',
            '<text x="75" y="978" class="small unknown">TBD = not present in reviewed evidence. Blank ways are not Spares.</text>',
            f'<text x="75" y="1000" class="tiny">Preview SHA-256: {preview["preview_sha256"]}</text>',
            '</svg>',
        ]
    )
    return "".join(parts)


def render_png(preview: dict[str, Any], output: Path) -> None:
    image = Image.new("RGB", (WIDTH, HEIGHT), "white")
    draw = ImageDraw.Draw(image)
    navy, teal, gray, red, amber = "#10244c", "#386b7c", "#aeb5bf", "#d00000", "#b45309"
    draw.text((WIDTH / 2, 28), f"Single Line Diagram - {preview['panel']}", font=_font(30, bold=True), fill=navy, anchor="mm")
    draw.text((55, 70), f"System: {preview['system']}", font=_font(18, bold=True), fill="black")
    draw.text((55, 96), "Frequency / incomer / busbar / protection / cable / phase: TBD", font=_font(13), fill=amber)
    center = WIDTH / 2
    draw.line((center, 115, center, 145), fill=navy, width=4)
    draw.polygon(((center - 8, 115), (center + 8, 115), (center, 100)), fill=navy)
    draw.rectangle((center - 92, 145, center + 92, 200), outline=navy, width=3)
    draw.text((center, 166), "INCOMER", font=_font(18, bold=True), fill=navy, anchor="mm")
    draw.text((center, 189), "RATING / TYPE TBD", font=_font(12), fill=amber, anchor="mm")
    draw.line((center, 200, center, BUS_Y), fill=navy, width=4)
    draw.line((MARGIN, BUS_Y, WIDTH - MARGIN, BUS_Y), fill=navy, width=8)
    draw.text((WIDTH - MARGIN, BUS_Y - 42), "BUSBAR RATING TBD", font=_font(12), fill=amber, anchor="rs")
    for item in preview["geometry"]:
        x, way, observed = float(item["x"]), item["way"], item["observed"]
        color = navy if observed else gray
        draw.text((x, BUS_Y - 18), str(way), font=_font(12, bold=True), fill=navy, anchor="ms")
        draw.line((x, BUS_Y, x, BREAKER_Y), fill=color, width=2)
        draw.rectangle((x - 31, BREAKER_Y, x + 31, BREAKER_Y + 38), outline=color, width=2)
        if observed:
            draw.text((x, BREAKER_Y + 19), "CB TBD", font=_font(10), fill=amber, anchor="mm")
        draw.line((x, BREAKER_Y + 38, x, LOAD_Y - 20), fill=color, width=2)
        draw.ellipse((x - 19, LOAD_Y - 19, x + 19, LOAD_Y + 19), outline=color, width=2)
        if observed:
            draw.text((x, LOAD_Y), str(way), font=_font(12, bold=True), fill=navy, anchor="mm")
            y = LOAD_Y + 45
            for line in _compact_load_lines(item.get("description")):
                draw.text((x, y), line, font=_font(9), fill="black", anchor="ma")
                y += 14
            if item.get("connected_w") is not None:
                draw.text((x, y + 5), f"{item['connected_w']:.1f} W", font=_font(12, bold=True), fill=red, anchor="ma")
                draw.text((x, y + 22), f"{item['connected_va']:.1f} VA", font=_font(12, bold=True), fill=red, anchor="ma")
                draw.text((x, y + 39), f"{item['current_a']:.3f} A", font=_font(12, bold=True), fill=red, anchor="ma")
            elif item.get("known_partial_w") is not None:
                draw.text((x, y + 5), f"KNOWN {item['known_partial_w']:.1f} W", font=_font(12, bold=True), fill=red, anchor="ma")
                draw.text((x, y + 22), f"KNOWN {item['known_partial_va']:.1f} VA", font=_font(12, bold=True), fill=red, anchor="ma")
                draw.text((x, y + 39), f"TOTAL TBD ({item.get('unresolved_load_count', 0)} loads)", font=_font(11, bold=True), fill=amber, anchor="ma")
            else:
                draw.text((x, y + 8), "LOAD TBD", font=_font(12, bold=True), fill=amber, anchor="ma")
            draw.text((x, y + 59), "PHASE / CABLE TBD", font=_font(9), fill=amber, anchor="ma")
    draw.rectangle((55, 900, 1845, 1005), fill="#f8fafc", outline="#94a3b8", width=1)
    draw.text((75, 920), "DRAFT NOTES", font=_font(18, bold=True), fill=navy)
    draw.text((75, 950), "Red numbers = provisional estimated load values.", font=_font(13), fill=red)
    draw.text((75, 974), "TBD = not present in reviewed evidence. Blank ways are not Spares.", font=_font(13), fill=amber)
    draw.text((75, 995), f"Preview SHA-256: {preview['preview_sha256']}", font=_font(10), fill=teal)
    output.parent.mkdir(parents=True, exist_ok=True)
    image.save(output)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--evidence", type=Path, required=True)
    parser.add_argument("--panel", required=True)
    parser.add_argument("--output-json", type=Path, required=True)
    parser.add_argument("--output-svg", type=Path, required=True)
    parser.add_argument("--output-png", type=Path, required=True)
    args = parser.parse_args()
    evidence = json.loads(args.evidence.read_text(encoding="utf-8"))
    preview = build_preview(evidence, args.panel)
    args.output_json.parent.mkdir(parents=True, exist_ok=True)
    args.output_json.write_bytes(_canonical(preview) + b"\n")
    args.output_svg.write_text(render_svg(preview), encoding="utf-8")
    render_png(preview, args.output_png)
    print(json.dumps({"panel": args.panel, "preview_sha256": preview["preview_sha256"], "drawing_mutation_performed": False}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
