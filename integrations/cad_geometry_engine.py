"""Universal CAD Geometry Engine for Natural Language Drafting in AutoCAD.

Interprets natural language drawing requests (electrical blocks, circles, squares,
rectangles, lines, composite shapes, rooms, apartments, MEP layouts) and outputs
clean, reviewed DXF geometry. Supports interactive engineering clarification when
parameters are missing, and integrates with Gemini AI for composite or custom CAD
drafting.
"""

from __future__ import annotations

import argparse
import json
import math
import os
import re
import sys
import time
from pathlib import Path
from typing import Any

# Force UTF-8 streams even when launched from GUI processes without PYTHONUTF8=1
if hasattr(sys.stdout, "reconfigure"):
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
if hasattr(sys.stderr, "reconfigure"):
    try:
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

# Ensure project root is in sys.path
ROOT = Path(__file__).resolve().parent.parent
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

import ezdxf
from ezdxf.enums import TextEntityAlignment

SESSION_STATE_FILE = ROOT / "data" / "cad_drawing_session.json"
_SESSION_TTL_SECONDS = 300.0


def _parse_unit_scale(unit_str: str | None) -> float:
    if not unit_str:
        return 10.0  # default cm -> mm
    u = unit_str.lower().strip()
    if u in ("متر", "م", "m", "meter", "meters"):
        return 1000.0
    if u in ("سم", "cm", "centimeter", "centimeters"):
        return 10.0
    if u in ("مم", "mm", "millimeter", "millimeters"):
        return 1.0
    return 10.0


def _load_pending_context() -> str | None:
    try:
        if SESSION_STATE_FILE.exists():
            data = json.loads(SESSION_STATE_FILE.read_text(encoding="utf-8"))
            ts = float(data.get("timestamp", 0.0))
            if ts and (time.time() - ts) > _SESSION_TTL_SECONDS:
                _clear_pending_context()
                return None
            if data.get("state") == "awaiting_clarification":
                return data.get("original_prompt")
    except Exception:
        pass
    return None


def _save_pending_context(original_prompt: str, entity_type: str = "") -> None:
    try:
        SESSION_STATE_FILE.parent.mkdir(parents=True, exist_ok=True)
        SESSION_STATE_FILE.write_text(
            json.dumps({
                "state": "awaiting_clarification",
                "original_prompt": original_prompt,
                "entity_type": entity_type,
                "timestamp": time.time(),
            }, ensure_ascii=False),
            encoding="utf-8",
        )
    except Exception:
        pass


def _clear_pending_context() -> None:
    try:
        if SESSION_STATE_FILE.exists():
            SESSION_STATE_FILE.unlink()
    except Exception:
        pass


def _starts_new_command(raw_text: str) -> bool:
    t = raw_text.strip().lower()
    return bool(
        re.search(
            r"(?:^|\s)(?:ارسم|ترسم|نرسم|صمم|تصمم|وزع|توزع|توزيع|عايز ارسم|عايز أرسم|draw\b|create\b|draft\b)",
            t,
        )
    )


def _ask_user(prompt: str, question: str, output_dir: Path, entity_type: str = "") -> dict[str, object]:
    """Save pending context and return clarification question with a fallback preview DXF."""
    _save_pending_context(prompt, entity_type)
    output_dir.mkdir(parents=True, exist_ok=True)
    doc = ezdxf.new("R2010", setup=True)
    doc.header["$INSUNITS"] = 4
    layer = "_AI_PREVIEW_REVIEW"
    doc.layers.add(layer, color=1)
    msp = doc.modelspace()
    txt = msp.add_text(
        "AI-ENGINEER: بانتظار توضيح المهندس للأبعاد في الشات...",
        dxfattribs={"layer": layer, "height": 50},
    )
    txt.set_placement((0, 0), align=TextEntityAlignment.MIDDLE_CENTER)
    out_file = output_dir / "clarification_prompt.dxf"
    doc.saveas(str(out_file))

    return {
        "status": "ask_user",
        "question": question,
        "description": question,
        "dxf": str(out_file),
    }


def _generate_electrical_blocks(text: str, output_dir: Path) -> dict[str, object] | None:
    """Generate AutoCAD Blocks for lamps, sockets, switches requested by user."""
    has_lamp = any(k in text for k in ("لمبة", "لمبه", "كشاف", "lamp", "spotlight"))
    has_socket = any(k in text for k in ("فيش", "بريزة", "برايز", "socket", "receptacle"))
    has_switch = any(k in text for k in ("مفتاح", "سويتش", "switch"))

    if not (has_lamp or has_socket or has_switch):
        return None

    # Exclude full apartment electrical distribution
    if any(k in text for k in ("وزع", "توزيع", "شقة", "شقه")) and not any(k in text for k in ("بلوك", "بلوكات", "رمز", "رموز", "block")):
        return None

    doc = ezdxf.new("R2010", setup=True)
    doc.header["$INSUNITS"] = 4  # mm
    review_layer = "_AI_PREVIEW_REVIEW"
    doc.layers.add(review_layer, color=1)
    doc.layers.add("E-LITE", color=2)
    doc.layers.add("E-POWR", color=3)
    doc.layers.add("A-TEXT", color=4)

    msp = doc.modelspace()
    items_drawn = []
    current_x = 0.0

    if has_lamp:
        v_match = re.search(r"(\d+)\s*(?:فولت|v|volt)", text)
        voltage = v_match.group(1) if v_match else "220"
        w_match = re.search(r"(\d+)\s*(?:وات|w|watt)", text)
        wattage = w_match.group(1) if w_match else "100"

        blk_name = f"BLOCK_LAMP_{voltage}V_{wattage}W"
        if blk_name not in doc.blocks:
            blk = doc.blocks.new(name=blk_name)
            blk.add_circle((0, 0), 150, dxfattribs={"layer": "E-LITE"})
            cross_r = 106.0
            blk.add_line((-cross_r, -cross_r), (cross_r, cross_r), dxfattribs={"layer": "E-LITE"})
            blk.add_line((-cross_r, cross_r), (cross_r, -cross_r), dxfattribs={"layer": "E-LITE"})
            txt = blk.add_text(f"{voltage}V {wattage}W", dxfattribs={"layer": "A-TEXT", "height": 25})
            txt.set_placement((0, -190), align=TextEntityAlignment.TOP_CENTER)

        msp.add_blockref(blk_name, (current_x, 0), dxfattribs={"layer": review_layer})
        lbl = msp.add_text(f"بلوك لمبة إنارة ({voltage}V {wattage}W)", dxfattribs={"layer": review_layer, "height": 35})
        lbl.set_placement((current_x, 220), align=TextEntityAlignment.BOTTOM_CENTER)

        items_drawn.append(f"• بلوك لمبة إنارة {voltage} فولت {wattage} وات (اسم البلوك: {blk_name} - طبقة E-LITE)")
        current_x += 700.0

    if has_socket:
        is_duplex = any(k in text for k in ("مزدوج", "مزوج", "دوبلكس", "duplex", "double", "مزدوجة", "مزوجه"))
        v_match = re.search(r"(\d+)\s*(?:فولت|v|volt)", text)
        voltage = v_match.group(1) if v_match else "220"

        if is_duplex:
            blk_name = f"BLOCK_SOCKET_DUPLEX_{voltage}V"
            if blk_name not in doc.blocks:
                blk = doc.blocks.new(name=blk_name)
                blk.add_arc((0, 0), 120, 0, 180, dxfattribs={"layer": "E-POWR"})
                blk.add_line((-120, 0), (120, 0), dxfattribs={"layer": "E-POWR"})
                blk.add_line((-40, 0), (-40, 100), dxfattribs={"layer": "E-POWR"})
                blk.add_line((40, 0), (40, 100), dxfattribs={"layer": "E-POWR"})
                txt = blk.add_text(f"DUPLEX 2x16A {voltage}V", dxfattribs={"layer": "A-TEXT", "height": 25})
                txt.set_placement((0, -30), align=TextEntityAlignment.TOP_CENTER)

            msp.add_blockref(blk_name, (current_x, 0), dxfattribs={"layer": review_layer})
            lbl = msp.add_text(f"بلوك فيش مزدوج ({voltage}V 2x16A)", dxfattribs={"layer": review_layer, "height": 35})
            lbl.set_placement((current_x, 220), align=TextEntityAlignment.BOTTOM_CENTER)
            items_drawn.append(f"• بلوك فيش مزدوج {voltage}V 16A (اسم البلوك: {blk_name} - طبقة E-POWR)")
        else:
            blk_name = f"BLOCK_SOCKET_SINGLE_{voltage}V"
            if blk_name not in doc.blocks:
                blk = doc.blocks.new(name=blk_name)
                blk.add_arc((0, 0), 120, 0, 180, dxfattribs={"layer": "E-POWR"})
                blk.add_line((-120, 0), (120, 0), dxfattribs={"layer": "E-POWR"})
                blk.add_line((0, 0), (0, 100), dxfattribs={"layer": "E-POWR"})
                txt = blk.add_text(f"16A {voltage}V", dxfattribs={"layer": "A-TEXT", "height": 25})
                txt.set_placement((0, -30), align=TextEntityAlignment.TOP_CENTER)

            msp.add_blockref(blk_name, (current_x, 0), dxfattribs={"layer": review_layer})
            lbl = msp.add_text(f"بلوك فيش مفرد ({voltage}V 16A)", dxfattribs={"layer": review_layer, "height": 35})
            lbl.set_placement((current_x, 220), align=TextEntityAlignment.BOTTOM_CENTER)
            items_drawn.append(f"• بلوك فيش مفرد {voltage}V 16A (اسم البلوك: {blk_name} - طبقة E-POWR)")
        current_x += 700.0

    if has_switch:
        blk_name = "BLOCK_SWITCH_1WAY_10A"
        if blk_name not in doc.blocks:
            blk = doc.blocks.new(name=blk_name)
            blk.add_circle((0, 0), 80, dxfattribs={"layer": "E-LITE"})
            blk.add_line((0, 0), (120, 120), dxfattribs={"layer": "E-LITE"})
            blk.add_line((120, 120), (160, 120), dxfattribs={"layer": "E-LITE"})
            txt = blk.add_text("1-WAY 10A", dxfattribs={"layer": "A-TEXT", "height": 22})
            txt.set_placement((0, -110), align=TextEntityAlignment.TOP_CENTER)

        msp.add_blockref(blk_name, (current_x, 0), dxfattribs={"layer": review_layer})
        lbl = msp.add_text("بلوك مفتاح إنارة 1-Way 10A", dxfattribs={"layer": review_layer, "height": 35})
        lbl.set_placement((current_x, 220), align=TextEntityAlignment.BOTTOM_CENTER)
        items_drawn.append(f"• بلوك مفتاح إنارة (اسم البلوك: {blk_name} - طبقة E-LITE)")
        current_x += 700.0

    total_w = max(700.0, current_x - 700.0)
    hdr = msp.add_text("ELECTRICAL BLOCKS LIBRARY", dxfattribs={"layer": review_layer, "height": 45})
    hdr.set_placement((total_w / 2.0, 380), align=TextEntityAlignment.BOTTOM_CENTER)

    out_file = output_dir / "custom_electrical_blocks.dxf"
    doc.saveas(str(out_file))

    desc = (
        "تم رسم وتوليد البلوكات والرموز الكهربائية المطلوبة بنجاح داخل أوتوكاد وحفظها كـ Blocks قابلة لإعادة الاستخدام:\n"
        + "\n".join(items_drawn)
        + "\n💡 يمكنك إعادة إدراج أي بلوك منها لاحقاً عبر أمر INSERT."
    )
    return {"status": "success", "dxf": str(out_file), "description": desc}


def _render_entities_to_dxf(entities: list[dict[str, Any]], output_dir: Path, filename: str = "gemini_generated_geometry.dxf") -> Path:
    doc = ezdxf.new("R2010", setup=True)
    doc.header["$INSUNITS"] = 4
    msp = doc.modelspace()
    layer = "_AI_PREVIEW_REVIEW"
    doc.layers.add(layer, color=1)
    doc.layers.add("A-CENTER", color=2, linetype="CENTER")
    doc.layers.add("A-TEXT", color=4)

    for ent in entities:
        if not isinstance(ent, dict):
            continue
        etype = str(ent.get("type", "")).lower().strip()
        label = ent.get("label")
        if etype == "circle":
            cx, cy = ent.get("center", [0, 0])[:2]
            if "radius" in ent and ent["radius"] is not None:
                r = float(ent["radius"])
            elif "diameter" in ent and ent["diameter"] is not None:
                r = float(ent["diameter"]) / 2.0
            else:
                r = 100.0
            msp.add_circle((float(cx), float(cy)), r, dxfattribs={"layer": layer})
            mark = max(15.0, r * 0.35)
            msp.add_line((float(cx) - mark, float(cy)), (float(cx) + mark, float(cy)), dxfattribs={"layer": "A-CENTER"})
            msp.add_line((float(cx), float(cy) - mark), (float(cx), float(cy) + mark), dxfattribs={"layer": "A-CENTER"})
            if label:
                t = msp.add_text(str(label), dxfattribs={"layer": "A-TEXT", "height": max(20.0, r * 0.25)})
                t.set_placement((float(cx), float(cy) + r + 25.0), align=TextEntityAlignment.BOTTOM_CENTER)
        elif etype in ("rect", "rectangle", "square"):
            if "bounds" in ent and isinstance(ent["bounds"], (list, tuple)) and len(ent["bounds"]) >= 4:
                x, y, w, h = [float(v) for v in ent["bounds"][:4]]
            else:
                w = float(ent.get("width", ent.get("size", ent.get("side", 500))))
                h = float(ent.get("height", ent.get("size", ent.get("side", w))))
                if "x" not in ent and "y" not in ent and isinstance(ent.get("center"), (list, tuple)) and len(ent["center"]) >= 2:
                    x = float(ent["center"][0]) - w / 2.0
                    y = float(ent["center"][1]) - h / 2.0
                else:
                    x = float(ent.get("x", 0))
                    y = float(ent.get("y", 0))
            msp.add_lwpolyline([(x, y), (x + w, y), (x + w, y + h), (x, y + h)], close=True, dxfattribs={"layer": layer})
            if label:
                t = msp.add_text(str(label), dxfattribs={"layer": "A-TEXT", "height": max(20.0, min(w, h) * 0.08)})
                t.set_placement((x + w / 2.0, y + h + 25.0), align=TextEntityAlignment.BOTTOM_CENTER)
        elif etype == "line":
            start = ent.get("start", [0, 0])[:2]
            end = ent.get("end", [100, 0])[:2]
            sx, sy = float(start[0]), float(start[1])
            ex, ey = float(end[0]), float(end[1])
            msp.add_line((sx, sy), (ex, ey), dxfattribs={"layer": layer})
            if label:
                t = msp.add_text(str(label), dxfattribs={"layer": "A-TEXT", "height": 25.0})
                t.set_placement(((sx + ex) / 2.0, (sy + ey) / 2.0 + 25.0), align=TextEntityAlignment.BOTTOM_CENTER)
        elif etype in ("polyline", "polygon"):
            pts = ent.get("points", [])
            if isinstance(pts, list) and len(pts) >= 2:
                clean_pts = [(float(p[0]), float(p[1])) for p in pts if isinstance(p, (list, tuple)) and len(p) >= 2]
                if len(clean_pts) >= 2:
                    msp.add_lwpolyline(clean_pts, close=bool(ent.get("closed", True)), dxfattribs={"layer": layer})
                    if label:
                        cx = sum(p[0] for p in clean_pts) / len(clean_pts)
                        cy = max(p[1] for p in clean_pts) + 25.0
                        t = msp.add_text(str(label), dxfattribs={"layer": "A-TEXT", "height": 25.0})
                        t.set_placement((cx, cy), align=TextEntityAlignment.BOTTOM_CENTER)
        elif etype == "arc":
            cx, cy = ent.get("center", [0, 0])[:2]
            r = float(ent.get("radius", 100))
            a1 = float(ent.get("start_angle", 0))
            a2 = float(ent.get("end_angle", 180))
            msp.add_arc((float(cx), float(cy)), r, a1, a2, dxfattribs={"layer": layer})
        elif etype == "text":
            txt = str(ent.get("text", ""))
            pos = ent.get("position", [0, 0])[:2]
            if txt:
                t = msp.add_text(txt, dxfattribs={"layer": "A-TEXT", "height": float(ent.get("height", 30))})
                t.set_placement((float(pos[0]), float(pos[1])))

    out_file = output_dir / filename
    doc.saveas(str(out_file))
    return out_file


def _call_gemini_cad_generator(prompt: str, output_dir: Path) -> dict[str, object]:
    """Call Gemini generateContent API for multi-shape, colloquial, or custom CAD drafting."""
    api_key = os.environ.get("GEMINI_API_KEY", "").strip()
    if not api_key:
        return {"status": "error", "error": "GEMINI_API_KEY environment variable is not set."}
    model = os.environ.get("AI_ENGINEER_GEMINI_MODEL", "gemini-3.5-flash-lite").strip() or "gemini-3.5-flash-lite"

    import urllib.request

    url = f"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={api_key}"
    system_prompt = (
        "You are an expert AutoCAD CAD geometry planner (all coordinates and dimensions MUST be in millimeters: 1 cm = 10 mm, 1 m = 1000 mm).\n"
        "Understand Arabic engineering terms AND typos/colloquial dialect (for example: 'طول طلعة' or 'طول ضلعه' = square side length; 'وجنبها' = place adjacent on the right with a clean visible gap so shapes do NOT overlap).\n"
        "If the user's drawing request lacks essential dimensions or critical details to draw accurately, respond ONLY with JSON:\n"
        '{"status": "ask_user", "question": "<polite clear Arabic question asking the engineer for the missing dimensions/specs>"}\n'
        "If the request has enough information to draw, respond ONLY with JSON:\n"
        '{"status": "success", "description": "<clear Arabic summary of exact shapes and dimensions drawn>", "entities": [\n'
        '  {"type": "circle", "center": [x, y], "radius": r, "label": "optional short label"},\n'
        '  {"type": "rect", "x": x, "y": y, "width": w, "height": h, "label": "optional short label"},\n'
        '  {"type": "line", "start": [x1, y1], "end": [x2, y2], "label": "optional short label"},\n'
        '  {"type": "polyline", "points": [[x1, y1], ...], "closed": true, "label": "optional short label"},\n'
        '  {"type": "arc", "center": [x, y], "radius": r, "start_angle": a1, "end_angle": a2},\n'
        '  {"type": "text", "position": [x, y], "text": "...", "height": 30}\n'
        "]}\n\n"
        f"Engineer request:\n{prompt}"
    )

    body = {
        "contents": [{"parts": [{"text": system_prompt}]}],
        "generationConfig": {"responseMimeType": "application/json"},
    }
    encoded = json.dumps(body, ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(
        url,
        data=encoded,
        headers={"Content-Type": "application/json", "Accept": "application/json"},
    )
    try:
        with urllib.request.urlopen(req, timeout=20) as resp:
            data = json.loads(resp.read().decode("utf-8"))
            text_resp = (
                data.get("candidates", [{}])[0]
                .get("content", {})
                .get("parts", [{}])[0]
                .get("text", "")
            )
            parsed = json.loads(text_resp)
            if parsed.get("status") == "ask_user":
                return _ask_user(
                    prompt,
                    parsed.get("question", "يرجى توضيح الأبعاد والمقاسات المطلوبة للرسم."),
                    output_dir,
                )
            if parsed.get("status") == "success" and isinstance(parsed.get("entities"), list) and parsed["entities"]:
                out_file = _render_entities_to_dxf(parsed["entities"], output_dir)
                _clear_pending_context()
                return {
                    "status": "success",
                    "dxf": str(out_file),
                    "description": parsed.get("description", "تم رسم الأشكال المطلوبة بنجاح بلون المراجعة الأحمر (_AI_PREVIEW_REVIEW)."),
                }
    except Exception:
        pass

    # Deterministic multi-shape offline fallback (e.g. circle + square) if API unavailable
    lower = prompt.lower()
    circle_m = re.search(r"(?:ب?قطر(?:ها|ه)?|ب?القطر)\D*?(\d+(?:\.\d+)?)\s*(سم|متر|مم|cm|m|mm)?", lower)
    square_m = re.search(r"مربع.*?(?:ضلع|ضلعه|طلعة|طول)\D*?(\d+(?:\.\d+)?)\s*(سم|متر|مم|cm|m|mm)?", lower)
    if circle_m and square_m:
        r = (float(circle_m.group(1)) * _parse_unit_scale(circle_m.group(2))) / 2.0
        s = float(square_m.group(1)) * _parse_unit_scale(square_m.group(2))
        entities = [
            {"type": "circle", "center": [0.0, 0.0], "radius": r, "label": f"D={circle_m.group(1)}{circle_m.group(2) or 'سم'}"},
            {"type": "rect", "x": r + 200.0, "y": -s / 2.0, "width": s, "height": s, "label": f"Square {square_m.group(1)}x{square_m.group(1)}"},
        ]
        out_file = _render_entities_to_dxf(entities, output_dir, "custom_composite_geometry.dxf")
        _clear_pending_context()
        return {
            "status": "success",
            "dxf": str(out_file),
            "description": f"تم رسم دائرة بقطر {circle_m.group(1)} {circle_m.group(2) or 'سم'} وبجانبها مربع طول ضلعه {square_m.group(1)} {square_m.group(2) or 'سم'} بنجاح بلون المراجعة الأحمر (_AI_PREVIEW_REVIEW).",
        }

    return _ask_user(
        prompt,
        (
            f"يا باشمهندس، لتنفيذ رسم «{prompt}» بدقة طبقاً للأصول الهندسية، "
            "برجاء توضيح الأبعاد أو المقاسات المطلوبة (مثلاً: القطر، الأبعاد بالمتر/السنتيمتر، أو طريقة التوزيع) وسأقوم برسمها فوراً داخل أوتوكاد."
        ),
        output_dir,
    )


def parse_and_generate(raw_prompt: str, output_dir: Path) -> dict[str, object]:
    output_dir.mkdir(parents=True, exist_ok=True)

    # Only combine with previous clarification if user is answering, NOT starting a brand new command
    pending = _load_pending_context()
    if pending and not _starts_new_command(raw_prompt):
        prompt = f"{pending} {raw_prompt}"
    else:
        if pending and _starts_new_command(raw_prompt):
            _clear_pending_context()
        prompt = raw_prompt

    text = prompt.strip().lower()

    # Detect how many geometry families appear so composite prompts go to multi-shape planner
    has_circle = any(k in text for k in ("دوائر", "دائرة", "دايرة", "circle", "circles"))
    has_square = any(k in text for k in ("مربع", "square"))
    has_rect = any(k in text for k in ("مستطيل", "rectangle"))
    has_line = bool(re.search(r"(?:^|\s)خط(?:\s|$)|\bline\b", text))
    has_other_shape = any(k in text for k in ("مثلث", "triangle", "سداسي", "خماسي", "معين", "خزان", "سلم", "قاعدة", "عمود", "قوس", "arc"))
    shape_families = int(has_circle) + int(has_square or has_rect) + int(has_line) + int(has_other_shape)

    # 1. Electrical Blocks (Lamp, Socket, Switch) - if not mixed with generic geometric shapes
    if shape_families == 0:
        blk_res = _generate_electrical_blocks(text, output_dir)
        if blk_res is not None:
            _clear_pending_context()
            return blk_res

    # 2. Full Apartment Electrical MEP Layout Distribution
    if any(k in text for k in ("افياش", "انارة", "إنارة", "كهرباء", "برايز", "توزيع كهربائي", "electrical", "lighting", "sockets")) and any(k in text for k in ("شقة", "شقه", "مخطط", "المخطط", "توزيع", "وزع")):
        from integrations.build_house_150sqm_electrical import build_electrical
        res = build_electrical(output_dir)
        _clear_pending_context()
        return {
            "status": "success",
            "dxf": res["dxf"],
            "description": (
                "تم توزيع شبكة الإنارة والأفياش والمفاتيح ولوحة التوزيع الرئيسية (Main DB) على مخطط الشقة 150 م² بنجاح!\n"
                f"• عدد وحدات الإنارة: {res['total_luminaires']} وحدة\n"
                f"• عدد مخارج الأفياش والقوى: {res['total_sockets']} مخرج\n"
                f"• اللوحة الرئيسية: {res['main_db']}\n"
                f"• الحمل التصميمي التقديري: {res['connected_load_estimate_kva']} kVA"
            ),
        }

    # 3. Full Apartment / House Floor Plan
    if any(k in text for k in ("شقة", "شقه", "منزل", "بيت", "apartment", "house", "فيلا")) and shape_families == 0 and not any(k in text for k in ("بلوك",)):
        area_m = re.search(r"(\d+(?:\.\d+)?)\s*(?:متر\s*مربع|م2|م²|sqm|m2|متر|م\b)", text)
        has_room_details = any(k in text for k in ("غرف", "غرفة", "غرفه", "حمام", "مطبخ", "استقبال", "صالة", "صاله", "room", "bedroom"))
        if not area_m and not has_room_details and not re.search(r"\d+", text):
            return _ask_user(
                prompt,
                "يا باشمهندس، لتصميم مخطط الشقة بدقة، كم المساحة المطلوبة (مثلاً 150 م²)، وكم عدد الغرف والحمامات المطلوبة، وما هو سمك الحوائط (مثلاً 20 سم)؟",
                output_dir,
                "apartment",
            )
        if area_m and abs(float(area_m.group(1)) - 150.0) > 0.5:
            return _call_gemini_cad_generator(prompt, output_dir)
        from integrations.build_house_150sqm_preview import build
        res = build(output_dir)
        _clear_pending_context()
        return {
            "status": "success",
            "dxf": res["output_dxf"],
            "description": (
                "تم رسم وتوليد مخطط الشقة مساحة 150 م² بنجاح بلون المراجعة الأحمر (_AI_PREVIEW_REVIEW) طبقاً للكود المصري:\n"
                "• صالة استقبال: 24.8 م² (6.20×4.00م) - نسبة تهوية 13.1%\n"
                "• 3 غرف نوم بمساحات (18.3، 18.7، 18.3 م²)\n"
                "• مطبخ 12.0 م² وحمام 8.4 م²\n"
                "• سماكة الحوائط: 20 سم بكامل المحيط والقواطع"
            ),
        }

    # 4. Composite / Multi-shape OR Custom shapes -> Route straight to Gemini CAD Geometry Planner
    if shape_families > 1 or has_other_shape:
        return _call_gemini_cad_generator(prompt, output_dir)

    # 5. Single Shape: Circle or Array of Circles
    if has_circle:
        dia_m = re.search(r"(?:ب?قطر(?:ها|ه)?|ب?القطر)(?:\s*كل\s*واحدة)?(?:\s*=|\s*:|\s+هو|\s+يساوي)?\s*(\d+(?:\.\d+)?)\s*(سم|متر|مم|cm|m|mm)?", text)
        radius_m = re.search(r"نصف\s*(?:قطر(?:ها|ه)?|القطر)(?:\s*=|\s*:|\s+هو)?\s*(\d+(?:\.\d+)?)\s*(سم|متر|مم|cm|m|mm)?", text)
        standalone_dim_m = re.search(r"(?<!\S)(\d+(?:\.\d+)?)\s*(سم|متر|مم|cm|m|mm)\b", text)
        extra_num_m = None
        for nm in re.finditer(r"(\d+(?:\.\d+)?)\s*(سم|متر|مم|cm|m|mm)?(?!\s*(?:دوائر|دائرة|دايرة|circles?))", text):
            if not re.match(r"^\s*(?:دوائر|دائرة|دايرة|circles?)", text[nm.end():]):
                extra_num_m = nm
                break

        if not dia_m and not radius_m and not standalone_dim_m and not extra_num_m:
            return _ask_user(
                prompt,
                "يا باشمهندس، لتنفيذ رسم الدوائر بدقة، ما هو القطر المطلوب لكل دائرة وكم دائرة تود رسمها؟ (مثلاً: دائرة بقطر 20 سم، أو 5 دوائر قطر 30 سم والمسافة 80 سم).",
                output_dir,
                "circle",
            )

        count_m = re.search(r"(\d+)\s*(?:دوائر|دائرة|دايرة|circles?)", text)
        count = int(count_m.group(1)) if count_m else 1

        if dia_m:
            val = float(dia_m.group(1))
            scale = _parse_unit_scale(dia_m.group(2))
            radius = (val * scale) / 2.0
            dia_display = val
            dia_unit = dia_m.group(2) or "سم"
        elif radius_m:
            val = float(radius_m.group(1))
            scale = _parse_unit_scale(radius_m.group(2))
            radius = val * scale
            dia_display = val * 2.0
            dia_unit = radius_m.group(2) or "سم"
        elif standalone_dim_m:
            val = float(standalone_dim_m.group(1))
            scale = _parse_unit_scale(standalone_dim_m.group(2))
            radius = (val * scale) / 2.0
            dia_display = val
            dia_unit = standalone_dim_m.group(2) or "سم"
        else:
            val = float(extra_num_m.group(1))
            scale = _parse_unit_scale(extra_num_m.group(2))
            radius = (val * scale) / 2.0
            dia_display = val
            dia_unit = extra_num_m.group(2) or "سم"

        spacing_m = re.search(r"(?:مسافة|مسافه|تباعد|بينهم|مركزهم)\s*(?:بين(?:هم|مراكزهم)|مركزهم)?\s*(\d+(?:\.\d+)?)\s*(سم|متر|مم|cm|m|mm)?", text)
        if spacing_m:
            s_val = float(spacing_m.group(1))
            s_scale = _parse_unit_scale(spacing_m.group(2))
            spacing = s_val * s_scale
            spacing_display = s_val
            spacing_unit = spacing_m.group(2) or "سم"
        else:
            spacing = radius * 4.0
            spacing_display = spacing / 10.0
            spacing_unit = "سم"

        is_vert = any(k in text for k in ("راسي", "رأسي", "عمودي", "vertical"))

        doc = ezdxf.new("R2010", setup=True)
        doc.header["$INSUNITS"] = 4
        msp = doc.modelspace()
        layer = "_AI_PREVIEW_REVIEW"
        doc.layers.add(layer, color=1)
        doc.layers.add("A-CENTER", color=2, linetype="CENTER")
        doc.layers.add("A-TEXT", color=3)

        for i in range(count):
            cx = 0.0 if is_vert else i * spacing
            cy = i * spacing if is_vert else 0.0
            msp.add_circle((cx, cy), radius, dxfattribs={"layer": layer})
            mark = radius * 0.4
            msp.add_line((cx - mark, cy), (cx + mark, cy), dxfattribs={"layer": "A-CENTER"})
            msp.add_line((cx, cy - mark), (cx, cy + mark), dxfattribs={"layer": "A-CENTER"})
            lbl = msp.add_text(f"C{i+1} (D={dia_display:g}{dia_unit})", dxfattribs={"layer": "A-TEXT", "height": max(20, radius * 0.3)})
            lbl.set_placement((cx, cy + radius + 30), align=TextEntityAlignment.BOTTOM_CENTER)

        if count > 1:
            total_len = (count - 1) * spacing
            ext = radius * 1.5
            if is_vert:
                msp.add_line((0, -ext), (0, total_len + ext), dxfattribs={"layer": "A-CENTER", "linetype": "CENTER"})
            else:
                msp.add_line((-ext, 0), (total_len + ext, 0), dxfattribs={"layer": "A-CENTER", "linetype": "CENTER"})

        out_file = output_dir / "custom_geometry.dxf"
        doc.saveas(str(out_file))
        _clear_pending_context()

        dir_desc = "رأسي" if is_vert else "أفقي"
        desc = (
            f"تم رسم {count} دوائر بنجاح داخل أوتوكاد بلون المراجعة الأحمر (_AI_PREVIEW_REVIEW):\n"
            f"• قطر كل دائرة: {dia_display:g} {dia_unit} (نصف القطر = {radius:g} مم)\n"
            f"• المسافة بين المراكز: {spacing_display:g} {spacing_unit} ({spacing:g} مم)\n"
            f"• التوزيع: على خط واحد {dir_desc} مع إضافة خطوط المحاور (Centerlines)"
        )
        return {"status": "success", "dxf": str(out_file), "description": desc}

    # 6. Single Shape: Square / Rectangle
    if has_square or has_rect:
        rect_m = re.search(r"(\d+(?:\.\d+)?)\s*(سم|متر|مم|cm|m|mm)?\s*(?:في|x|\*)\s*(\d+(?:\.\d+)?)\s*(سم|متر|مم|cm|m|mm)?", text)
        sq_m = re.search(r"(?:ضلع|ضلعه|طلعة|طول|بضلع|طوله)?\s*(\d+(?:\.\d+)?)\s*(سم|متر|مم|cm|m|mm)?", text) if has_square else None

        if rect_m:
            w_val = float(rect_m.group(1))
            width = w_val * _parse_unit_scale(rect_m.group(2))
            h_val = float(rect_m.group(3))
            height = h_val * _parse_unit_scale(rect_m.group(4) or rect_m.group(2))
            label = f"RECTANGLE {w_val:g}x{h_val:g}"
            desc = f"تم رسم مستطيل بأبعاد {w_val:g} × {h_val:g} بلون المراجعة الأحمر (_AI_PREVIEW_REVIEW)."
        elif sq_m and sq_m.group(1):
            s_val = float(sq_m.group(1))
            unit = sq_m.group(2) or "سم"
            width = height = s_val * _parse_unit_scale(unit)
            label = f"SQUARE {s_val:g}x{s_val:g} {unit}"
            desc = f"تم رسم مربع طول ضلعه {s_val:g} {unit} بلون المراجعة الأحمر (_AI_PREVIEW_REVIEW)."
        else:
            return _ask_user(
                prompt,
                "يا باشمهندس، ما هي الأبعاد المطلوبة (مثلاً: مربع طول ضلعه 50 سم، أو مستطيل 3×5 متر)؟",
                output_dir,
                "rectangle",
            )

        out_file = _render_entities_to_dxf(
            [{"type": "rect", "x": 0.0, "y": 0.0, "width": width, "height": height, "label": label}],
            output_dir,
            "custom_geometry.dxf",
        )
        _clear_pending_context()
        return {"status": "success", "dxf": str(out_file), "description": desc}

    # 7. Single Shape: Line (word-boundary safe — never matches مخطط!)
    if has_line:
        line_m = re.search(r"(\d+(?:\.\d+)?)\s*(سم|متر|مم|cm|m|mm)?", text)
        if not line_m:
            return _ask_user(
                prompt,
                "يا باشمهندس، ما هو طول الخط المطلوب واتجاهه (أفقي أم رأسي)؟",
                output_dir,
                "line",
            )

        l_val = float(line_m.group(1))
        unit = line_m.group(2) or "سم"
        length = l_val * _parse_unit_scale(unit)
        is_vert = any(k in text for k in ("راسي", "رأسي", "عمودي", "vertical"))
        end_pt = [0.0, length] if is_vert else [length, 0.0]
        dir_txt = "رأسي" if is_vert else "أفقي"
        out_file = _render_entities_to_dxf(
            [{"type": "line", "start": [0.0, 0.0], "end": end_pt, "label": f"L = {l_val:g} {unit}"}],
            output_dir,
            "custom_geometry.dxf",
        )
        _clear_pending_context()
        return {
            "status": "success",
            "dxf": str(out_file),
            "description": f"تم رسم خط {dir_txt} بطول {l_val:g} {unit} بلون المراجعة الأحمر (_AI_PREVIEW_REVIEW).",
        }

    # 8. Single Room
    if any(k in text for k in ("غرفة", "غرفه", "صالة", "صاله", "room")):
        dim_m = re.search(r"(\d+(?:\.\d+)?)\s*(?:في|x|\*)\s*(\d+(?:\.\d+)?)\s*(سم|متر|مم|cm|m|mm)?", text)
        if not dim_m:
            return _ask_user(
                prompt,
                "يا باشمهندس، ما هي أبعاد الغرفة المطلوبة (مثلاً: غرفة 4×5 متر بسماكة جدار 20 سم) وموقع الباب والشباك؟",
                output_dir,
                "room",
            )
        w = float(dim_m.group(1)) * 1000.0
        h = float(dim_m.group(2)) * 1000.0
        wall = 200.0

        doc = ezdxf.new("R2010", setup=True)
        doc.header["$INSUNITS"] = 4
        msp = doc.modelspace()
        layer = "_AI_PREVIEW_REVIEW"
        doc.layers.add(layer, color=1)
        doc.layers.add("A-DOOR", color=2)
        msp.add_lwpolyline([(0, 0), (w, 0), (w, h), (0, h)], close=True, dxfattribs={"layer": layer})
        msp.add_lwpolyline([(wall, wall), (w - wall, wall), (w - wall, h - wall), (wall, h - wall)], close=True, dxfattribs={"layer": layer})
        msp.add_arc((wall, wall + 900.0), 900.0, 270, 360, dxfattribs={"layer": "A-DOOR"})
        txt = msp.add_text(f"ROOM {dim_m.group(1)}x{dim_m.group(2)}m (Wall=20cm)", dxfattribs={"layer": layer, "height": 80})
        txt.set_placement((w / 2, h / 2), align=TextEntityAlignment.MIDDLE_CENTER)

        out_file = output_dir / "custom_room.dxf"
        doc.saveas(str(out_file))
        _clear_pending_context()
        return {
            "status": "success",
            "dxf": str(out_file),
            "description": f"تم رسم غرفة بأبعاد {dim_m.group(1)} × {dim_m.group(2)} متر وسماكة جدران 20 سم بنجاح بلون المراجعة الأحمر (_AI_PREVIEW_REVIEW).",
        }

    # 9. Generative AI Fallback via Gemini
    return _call_gemini_cad_generator(prompt, output_dir)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--prompt", type=str, required=True)
    default_out = Path(__file__).resolve().parent.parent / "workspace" / "apartment_150sqm"
    parser.add_argument("--output-dir", type=Path, default=default_out)
    args = parser.parse_args()
    result = parse_and_generate(args.prompt, args.output_dir)
    # Output ASCII-safe JSON so ANY host codepage (cp1252, cp1256, utf-8) decodes cleanly
    print(json.dumps(result, ensure_ascii=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
