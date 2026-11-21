"""Generate complete AutoCAD DWG/DXF and high-resolution PNG engineering drawings
for the EEHC 2500 kVA Low Voltage Distribution Panel according to EDMS 02-300-3 (Rev-3, 23-04-2024).
"""

from __future__ import annotations

import json
import math
import subprocess
from pathlib import Path
from typing import Any

import ezdxf
from ezdxf import units
from PIL import Image, ImageDraw, ImageFont


# ---------------------------------------------------------------------------
# Pure-Python Arabic Contextual Reshaper & BiDi Helper for crisp PIL rendering
# ---------------------------------------------------------------------------
# Maps Unicode Arabic codepoints to (Isolated, Final, Initial, Medial) Presentation Forms-B
_ARABIC_FORMS: dict[int, tuple[int, int, int | None, int | None]] = {
    0x0621: (0xFE80, 0xFE80, None, None),        # HAMZA
    0x0622: (0xFE81, 0xFE82, None, None),        # ALEF WITH MADDA
    0x0623: (0xFE83, 0xFE84, None, None),        # ALEF WITH HAMZA ABOVE
    0x0624: (0xFE85, 0xFE86, None, None),        # WAW WITH HAMZA
    0x0625: (0xFE87, 0xFE88, None, None),        # ALEF WITH HAMZA BELOW
    0x0626: (0xFE89, 0xFE8A, 0xFE8B, 0xFE8C),    # YEH WITH HAMZA
    0x0627: (0xFE8D, 0xFE8E, None, None),        # ALEF
    0x0628: (0xFE8F, 0xFE90, 0xFE91, 0xFE92),    # BEH
    0x0629: (0xFE93, 0xFE94, None, None),        # TEH MARBUTA
    0x062A: (0xFE95, 0xFE96, 0xFE97, 0xFE98),    # TEH
    0x062B: (0xFE99, 0xFE9A, 0xFE9B, 0xFE9C),    # THEH
    0x062C: (0xFE9D, 0xFE9E, 0xFE9F, 0xFEA0),    # JEEM
    0x062D: (0xFEA1, 0xFEA2, 0xFEA3, 0xFEA4),    # HAH
    0x062E: (0xFEA5, 0xFEA6, 0xFEA7, 0xFEA8),    # KHAH
    0x062F: (0xFEA9, 0xFEAA, None, None),        # DAL
    0x0630: (0xFEAB, 0xFEAC, None, None),        # THAL
    0x0631: (0xFEAD, 0xFEAE, None, None),        # REH
    0x0632: (0xFEAF, 0xFEB0, None, None),        # ZAIN
    0x0633: (0xFEB1, 0xFEB2, 0xFEB3, 0xFEB4),    # SEEN
    0x0634: (0xFEB5, 0xFEB6, 0xFEB7, 0xFEB8),    # SHEEN
    0x0635: (0xFEB9, 0xFEBA, 0xFEBB, 0xFEBC),    # SAD
    0x0636: (0xFEBD, 0xFEBE, 0xFEBF, 0xFEC0),    # DAD
    0x0637: (0xFEC1, 0xFEC2, 0xFEC3, 0xFEC4),    # TAH
    0x0638: (0xFEC5, 0xFEC6, 0xFEC7, 0xFEC8),    # ZAH
    0x0639: (0xFEC9, 0xFECA, 0xFECB, 0xFECC),    # AIN
    0x063A: (0xFECD, 0xFECE, 0xFECF, 0xFED0),    # GHAIN
    0x0640: (0x0640, 0x0640, 0x0640, 0x0640),    # TATWEEL
    0x0641: (0xFED1, 0xFED2, 0xFED3, 0xFED4),    # FEH
    0x0642: (0xFED5, 0xFED6, 0xFED7, 0xFED8),    # QAF
    0x0643: (0xFED9, 0xFEDA, 0xFEDB, 0xFEDC),    # KAF
    0x0644: (0xFEDD, 0xFEDE, 0xFEDF, 0xFEE0),    # LAM
    0x0645: (0xFEE1, 0xFEE2, 0xFEE3, 0xFEE4),    # MEEM
    0x0646: (0xFEE5, 0xFEE6, 0xFEE7, 0xFEE8),    # NOON
    0x0647: (0xFEE9, 0xFEEA, 0xFEEB, 0xFEEC),    # HEH
    0x0648: (0xFEED, 0xFEEE, None, None),        # WAW
    0x0649: (0xFEEF, 0xFEF0, None, None),        # ALEF MAKSURA
    0x064A: (0xFEF1, 0xFEF2, 0xFEF3, 0xFEF4),    # YEH
}

_LAM_ALEF_LIGATURES: dict[int, tuple[int, int]] = {
    0x0622: (0xFEF5, 0xFEF6),  # LAM + ALEF MADDA
    0x0623: (0xFEF7, 0xFEF8),  # LAM + ALEF HAMZA ABOVE
    0x0625: (0xFEF9, 0xFEFA),  # LAM + ALEF HAMZA BELOW
    0x0627: (0xFEFB, 0xFEFC),  # LAM + ALEF
}


def _connects_after(cp: int) -> bool:
    forms = _ARABIC_FORMS.get(cp)
    return bool(forms and forms[2] is not None)


def _connects_before(cp: int) -> bool:
    return cp in _ARABIC_FORMS and cp != 0x0621


def shape_arabic_word(text: str) -> str:
    """Shape a pure Arabic run into Presentation Forms-B and reverse for LTR canvas."""
    cps = [ord(ch) for ch in text if not (0x064B <= ord(ch) <= 0x065F)]
    out: list[int] = []
    i = 0
    n = len(cps)
    while i < n:
        cur = cps[i]
        prev_cp = cps[i - 1] if i > 0 else None
        prev_conn = prev_cp is not None and _connects_after(prev_cp)
        # Check Lam-Alef ligature
        if cur == 0x0644 and i + 1 < n and cps[i + 1] in _LAM_ALEF_LIGATURES:
            iso_lig, fin_lig = _LAM_ALEF_LIGATURES[cps[i + 1]]
            out.append(fin_lig if prev_conn else iso_lig)
            i += 2
            continue
        forms = _ARABIC_FORMS.get(cur)
        if forms is None:
            out.append(cur)
            i += 1
            continue
        next_cp = None
        if i + 1 < n:
            if cur == 0x0644 and cps[i + 1] in _LAM_ALEF_LIGATURES:
                next_cp = 0x0627
            else:
                next_cp = cps[i + 1]
        next_conn = next_cp is not None and _connects_after(cur) and _connects_before(next_cp)
        iso_f, fin_f, ini_f, med_f = forms
        if prev_conn and next_conn and med_f is not None:
            out.append(med_f)
        elif prev_conn and fin_f is not None:
            out.append(fin_f)
        elif next_conn and ini_f is not None:
            out.append(ini_f)
        else:
            out.append(iso_f)
        i += 1
    return "".join(chr(c) for c in reversed(out))


def _reshape_rtl_run(text: str) -> str:
    """Reshape and visually order an RTL-base string containing Arabic and optional LTR tokens."""
    tokens: list[tuple[str, str]] = []
    buf: list[str] = []
    mode = ""
    n = len(text)
    for i, ch in enumerate(text):
        cp = ord(ch)
        is_ar = 0x0600 <= cp <= 0x06FF
        is_alnum_ltr = ("A" <= ch <= "Z") or ("a" <= ch <= "z") or ("0" <= ch <= "9")
        # Keep connectors like ., /, %, +, - inside an LTR token only when between LTR characters
        prev_ltr = i > 0 and (("A" <= text[i - 1] <= "Z") or ("a" <= text[i - 1] <= "z") or ("0" <= text[i - 1] <= "9"))
        next_ltr = (i + 1 < n) and (("A" <= text[i + 1] <= "Z") or ("a" <= text[i + 1] <= "z") or ("0" <= text[i + 1] <= "9"))
        is_ltr = is_alnum_ltr or (ch in "./%+-" and prev_ltr and next_ltr)
        cur_mode = "AR" if is_ar else ("LTR" if is_ltr else "NEUTRAL")
        if cur_mode != mode and buf:
            tokens.append((mode, "".join(buf)))
            buf = [ch]
            mode = cur_mode
        else:
            buf.append(ch)
            mode = cur_mode
    if buf:
        tokens.append((mode, "".join(buf)))
    rendered_visual: list[str] = []
    mirror = str.maketrans("()[]{}<>", ")(][}{><")
    for tok_mode, seg in reversed(tokens):
        if tok_mode == "AR":
            rendered_visual.append(shape_arabic_word(seg))
        elif tok_mode == "LTR":
            rendered_visual.append(seg)
        else:
            rendered_visual.append(seg[::-1].translate(mirror))
    return "".join(rendered_visual)


def bidi_text(text: str) -> str:
    """Shape Arabic segments and order mixed Arabic/English text for PIL drawing."""
    if not any(0x0600 <= ord(ch) <= 0x06FF for ch in text):
        return text
    # Determine base direction from first strong character
    first_strong = "RTL"
    for ch in text:
        cp = ord(ch)
        if 0x0600 <= cp <= 0x06FF:
            first_strong = "RTL"
            break
        if ("A" <= ch <= "Z") or ("a" <= ch <= "z"):
            first_strong = "LTR"
            break
    if first_strong == "RTL":
        return _reshape_rtl_run(text)
    # LTR base string with embedded Arabic segments: keep LTR order, reshape each contiguous Arabic phrase in-place
    import re
    return re.sub(r"[\u0600-\u06FF]+(?:\s+[\u0600-\u06FF]+)*", lambda m: _reshape_rtl_run(m.group(0)), text)


def get_font(size: int, bold: bool = False) -> ImageFont.FreeTypeFont | ImageFont.ImageFont:
    candidates = (
        ["C:/Windows/Fonts/arialbd.ttf", "C:/Windows/Fonts/tahomabd.ttf"]
        if bold
        else ["C:/Windows/Fonts/arial.ttf", "C:/Windows/Fonts/tahoma.ttf"]
    )
    for c in candidates:
        if Path(c).exists():
            return ImageFont.truetype(c, size)
    return ImageFont.load_default()


# ---------------------------------------------------------------------------
# Dual-Target CAD + High-Res Engineering Blueprint Canvas (DXF/DWG + PNG)
# ---------------------------------------------------------------------------
LAYER_DEFS: dict[str, tuple[int, str]] = {
    "EEHC-BORDER": (7, "#0f172a"),
    "EEHC-TITLE": (5, "#1e3a8a"),
    "EEHC-ENCLOSURE": (7, "#1e293b"),
    "EEHC-FRAME": (8, "#475569"),
    "EEHC-DOOR": (9, "#334155"),
    "EEHC-BUS-R": (1, "#dc2626"),
    "EEHC-BUS-Y": (2, "#d97706"),
    "EEHC-BUS-B": (5, "#2563eb"),
    "EEHC-BUS-N": (7, "#111827"),
    "EEHC-BUS-PE": (3, "#15803d"),
    "EEHC-ACB": (4, "#0284c7"),
    "EEHC-MCCB": (5, "#1d4ed8"),
    "EEHC-SWFUSE": (6, "#7c3aed"),
    "EEHC-CAPACITOR": (3, "#059669"),
    "EEHC-LIGHTING": (30, "#ea580c"),
    "EEHC-METERING": (4, "#0369a1"),
    "EEHC-INSULATOR": (30, "#b45309"),
    "EEHC-PROHIBITED": (1, "#b91c1c"),
    "EEHC-DIM": (8, "#64748b"),
    "EEHC-TEXT": (7, "#0f172a"),
    "EEHC-TEXT-AR": (5, "#1e3a8a"),
    "EEHC-TABLE": (7, "#1e293b"),
}


class EngineeringSheet:
    """Simultaneously builds a 1:1 mm AutoCAD DXF/DWG and a high-resolution PNG."""

    def __init__(self, sheet_no: str, title_en: str, title_ar: str, width_mm: float = 4200.0, height_mm: float = 2970.0, px_scale: float = 0.85) -> None:
        self.sheet_no = sheet_no
        self.title_en = title_en
        self.title_ar = title_ar
        self.w_mm = width_mm
        self.h_mm = height_mm
        self.scale = px_scale
        self.img_w = int(round(width_mm * px_scale))
        self.img_h = int(round(height_mm * px_scale))
        self.image = Image.new("RGB", (self.img_w, self.img_h), "#ffffff")
        self.draw = ImageDraw.Draw(self.image)

        self.doc = ezdxf.new("R2010", setup=True)
        self.doc.units = units.MM
        self.doc.header["$MEASUREMENT"] = 1
        self.doc.header["$INSUNITS"] = 4
        if "EEHC_ARIAL" not in self.doc.styles:
            self.doc.styles.add("EEHC_ARIAL", font="arial.ttf")
        for lname, (aci, _) in LAYER_DEFS.items():
            if lname not in self.doc.layers:
                self.doc.layers.add(lname, color=aci)
        self.msp = self.doc.modelspace()
        self._draw_sheet_frame()

    def _px(self, x: float, y: float) -> tuple[int, int]:
        return (int(round(x * self.scale)), int(round((self.h_mm - y) * self.scale)))

    def _color(self, layer: str, override: str | None = None) -> str:
        if override:
            return override
        return LAYER_DEFS.get(layer, (7, "#0f172a"))[1]

    def line(self, x1: float, y1: float, x2: float, y2: float, layer: str = "EEHC-ENCLOSURE", width: int = 2, dashed: bool = False, color: str | None = None) -> None:
        dxf_attribs: dict[str, Any] = {"layer": layer}
        if dashed:
            dxf_attribs["linetype"] = "DASHED"
        self.msp.add_line((x1, y1), (x2, y2), dxfattribs=dxf_attribs)
        c = self._color(layer, color)
        if not dashed:
            self.draw.line((*self._px(x1, y1), *self._px(x2, y2)), fill=c, width=width)
        else:
            dist = math.hypot(x2 - x1, y2 - y1)
            steps = max(int(dist // 18), 2)
            for i in range(0, steps, 2):
                t0 = i / steps
                t1 = min((i + 1) / steps, 1.0)
                sx0, sy0 = x1 + (x2 - x1) * t0, y1 + (y2 - y1) * t0
                sx1, sy1 = x1 + (x2 - x1) * t1, y1 + (y2 - y1) * t1
                self.draw.line((*self._px(sx0, sy0), *self._px(sx1, sy1)), fill=c, width=width)

    def rect(self, x: float, y: float, w: float, h: float, layer: str = "EEHC-ENCLOSURE", width: int = 2, fill: str | None = None, dashed: bool = False, color: str | None = None) -> None:
        pts = [(x, y), (x + w, y), (x + w, y + h), (x, y + h), (x, y)]
        dxf_attribs: dict[str, Any] = {"layer": layer}
        if dashed:
            dxf_attribs["linetype"] = "DASHED"
        self.msp.add_lwpolyline(pts, dxfattribs=dxf_attribs)
        p1 = self._px(x, y + h)
        p2 = self._px(x + w, y)
        c = self._color(layer, color)
        if fill:
            self.draw.rectangle((*p1, *p2), fill=fill, outline=c if not dashed else None, width=width)
        if dashed:
            self.line(x, y, x + w, y, layer, width, True, color)
            self.line(x + w, y, x + w, y + h, layer, width, True, color)
            self.line(x + w, y + h, x, y + h, layer, width, True, color)
            self.line(x, y + h, x, y, layer, width, True, color)
        elif not fill:
            self.draw.rectangle((*p1, *p2), outline=c, width=width)

    def hatch_rect(self, x: float, y: float, w: float, h: float, layer: str = "EEHC-PROHIBITED", fill: str = "#fef2f2", step: float = 35.0) -> None:
        self.rect(x, y, w, h, layer=layer, width=2, fill=fill)
        cur = 0.0
        while cur < w + h:
            x_start = x + max(0.0, cur - h)
            y_start = y + min(h, cur)
            x_end = x + min(w, cur)
            y_end = y + max(0.0, cur - w)
            self.line(x_start, y_start, x_end, y_end, layer=layer, width=1)
            cur += step

    def circle(self, cx: float, cy: float, r: float, layer: str = "EEHC-METERING", width: int = 2, fill: str | None = None, color: str | None = None) -> None:
        self.msp.add_circle((cx, cy), r, dxfattribs={"layer": layer})
        p1 = self._px(cx - r, cy + r)
        p2 = self._px(cx + r, cy - r)
        c = self._color(layer, color)
        self.draw.ellipse((*p1, *p2), fill=fill, outline=c, width=width)

    def text(self, x: float, y: float, value: str, height_mm: float = 18.0, layer: str = "EEHC-TEXT", bold: bool = False, align: str = "left", color: str | None = None) -> None:
        clean = value.strip()
        if not clean:
            return
        # Add to DXF
        dxf_x = x
        if align == "center":
            dxf_x = x - (len(clean) * height_mm * 0.27)
        elif align == "right":
            dxf_x = x - (len(clean) * height_mm * 0.54)
        self.msp.add_text(
            clean,
            dxfattribs={"layer": layer, "height": height_mm, "style": "EEHC_ARIAL", "insert": (dxf_x, y)},
        )
        # Add to PNG
        px_size = max(int(round(height_mm * self.scale)), 9)
        font = get_font(px_size, bold=bold)
        px_pt = self._px(x, y)
        anchor = {"left": "ls", "center": "ms", "right": "rs"}.get(align, "ls")
        c = self._color(layer, color)
        self.draw.text(px_pt, bidi_text(clean), font=font, fill=c, anchor=anchor)

    def dim_h(self, x1: float, x2: float, y: float, label: str, offset: float = 0.0) -> None:
        yd = y + offset
        if offset != 0.0:
            self.line(x1, y, x1, yd, layer="EEHC-DIM", width=1, dashed=True)
            self.line(x2, y, x2, yd, layer="EEHC-DIM", width=1, dashed=True)
        self.line(x1, yd, x2, yd, layer="EEHC-DIM", width=2)
        # Arrow ticks
        a = 12.0
        self.line(x1, yd, x1 + a, yd + a * 0.5, layer="EEHC-DIM", width=2)
        self.line(x1, yd, x1 + a, yd - a * 0.5, layer="EEHC-DIM", width=2)
        self.line(x2, yd, x2 - a, yd + a * 0.5, layer="EEHC-DIM", width=2)
        self.line(x2, yd, x2 - a, yd - a * 0.5, layer="EEHC-DIM", width=2)
        self.text((x1 + x2) / 2, yd + 8, label, height_mm=15.0, layer="EEHC-DIM", bold=True, align="center")

    def dim_v(self, x: float, y1: float, y2: float, label: str, offset: float = 0.0) -> None:
        xd = x + offset
        if offset != 0.0:
            self.line(x, y1, xd, y1, layer="EEHC-DIM", width=1, dashed=True)
            self.line(x, y2, xd, y2, layer="EEHC-DIM", width=1, dashed=True)
        self.line(xd, y1, xd, y2, layer="EEHC-DIM", width=2)
        a = 12.0
        self.line(xd, y1, xd - a * 0.5, y1 + a, layer="EEHC-DIM", width=2)
        self.line(xd, y1, xd + a * 0.5, y1 + a, layer="EEHC-DIM", width=2)
        self.line(xd, y2, xd - a * 0.5, y2 - a, layer="EEHC-DIM", width=2)
        self.line(xd, y2, xd + a * 0.5, y2 - a, layer="EEHC-DIM", width=2)
        self.text(xd + (12 if offset >= 0 else -12), (y1 + y2) / 2, label, height_mm=15.0, layer="EEHC-DIM", bold=True, align="left" if offset >= 0 else "right")

    def callout(self, x_target: float, y_target: float, x_box: float, y_box: float, title: str, lines: list[str], box_w: float = 640.0, layer: str = "EEHC-TITLE", fill: str = "#f8fafc") -> None:
        line_h = 24.0
        box_h = 42.0 + len(lines) * line_h
        # Leader line
        mid_x = x_box if x_target < x_box else x_box + box_w
        mid_y = y_box + box_h * 0.5
        self.line(x_target, y_target, mid_x, mid_y, layer=layer, width=2)
        self.circle(x_target, y_target, 6.0, layer=layer, fill=self._color(layer))
        # Box
        self.rect(x_box, y_box, box_w, box_h, layer=layer, width=2, fill=fill)
        self.rect(x_box, y_box + box_h - 32.0, box_w, 32.0, layer=layer, width=2, fill="#e2e8f0")
        self.text(x_box + 12, y_box + box_h - 23.0, title, height_mm=16.0, layer=layer, bold=True)
        for idx, ln in enumerate(lines):
            self.text(x_box + 12, y_box + box_h - 56.0 - idx * line_h, ln, height_mm=14.0, layer="EEHC-TEXT")

    def table(self, x: float, y_top: float, title: str, headers: list[tuple[str, float]], rows: list[list[str]], row_h: float = 28.0, header_fill: str = "#1e3a8a") -> float:
        total_w = sum(w for _, w in headers)
        # Title bar
        self.rect(x, y_top - 36.0, total_w, 36.0, layer="EEHC-TITLE", width=2, fill=header_fill)
        self.text(x + total_w / 2, y_top - 26.0, title, height_mm=17.0, layer="EEHC-TITLE", bold=True, align="center", color="#ffffff")
        # Column headers
        cur_y = y_top - 36.0 - row_h
        cur_x = x
        for h_text, col_w in headers:
            self.rect(cur_x, cur_y, col_w, row_h, layer="EEHC-TABLE", width=1, fill="#dbeafe")
            self.text(cur_x + 8, cur_y + 8, h_text, height_mm=14.0, layer="EEHC-TITLE", bold=True)
            cur_x += col_w
        # Data rows
        for r_idx, r_vals in enumerate(rows):
            cur_y -= row_h
            cur_x = x
            bg = "#ffffff" if r_idx % 2 == 0 else "#f8fafc"
            for (_, col_w), cell in zip(headers, r_vals):
                self.rect(cur_x, cur_y, col_w, row_h, layer="EEHC-TABLE", width=1, fill=bg)
                self.text(cur_x + 8, cur_y + 8, cell, height_mm=13.5, layer="EEHC-TEXT", bold=(cur_x == x))
                cur_x += col_w
        return cur_y

    def _draw_sheet_frame(self) -> None:
        m = 40.0
        self.rect(m, m, self.w_mm - 2 * m, self.h_mm - 2 * m, layer="EEHC-BORDER", width=4)
        self.rect(m + 12, m + 12, self.w_mm - 2 * (m + 12), self.h_mm - 2 * (m + 12), layer="EEHC-BORDER", width=2)
        # Top header banner
        banner_h = 115.0
        by = self.h_mm - m - 12 - banner_h
        self.rect(m + 12, by, self.w_mm - 2 * (m + 12), banner_h, layer="EEHC-TITLE", width=2, fill="#0f172a")
        self.text(m + 45, by + 72, "MINISTRY OF ELECTRICITY & RENEWABLE ENERGY - EGYPTIAN ELECTRICITY HOLDING COMPANY (EEHC)", height_mm=21.0, layer="EEHC-TITLE", bold=True, color="#f8fafc")
        self.text(m + 45, by + 38, f"SPECIFICATION: EDMS 02-300-3 (ISSUE: APRIL 2024 / REV-3, DATE: 23-04-2024) | 2500 KVA L.V. DISTRIBUTION PANEL (0.4 kV, 50 Hz, 4500 A)", height_mm=17.0, layer="EEHC-TITLE", bold=True, color="#93c5fd")
        self.text(m + 45, by + 12, f"SHEET {self.sheet_no}: {self.title_en}", height_mm=16.0, layer="EEHC-TITLE", bold=True, color="#fde047")
        self.text(self.w_mm - m - 45, by + 65, "وزارة الكهرباء والطاقة المتجددة - الشركة القابضة لكهرباء مصر", height_mm=21.0, layer="EEHC-TITLE", bold=True, align="right", color="#f8fafc")
        self.text(self.w_mm - m - 45, by + 25, self.title_ar, height_mm=19.0, layer="EEHC-TITLE", bold=True, align="right", color="#fde047")
        # Bottom title block bar
        tb_h = 75.0
        ty = m + 12
        self.rect(m + 12, ty, self.w_mm - 2 * (m + 12), tb_h, layer="EEHC-TITLE", width=2, fill="#f1f5f9")
        self.text(m + 35, ty + 44, "STANDARD: EEHC EDMS 02-300-3 + EDMS 19-300 / 19-302 / 19-303 + IEC 61439-1 / IEC 60947 / IEC 60529 / IEC 62262", height_mm=14.0, layer="EEHC-TITLE", bold=True)
        self.text(m + 35, ty + 16, "TRANSFORMER RATING: 2500 KVA | PANEL RATED CURRENT: 4500 A | MAIN BUSBARS: 3000 mm2 (PHASES) / 1800 mm2 (NEUTRAL) / >=750 mm2 (EARTH)", height_mm=14.0, layer="EEHC-TEXT", bold=True)
        self.text(self.w_mm - m - 35, ty + 44, f"GENERATED BY: AI-ENGINEER-R50 CAD ENGINE (1:1 mm MODEL SPACE) | SHEET: {self.sheet_no}", height_mm=14.0, layer="EEHC-TITLE", bold=True, align="right")
        self.text(self.w_mm - m - 35, ty + 16, "لوحة توزيع جهد منخفض بقدرة 2500 ك.ف.أ - مطابقة حرفيا لمواصفة القابضة لكهرباء مصر EDMS 02-300-3", height_mm=14.0, layer="EEHC-TEXT-AR", bold=True, align="right")

    def save(self, dxf_path: Path, png_path: Path) -> None:
        dxf_path.parent.mkdir(parents=True, exist_ok=True)
        png_path.parent.mkdir(parents=True, exist_ok=True)
        self.doc.saveas(str(dxf_path))
        self.image.save(str(png_path))


# ---------------------------------------------------------------------------
# SHEET 01: MAIN CIRCUIT BREAKER (ACB 5000A) CUBICLE & INCOMING CONNECTION
# ---------------------------------------------------------------------------
def build_sheet_01_main_acb(out_dir: Path) -> tuple[Path, Path]:
    s = EngineeringSheet(
        "01 / 06",
        "MAIN CIRCUIT BREAKER CUBICLE (ACB 5000A), METERING & TRANSFORMER CONNECTION DETAILS",
        "لوحة تفصيلية لخلية القاطع الرئيسي الهوائي (ACB 5000A) وأجهزة القياس وطرق الربط مع المحول 2500 ك.ف.أ",
    )

    # 1. Left View: External Front Door of Incoming Cubicle (x=180..1180, y=320..2520)
    bx, by, bw, bh = 200.0, 350.0, 950.0, 2000.0
    # 4 Plinths (100 mm height)
    s.rect(bx, by - 100.0, bw, 100.0, layer="EEHC-FRAME", width=3, fill="#cbd5e1")
    for px in (bx + 20, bx + 260, bx + 610, bx + bw - 100):
        s.rect(px, by - 100.0, 80.0, 100.0, layer="EEHC-ENCLOSURE", width=2, fill="#64748b")
    s.text(bx + bw / 2, by - 62.0, "4 PLINTHS (HEIGHT >= 100 mm / 10 cm) - CLOSED AFTER INSTALLATION (CL. 6.3)", height_mm=13.0, bold=True, align="center")

    # Main Cubicle Body & Sloped Roof (6 deg)
    s.rect(bx, by, bw, bh, layer="EEHC-ENCLOSURE", width=4, fill="#f8fafc")
    s.rect(bx + 25, by + 25, bw - 50, bh - 50, layer="EEHC-DOOR", width=2, fill="#f1f5f9")
    # Vertical longitudinal door stiffener (dashed)
    s.line(bx + bw / 2, by + 40, bx + bw / 2, by + bh - 40, layer="EEHC-FRAME", width=2, dashed=True)
    # Pyramidal sloped roof (6 deg)
    roof_h = 65.0
    s.line(bx - 15, by + bh, bx + bw / 2, by + bh + roof_h, layer="EEHC-ENCLOSURE", width=3)
    s.line(bx + bw / 2, by + bh + roof_h, bx + bw + 15, by + bh, layer="EEHC-ENCLOSURE", width=3)
    s.line(bx - 15, by + bh, bx + bw + 15, by + bh, layer="EEHC-ENCLOSURE", width=3)
    # Lifting lugs
    s.circle(bx + 50, by + bh + 25, 18.0, layer="EEHC-FRAME", width=3)
    s.circle(bx + bw - 50, by + bh + 25, 18.0, layer="EEHC-FRAME", width=3)

    # Top Busbar Tree Protruding 200 mm through Paxolin Plate
    s.rect(bx + 220, by + bh, 510.0, 22.0, layer="EEHC-INSULATOR", width=2, fill="#d97706")
    for idx, (lbl, lyr, xp) in enumerate((("L1", "EEHC-BUS-R", 280), ("L2", "EEHC-BUS-Y", 390), ("L3", "EEHC-BUS-B", 500), ("N", "EEHC-BUS-N", 620))):
        s.rect(bx + xp, by + bh + 22, 42.0, 200.0, layer=lyr, width=2, fill=s._color(lyr))
        # Tree branches at top
        s.line(bx + xp - 22, by + bh + 195, bx + xp + 64, by + bh + 195, layer=lyr, width=4)
        s.circle(bx + xp - 14, by + bh + 195, 5.0, layer="EEHC-ENCLOSURE", fill="#ffffff")
        s.circle(bx + xp + 56, by + bh + 195, 5.0, layer="EEHC-ENCLOSURE", fill="#ffffff")
        s.text(bx + xp + 21, by + bh + 230, f"{lbl} B.B. TREE", height_mm=13.0, layer=lyr, bold=True, align="center")
    s.rect(bx + 255, by + bh + 24, 440.0, 26.0, layer="EEHC-INSULATOR", width=1, fill="#fef3c7")
    s.text(bx + 475, by + bh + 30, "PAXOLIN INSULATING PLATE (لوح فيبر عازل باكسولين)", height_mm=12.0, layer="EEHC-INSULATOR", bold=True, align="center")

    # 3 Stainless Steel / Galvanized Hinges on left + 3-point Master Key Lock on right
    for hy in (by + 280, by + 1000, by + 1720):
        s.rect(bx + 15, hy, 22.0, 65.0, layer="EEHC-FRAME", width=2, fill="#475569")
    s.rect(bx + bw - 65, by + 940, 28.0, 120.0, layer="EEHC-FRAME", width=2, fill="#94a3b8")
    s.line(bx + bw - 51, by + 120, bx + bw - 51, by + bh - 120, layer="EEHC-FRAME", width=2, dashed=True)

    # Door Metering & Indication Compartment (Top Section of Main Cubicle)
    s.line(bx + 25, by + 1450, bx + bw - 25, by + 1450, layer="EEHC-DOOR", width=2)
    # 3 LED Indication Lamps (R, Y, B) with fuses
    for idx, (c_name, col, lx) in enumerate((("R", "#dc2626", 180), ("Y", "#eab308", 270), ("B", "#2563eb", 360))):
        s.circle(bx + lx, by + 1860, 22.0, layer="EEHC-METERING", width=2, fill=col)
        s.text(bx + lx, by + 1815, f"LED {c_name}+FUSE", height_mm=11.0, bold=True, align="center")
    # 3 Digital Ammeters (0-5000A) + 1 Digital Voltmeter (0-500V)
    for idx, (m_lbl, mx) in enumerate((("A1: 0-5000A", 140), ("A2: 0-5000A", 290), ("A3: 0-5000A", 440), ("V: 0-500V", 590))):
        s.rect(bx + mx, by + 1660, 115.0, 115.0, layer="EEHC-METERING", width=2, fill="#0f172a")
        s.text(bx + mx + 57.5, by + 1725, "8888", height_mm=22.0, layer="EEHC-ACB", bold=True, align="center", color="#ef4444")
        s.text(bx + mx + 57.5, by + 1635, m_lbl, height_mm=12.0, bold=True, align="center")
    # KWH Meter Space & Terminal Blocks + 2 Flag Relays
    s.rect(bx + 735, by + 1650, 165.0, 210.0, layer="EEHC-METERING", width=2, fill="#e0f2fe")
    s.text(bx + 817.5, by + 1785, "KWH METER", height_mm=14.0, layer="EEHC-METERING", bold=True, align="center")
    s.text(bx + 817.5, by + 1755, "SPACE + T.B.", height_mm=13.0, layer="EEHC-METERING", bold=True, align="center")
    s.text(bx + 817.5, by + 1725, "(مكان العداد", height_mm=13.0, layer="EEHC-TEXT-AR", bold=True, align="center")
    s.text(bx + 817.5, by + 1695, "والروزيتات)", height_mm=13.0, layer="EEHC-TEXT-AR", bold=True, align="center")

    # 2 Flag Relays (Main ACB & Transformer Fuse LBS status)
    s.rect(bx + 140, by + 1500, 240.0, 95.0, layer="EEHC-LIGHTING", width=2, fill="#fff7ed")
    s.text(bx + 260, by + 1560, "FLAG RELAY #1: MAIN ACB", height_mm=12.5, layer="EEHC-LIGHTING", bold=True, align="center")
    s.text(bx + 260, by + 1525, "(فلاج ريليه حالة القاطع الرئيسي)", height_mm=12.0, layer="EEHC-TEXT-AR", bold=True, align="center")
    s.rect(bx + 410, by + 1500, 260.0, 95.0, layer="EEHC-LIGHTING", width=2, fill="#fff7ed")
    s.text(bx + 540, by + 1560, "FLAG RELAY #2: MV LBS FUSE", height_mm=12.5, layer="EEHC-LIGHTING", bold=True, align="center")
    s.text(bx + 540, by + 1525, "(فلاج ريليه حالة سكينة المحول)", height_mm=12.0, layer="EEHC-TEXT-AR", bold=True, align="center")

    # Metallic Nameplate (Clause 7)
    s.rect(bx + 700, by + 1500, 200.0, 110.0, layer="EEHC-TITLE", width=2, fill="#fef9c3")
    s.text(bx + 800, by + 1582, "NAMEPLATE (CL. 7)", height_mm=12.0, bold=True, align="center")
    s.text(bx + 800, by + 1558, "PANEL: 4500 A", height_mm=12.0, bold=True, align="center")
    s.text(bx + 800, by + 1534, "2500 KVA - 0.4 kV", height_mm=11.5, bold=True, align="center")
    s.text(bx + 800, by + 1510, "CUSTOMER: ... EDC", height_mm=11.5, bold=True, align="center")

    # Main ACB Compartment (Middle Section: y = by+720 .. by+1450)
    s.line(bx + 25, by + 720, bx + bw - 25, by + 720, layer="EEHC-DOOR", width=2)
    # Internal mirror cut-out showing ONLY Main ACB operating face/handle (Clause 4.4)
    s.rect(bx + 240, by + 820, 470.0, 520.0, layer="EEHC-ACB", width=4, fill="#e0f2fe")
    s.rect(bx + 290, by + 920, 370.0, 340.0, layer="EEHC-ENCLOSURE", width=2, fill="#ffffff")
    s.text(bx + 475, by + 1295, "MAIN AIR CIRCUIT BREAKER (ACB)", height_mm=16.0, layer="EEHC-ACB", bold=True, align="center")
    s.text(bx + 475, by + 1215, "3P - Frame: 5000 A | In = 5000 A (at 40 C)", height_mm=14.0, bold=True, align="center")
    s.text(bx + 475, by + 1180, "Icu = Ics = 65 kA at 400 V AC, 50 Hz", height_mm=14.0, layer="EEHC-PROHIBITED", bold=True, align="center")
    s.text(bx + 475, by + 1145, "Ui = 750 V | Uimp = 8 kV | (4700 A at 50 C)", height_mm=13.5, bold=True, align="center")
    s.text(bx + 475, by + 1110, "Thermal Ir: (0.4 - 1.0) In | Magnetic Im: (2 - 10) In", height_mm=13.0, bold=True, align="center")
    s.text(bx + 475, by + 1075, "WITHOUT Under-Voltage Coil (بدون ملف هبوط جهد)", height_mm=13.0, layer="EEHC-PROHIBITED", bold=True, align="center")
    s.text(bx + 475, by + 1040, "WITH Shunt Trip Coil (شامل ملف فصل Shunt Trip)", height_mm=13.0, layer="EEHC-ACB", bold=True, align="center")
    s.text(bx + 475, by + 960, "ONLY OPERATING HANDLE VISIBLE THROUGH", height_mm=12.0, bold=True, align="center")
    s.text(bx + 475, by + 935, ">=1.25 mm COMPARTMENTALIZED MIRROR (CL. 4.4)", height_mm=12.0, bold=True, align="center")

    # Prohibited Zone Below Main ACB (y = by+25 .. by+720) - Clause 5.2.A
    s.hatch_rect(bx + 45, by + 45, bw - 90, 655.0, layer="EEHC-PROHIBITED", fill="#fef2f2", step=45.0)
    s.rect(bx + 110, by + 230, bw - 220, 290.0, layer="EEHC-PROHIBITED", width=3, fill="#ffffff")
    s.text(bx + bw / 2, by + 465, "MANDATORY RULE FOR PANELS >= 500 KVA (CLAUSE 5.2.A):", height_mm=15.5, layer="EEHC-PROHIBITED", bold=True, align="center")
    s.text(bx + bw / 2, by + 425, "SEPARATE CUBICLE FOR MAIN ACB", height_mm=16.0, layer="EEHC-PROHIBITED", bold=True, align="center")
    s.text(bx + bw / 2, by + 385, "IT IS TOTALLY PROHIBITED TO INSTALL ANY", height_mm=15.0, layer="EEHC-PROHIBITED", bold=True, align="center")
    s.text(bx + bw / 2, by + 345, "SWITCH-FUSES OR MCCBs BELOW THE MAIN C.B.", height_mm=15.0, layer="EEHC-PROHIBITED", bold=True, align="center")
    s.text(bx + bw / 2, by + 295, "خلية منفصلة للقاطع الرئيسي - يمنع تماما وضع سويتش فيوز", height_mm=16.0, layer="EEHC-PROHIBITED", bold=True, align="center")
    s.text(bx + bw / 2, by + 255, "أو قواطع فرعية أسفل القاطع الرئيسي (بند 5-2/أ صفحة 6)", height_mm=16.0, layer="EEHC-PROHIBITED", bold=True, align="center")

    # Dimensions on Left View
    s.dim_h(bx, bx + bw, by - 165.0, "CUBICLE WIDTH <= 1000 mm (1 m MAX - CL. 6.2)")
    s.dim_v(bx - 75.0, by, by + bh, "PANEL HEIGHT = 2000 mm")
    s.dim_v(bx - 75.0, by - 100.0, by, "PLINTH >= 100 mm")
    s.dim_v(bx - 75.0, by + bh, by + bh + 222.0, "PROTRUDED B.B. = 200 mm (20 cm)")
    s.text(bx + bw / 2, by + bh + 280.0, "1. FRONT EXTERNAL / COMPARTMENT VIEW (مسقط أمامي لخلية القاطع الرئيسي)", height_mm=19.0, layer="EEHC-TITLE", bold=True, align="center")

    # 2. Middle View: Internal Copper Busbar Tree, CTs & ACB Copper Links (x=1330..2350)
    ix, iy, iw, ih = 1360.0, 350.0, 950.0, 2000.0
    s.rect(ix, iy - 100.0, iw, 100.0, layer="EEHC-FRAME", width=3, fill="#cbd5e1")
    s.rect(ix, iy, iw, ih, layer="EEHC-ENCLOSURE", width=4, fill="#ffffff")
    s.text(ix + iw / 2, iy + ih + 280.0, "2. INTERNAL BUSBAR, CTs & ACB SECTION (المسقط الداخلي وتفاصيل البارات ومحولات التيار)", height_mm=19.0, layer="EEHC-TITLE", bold=True, align="center")

    # Top Paxolin Plate + Protruding Busbar Tree (200 mm)
    s.rect(ix + 180, iy + ih - 10, 590.0, 25.0, layer="EEHC-INSULATOR", width=2, fill="#d97706")
    bus_x = [ix + 250, ix + 390, ix + 530, ix + 690]
    bus_layers = ["EEHC-BUS-R", "EEHC-BUS-Y", "EEHC-BUS-B", "EEHC-BUS-N"]
    bus_names = ["L1 (RED - 3000 mm2)", "L2 (YELLOW - 3000 mm2)", "L3 (BLUE - 3000 mm2)", "N (BLACK - 1800 mm2)"]
    for bx_i, lyr, bname in zip(bus_x, bus_layers, bus_names):
        # Protruding top tree
        s.rect(bx_i - 18, iy + ih + 15, 36.0, 200.0, layer=lyr, width=2, fill=s._color(lyr))
        s.rect(bx_i - 48, iy + ih + 170, 96.0, 28.0, layer=lyr, width=2, fill=s._color(lyr))
        # Dropper down to ACB (for L1, L2, L3) or Neutral Busbar (for N)
        if lyr != "EEHC-BUS-N":
            s.rect(bx_i - 18, iy + 1260, 36.0, 730.0, layer=lyr, width=2, fill=s._color(lyr))
        else:
            s.rect(bx_i - 15, iy + 320, 30.0, 1670.0, layer=lyr, width=2, fill=s._color(lyr))

    # 2 Sets of 3 Ring-Type Current Transformers (5000/5 A, Class 0.5, 10 VA, Ith=10kA)
    for set_idx, (ct_y, set_lbl) in enumerate(((iy + 1720, "CT SET #1 (METERING): 3x 5000/5A, Cl 0.5, 10VA, Ith=10kA"), (iy + 1560, "CT SET #2 (KWH METER): 3x 5000/5A, Cl 0.5, 10VA, Ith=10kA"))):
        for bx_i in bus_x[:3]:
            s.circle(bx_i, ct_y, 44.0, layer="EEHC-METERING", width=3, fill="#e0f2fe")
            s.circle(bx_i, ct_y, 22.0, layer="EEHC-ENCLOSURE", width=2, fill="#ffffff")
        s.rect(ix + 185, ct_y + 46, 430.0, 24.0, layer="EEHC-METERING", width=1, fill="#ffffff")
        s.text(ix + 400, ct_y + 52, set_lbl, height_mm=12.0, layer="EEHC-METERING", bold=True, align="center")

    # Sandwich Insulators supporting Vertical Busbars (Clause 6.8)
    for sy in (iy + 1850, iy + 1430, iy + 640):
        s.rect(ix + 190, sy, 560.0, 26.0, layer="EEHC-INSULATOR", width=2, fill="#fde68a")
        s.text(ix + 470, sy + 6, "SANDWICH INSULATOR (عازل المسطرة - بند 6-8)", height_mm=11.5, layer="EEHC-INSULATOR", bold=True, align="center")

    # Internal ACB 5000A Body & Arc Chutes
    s.rect(ix + 190, iy + 820, 430.0, 440.0, layer="EEHC-ACB", width=4, fill="#f0f9ff")
    for arc_x in (ix + 225, ix + 365, ix + 505):
        s.rect(arc_x, iy + 1180, 55.0, 70.0, layer="EEHC-FRAME", width=2, fill="#94a3b8")
    s.text(ix + 405, iy + 1090, "ACB 5000 A / 65 kA BODY", height_mm=16.0, layer="EEHC-ACB", bold=True, align="center")
    s.text(ix + 405, iy + 1050, "Silver-Alloy Main & Arcing Contacts", height_mm=13.0, bold=True, align="center")
    s.text(ix + 405, iy + 1015, "Arc Chutes Included | Ui=750V, Uimp=8kV", height_mm=13.0, bold=True, align="center")

    # Outgoing Busbars from bottom of ACB to Horizontal Main Busbar Chamber (3000 mm2)
    for idx, (bx_i, lyr) in enumerate(zip(bus_x[:3], bus_layers[:3])):
        target_y = iy + 520 - idx * 75
        s.rect(bx_i - 18, target_y, 36.0, iy + 820 - target_y, layer=lyr, width=2, fill=s._color(lyr))
        # Horizontal main busbar going right to outgoing cubicles
        s.rect(bx_i - 18, target_y - 18, iw - (bx_i - ix) + 20, 36.0, layer=lyr, width=2, fill=s._color(lyr))
    # Horizontal Neutral Busbar (1800 mm2) & Earth Busbar (>=750 mm2)
    s.rect(ix + 675, iy + 290, iw - 675 + 20, 30.0, layer="EEHC-BUS-N", width=2, fill=s._color("EEHC-BUS-N"))
    s.rect(ix + 40, iy + 110, iw - 60, 22.0, layer="EEHC-BUS-PE", width=2, fill=s._color("EEHC-BUS-PE"))
    s.text(ix + iw / 2, iy + 140, "MAIN EARTH BUSBAR (PE) >= 750 mm2 (>= 25% OF PHASE BUSBARS - CL. 5.1.5) + EARTHING BOLTS", height_mm=13.0, layer="EEHC-BUS-PE", bold=True, align="center")

    # Internal 220V Socket + MCB & >=9W LED Lamp + Separate MCB (Clause 6.24)
    s.rect(ix + 55, iy + 1880, 280.0, 85.0, layer="EEHC-LIGHTING", width=2, fill="#fefce8")
    s.text(ix + 195, iy + 1935, "INTERNAL LED LAMP >= 9W", height_mm=12.5, layer="EEHC-LIGHTING", bold=True, align="center")
    s.text(ix + 195, iy + 1900, "+ SEPARATE MCB (CL. 6.24)", height_mm=12.0, bold=True, align="center")
    s.rect(ix + 55, iy + 360, 210.0, 110.0, layer="EEHC-LIGHTING", width=2, fill="#fefce8")
    s.text(ix + 160, iy + 430, "220V SOCKET + MCB", height_mm=12.5, layer="EEHC-LIGHTING", bold=True, align="center")
    s.text(ix + 160, iy + 390, "(بريزة 220 فولت بمفتاح وقاية)", height_mm=12.0, layer="EEHC-TEXT-AR", bold=True, align="center")

    # 3. Right Column: Mandatory Specification Tables & 3 Transformer Connection Methods (x=2440..4080)
    tx = 2440.0
    s.table(
        tx,
        2620.0,
        "MAIN CIRCUIT BREAKER & INCOMING CUBICLE MANDATORY DATA (2500 KVA - TABLE 1 & CLAUSES 4, 5, 6)",
        [("Parameter / Clause", 520.0), ("Mandatory Requirement (EDMS 02-300-3)", 680.0), ("البيان العربي المطابق للمواصفة", 440.0)],
        [
            ["Transformer / Panel Rating", "2500 KVA | Panel Current = 4500 A (0.4 kV, 50 Hz)", "قدرة المحول 2500 ك.ف.أ | سعة اللوحة 4500 أمبير"],
            ["Main Breaker Type & Poles", "1 x 3-Pole Air Circuit Breaker (ACB)", "قاطع رئيسي هوائي ثلاثي الأوجه (ACB)"],
            ["Frame & Nominal Current (40 C)", "Frame = 5000 A | In = 5000 A at 40 C", "تيار الإطار والتيار المقنن 5000 أمبير عند 40 م"],
            ["Derated Current (Table-3 p.16)", "5500 A (at 25 C) | 4700 A (at 50 C) | 4125 A (at 60 C)", "التيار عند 50 م = 4700 أمبير (جدول الخفض ص 16)"],
            ["Breaking Capacity (Icu = Ics)", "65 kA at 400 V AC (Icu = Ics = 65 kA)", "سعة القطع الاسمية والخدمية 65 كيلو أمبير عند 400 فولت"],
            ["Insulation & Impulse Voltage", "Ui = 750 V | Uimp = 8 kV | 1-sec P.F. test = 2.5 kV", "جهد العزل 750 فولت | جهد الصمود الدفعي 8 ك.ف"],
            ["Overload (Thermal) Release Ir", "Adjustable (0.4 - 1.0) In at 40 C", "ضبط الحماية الحرارية ضد زيادة التيار (0.4 - 1) In"],
            ["Short-Circuit (Magnetic) Im", "Adjustable (2 - 10) In", "ضبط الحماية المغناطيسية ضد القصر (2 - 10) In"],
            ["Under-Voltage Release Coil", "WITHOUT Under-Voltage Coil (to avoid voltage dips)", "بدون ملف هبوط الجهد لتفادي الانخفاض في الجهد"],
            ["Shunt Trip & Flag Relays", "WITH Shunt Trip + 2 Flag Relays (Main CB & LBS)", "مزود بملف فصل + 2 فلاج ريليه (القاطع وسكينة المحول)"],
            ["Separate Cubicle Rule (>=500kVA)", "Separate cubicle; NO MCCBs/Switch-fuses below ACB", "خلية منفصلة ويمنع وضع قواطع أو سويتش فيوز أسفله"],
            ["Main Phase Busbars (L1,L2,L3)", "3000 mm2 Electrolytic Copper (>=99.99%, >=57 MS/m)", "مساحة مقطع بارات الأوجه 3000 مم2 نحاس 99.99%"],
            ["Neutral & Earth Busbars", "Neutral = 1800 mm2 | Earth (PE) >= 750 mm2 (>=25%)", "التعادل 1800 مم2 | الأرضي لا يقل عن 750 مم2"],
            ["Busbar Insulation & Support", "Heat-shrink tubes (R,Y,B,Black) + Sandwich Insulators", "عزل ريكم ملون + عازل المسطرة وعوازل بورسلين/راتنج"],
            ["Current Transformers (Item 8)", "2 Sets x 3 Ring CTs, 5000/5 A, Class 0.5, 10VA, Ith=10kA", "مجموعتان محولات تيار حلقية 5000/5 أمبير دقة 0.5"],
            ["Ammeters & Voltmeter (Items 9,10)", "3 Digital Ammeters (0-5000A) + 1 Digital Voltmeter (0-500V)", "3 أميتر رقمي (0-5000A) + فولتميتر رقمي (0-500V)"],
            ["Indication & Energy Meter (6.20)", "3 LED Lamps with fuses + KWH Meter Space & T.B.", "3 لمبات بيان LED بالفيوزات + مكان وروزيتات العداد"],
            ["Internal Wiring & Services", "PVC Copper >=2 mm2 (450/750V) + 220V Socket + >=9W LED", "أسلاك >=2 مم2 غير قابلة للاشتعال + بريزة ولمبة LED"],
        ],
        row_h=30.0,
    )

    # Box showing the 3 Approved Transformer-to-Panel Connection Methods (Clause 6.25)
    s.rect(tx, 340.0, 1640.0, 620.0, layer="EEHC-TITLE", width=3, fill="#f8fafc")
    s.rect(tx, 920.0, 1640.0, 40.0, layer="EEHC-TITLE", width=2, fill="#0f172a")
    s.text(tx + 820.0, 932.0, "APPROVED TRANSFORMER-TO-PANEL CONNECTION METHODS (CLAUSE 6.25 - PAGES 8 & 9)", height_mm=16.5, bold=True, align="center", color="#ffffff")

    methods = [
        (
            tx + 25,
            "METHOD A: TOP PROTRUDED BUSBAR TREE",
            "أولاً: الدخول من أعلى ببارات بارزة (شجرة)",
            [
                "• Busbars protrude 20 cm (200 mm) above roof.",
                "• Pass through insulated Paxolin plate.",
                "• Same CSA as main busbars (3000 / 1800 mm2).",
                "• Mandatory Busbar Tree (شجرة) for >= 500 KVA.",
            ],
        ),
        (
            tx + 565,
            "METHOD B: TOP PREFABRICATED BUSWAY",
            "ثانياً: الدخول من أعلى ببارات سابقة التجهيز",
            [
                "• Protruded busbars prepared for direct",
                "  connection to Busway Flange.",
                "• NO extension copper links permitted",
                "  (بدون استخدام أي نزلات نحاسية إضافية).",
            ],
        ),
        (
            tx + 1105,
            "METHOD C: BOTTOM CABLE ENTRY + TREE",
            "ثالثاً: الدخول من أسفل عن طريق كابلات",
            [
                "• Incoming busbar prepared with B.B. Tree.",
                "• Bottom holes: 1 hole per cable, Dia >= 8 cm.",
                "• Equipped with rubber gaskets (جوانات كاوتش)",
                "  to lock on cables & prevent reptiles entry.",
            ],
        ),
    ]
    for mx, m_en, m_ar, m_bullets in methods:
        s.rect(mx, 365.0, 510.0, 535.0, layer="EEHC-ENCLOSURE", width=2, fill="#ffffff")
        s.rect(mx, 835.0, 510.0, 65.0, layer="EEHC-ACB", width=2, fill="#dbeafe")
        s.text(mx + 255.0, 872.0, m_en, height_mm=13.5, layer="EEHC-TITLE", bold=True, align="center")
        s.text(mx + 255.0, 845.0, m_ar, height_mm=13.5, layer="EEHC-TEXT-AR", bold=True, align="center")
        for b_idx, b_txt in enumerate(m_bullets):
            s.text(mx + 18.0, 790.0 - b_idx * 34.0, b_txt, height_mm=13.0)

    # Mini schematic diagrams inside the 3 method boxes
    # Mini A: Top tree
    s.rect(tx + 180, 400, 200, 180, layer="EEHC-ENCLOSURE", width=2, fill="#f1f5f9")
    s.rect(tx + 220, 575, 120, 10, layer="EEHC-INSULATOR", width=2, fill="#d97706")
    for bx_o in (240, 280, 320):
        s.line(tx + bx_o, 500, tx + bx_o, 640, layer="EEHC-BUS-R", width=3)
        s.line(tx + bx_o - 12, 640, tx + bx_o + 12, 640, layer="EEHC-BUS-R", width=3)
    # Mini B: Busway flange
    s.rect(tx + 720, 400, 200, 180, layer="EEHC-ENCLOSURE", width=2, fill="#f1f5f9")
    s.rect(tx + 755, 580, 130, 65, layer="EEHC-FRAME", width=3, fill="#cbd5e1")
    s.text(tx + 820, 605, "BUSWAY FLANGE", height_mm=11.5, bold=True, align="center")
    # Mini C: Bottom cable entry with >=8cm gasketed holes
    s.rect(tx + 1260, 420, 200, 180, layer="EEHC-ENCLOSURE", width=2, fill="#f1f5f9")
    for cx_o in (1295, 1360, 1425):
        s.circle(tx + cx_o, 420, 20.0, layer="EEHC-PROHIBITED", width=2, fill="#334155")
        s.circle(tx + cx_o, 420, 10.0, layer="EEHC-ENCLOSURE", width=1, fill="#ffffff")
    s.text(tx + 1360, 380, "HOLES >= 8 cm + RUBBER GASKETS", height_mm=11.5, layer="EEHC-PROHIBITED", bold=True, align="center")

    dxf_p = out_dir / "01_Main_Breaker_ACB_5000A_Cubicle.dxf"
    png_p = out_dir / "01_Main_Breaker_ACB_5000A_Cubicle.png"
    s.save(dxf_p, png_p)
    return dxf_p, png_p


# ---------------------------------------------------------------------------
# SHEET 02: OUTGOING CUBICLES (OPTION A: 16x400A MCCB | OPTION B: 20x630A SW-FUSE)
# ---------------------------------------------------------------------------
def build_sheet_02_outgoing_cells(out_dir: Path) -> tuple[Path, Path]:
    s = EngineeringSheet(
        "02 / 06",
        "OUTGOING FEEDER CUBICLES: OPTION (A) 16x400A MCCBs & OPTION (B) 20x400/630A SIZE-3 VERTICAL SWITCH-FUSES",
        "لوحة تفصيلية لخلايا الخروج: الخيار الأول (16 قاطع مقولب 400A) والخيار الثاني (20 سويتش فيوز رأسي 630A بالنزلات 250 مم2)",
    )

    # 1. Left Assembly: 2 Cubicles of Outgoing MCCBs (16 x 400A MCCB, 8 per cubicle) (x=140..1340)
    ox, oy, cw, ch = 150.0, 350.0, 580.0, 2000.0
    s.text(ox + cw, oy + ch + 210.0, "OPTION (A): 16 x OUTGOING MOLDED CASE CIRCUIT BREAKERS (16 x 400A MCCB, 65 kA)", height_mm=18.5, layer="EEHC-TITLE", bold=True, align="center")
    s.text(ox + cw, oy + ch + 175.0, "الخيار الأول للخروجات: عدد 16 قاطع مقولب سعة 400 أمبير / 65 كيلو أمبير (موزعة على خليتين عرض كل خلية <= 1 متر)", height_mm=16.5, layer="EEHC-TEXT-AR", bold=True, align="center")

    for cub_idx in range(2):
        cx = ox + cub_idx * (cw + 30.0)
        # 4 Plinths (100 mm)
        s.rect(cx, oy - 100.0, cw, 100.0, layer="EEHC-FRAME", width=3, fill="#cbd5e1")
        for px in (cx + 15, cx + 175, cx + 355, cx + cw - 65):
            s.rect(px, oy - 100.0, 50.0, 100.0, layer="EEHC-ENCLOSURE", width=2, fill="#64748b")
        # Enclosure body
        s.rect(cx, oy, cw, ch, layer="EEHC-ENCLOSURE", width=4, fill="#ffffff")
        # Sloped roof
        s.line(cx - 10, oy + ch, cx + cw / 2, oy + ch + 55.0, layer="EEHC-ENCLOSURE", width=3)
        s.line(cx + cw / 2, oy + ch + 55.0, cx + cw + 10, oy + ch, layer="EEHC-ENCLOSURE", width=3)
        s.text(cx + cw / 2, oy + ch + 75.0, f"OUTGOING MCCB CUBICLE #{cub_idx + 1} (WIDTH <= 1000 mm)", height_mm=14.0, layer="EEHC-TITLE", bold=True, align="center")

        # Main Horizontal Busbars at Top (3000 mm2 Phases + Sandwich Insulator)
        for b_idx, lyr in enumerate(("EEHC-BUS-R", "EEHC-BUS-Y", "EEHC-BUS-B")):
            by_bar = oy + 1860.0 - b_idx * 45.0
            s.rect(cx + 15, by_bar, cw - 30, 24.0, layer=lyr, width=2, fill=s._color(lyr))
        # Sandwich Insulator on Main Busbar
        s.rect(cx + cw / 2 - 14, oy + 1745.0, 28.0, 160.0, layer="EEHC-INSULATOR", width=2, fill="#fde68a")

        # 8 MCCBs (4 rows x 2 columns) with >= 60 mm (6 cm) clearance between MCCBs & panel body
        for row in range(4):
            for col in range(2):
                f_num = cub_idx * 8 + row * 2 + col + 1
                mx = cx + 70.0 + col * 260.0
                my = oy + 1480.0 - row * 225.0
                mw, mh = 180.0, 150.0
                s.rect(mx, my, mw, mh, layer="EEHC-MCCB", width=3, fill="#eff6ff")
                # Operating handle only visible through >=1.25mm front cover
                s.rect(mx + 68, my + 55, 44.0, 42.0, layer="EEHC-ENCLOSURE", width=2, fill="#1e293b")
                s.text(mx + mw / 2, my + 120, f"QF-{f_num:02d}: MCCB 400A", height_mm=12.0, layer="EEHC-MCCB", bold=True, align="center")
                s.text(mx + mw / 2, my + 32, "Icu=Ics=65kA | Ir=(0.8-1)In", height_mm=10.5, bold=True, align="center")
                s.text(mx + mw / 2, my + 12, "Im=(5-10)In | Ui=750V, 8kV", height_mm=10.0, align="center")
                # Additional copper extension bars at output terminals (Clause 6.10)
                for p_idx, p_lyr in enumerate(("EEHC-BUS-R", "EEHC-BUS-Y", "EEHC-BUS-B")):
                    lx_bar = mx + 45.0 + p_idx * 45.0
                    s.rect(lx_bar - 5, my - 42.0, 10.0, 42.0, layer=p_lyr, width=1, fill=s._color(p_lyr))
                    s.circle(lx_bar, my - 32.0, 4.0, layer="EEHC-ENCLOSURE", fill="#ffffff")

        # Clearance annotations (>= 6 cm between MCCBs and panel wall)
        s.dim_h(cx, cx + 70.0, oy + 1650.0, ">=6cm")
        s.dim_h(cx + 250.0, cx + 330.0, oy + 1650.0, ">=6cm (CL.6.9)")

        # Cable Terminal Zone at bottom >= 550 mm (55 cm) from panel bottom (Clause 6.15)
        s.line(cx + 20, oy + 550.0, cx + cw - 20, oy + 550.0, layer="EEHC-PROHIBITED", width=2, dashed=True)
        s.text(cx + cw / 2, oy + 565.0, "CABLE TERMINALS HEIGHT >= 55 cm (550 mm) FROM BOTTOM (CL. 6.15)", height_mm=11.5, layer="EEHC-PROHIBITED", bold=True, align="center")
        # Neutral Busbar (1800 mm2, pre-drilled with galvanized bolts) & Earth Busbar (>=750 mm2)
        s.rect(cx + 45, oy + 320.0, cw - 90, 26.0, layer="EEHC-BUS-N", width=2, fill=s._color("EEHC-BUS-N"))
        s.text(cx + cw / 2, oy + 355.0, "NEUTRAL BUSBAR (1800 mm2 - BLACK) WITH GALVANIZED BOLTS/WASHERS (CL. 6.13)", height_mm=11.0, bold=True, align="center")
        s.rect(cx + 45, oy + 180.0, cw - 90, 20.0, layer="EEHC-BUS-PE", width=2, fill=s._color("EEHC-BUS-PE"))
        s.text(cx + cw / 2, oy + 210.0, "EARTH BUSBAR (>= 750 mm2) + EARTHING BOLTS ON ALL METAL PARTS (CL. 6.14)", height_mm=11.0, layer="EEHC-BUS-PE", bold=True, align="center")
        # Bottom cable openings Dia >= 8 cm with rubber gaskets
        for hole_x in range(int(cx + 80), int(cx + cw - 50), 95):
            s.circle(float(hole_x), oy + 65.0, 28.0, layer="EEHC-ENCLOSURE", width=2, fill="#475569")
            s.circle(float(hole_x), oy + 65.0, 16.0, layer="EEHC-ENCLOSURE", width=1, fill="#ffffff")
        s.text(cx + cw / 2, oy + 15.0, "BOTTOM CABLE HOLES DIA >= 8 cm WITH ANTI-REPTILE RUBBER GASKETS (CL. 6.25)", height_mm=11.0, bold=True, align="center")

    s.dim_v(ox - 55.0, oy, oy + 550.0, "TERMINALS >= 550 mm (55 cm)")

    # 2. Middle Assembly: Option (B) 20 x Vertical Switch-Fuse 400/630A Size 3 (x=1450..2550)
    sx, sy, sw, sh = 1460.0, 350.0, 1020.0, 2000.0
    s.text(sx + sw / 2, sy + sh + 210.0, "OPTION (B): 20 x VERTICAL SWITCH-FUSE 400/630A, SIZE 3, 500V, 50kA (AC-22B)", height_mm=18.5, layer="EEHC-SWFUSE", bold=True, align="center")
    s.text(sx + sw / 2, sy + sh + 175.0, "الخيار الثاني للخروجات: عدد 20 سويتش فيوز رأسي سعة 400/630 أمبير مقاس 3 بنزلات نحاسية 250 مم2", height_mm=16.5, layer="EEHC-TEXT-AR", bold=True, align="center")

    s.rect(sx, sy - 100.0, sw, 100.0, layer="EEHC-FRAME", width=3, fill="#cbd5e1")
    s.rect(sx, sy, sw, sh, layer="EEHC-ENCLOSURE", width=4, fill="#ffffff")
    s.line(sx - 10, sy + sh, sx + sw / 2, sy + sh + 55.0, layer="EEHC-ENCLOSURE", width=3)
    s.line(sx + sw / 2, sy + sh + 55.0, sx + sw + 10, sy + sh, layer="EEHC-ENCLOSURE", width=3)
    s.text(sx + sw / 2, sy + sh + 75.0, "TYPICAL 10-WAY VERTICAL SWITCH-FUSE SECTION (X2 SECTIONS = 20 WAYS TOTAL)", height_mm=14.0, layer="EEHC-SWFUSE", bold=True, align="center")

    # Main 3-Phase Horizontal Busbars spaced at exact 210 mm pole centers! (Guarantee Table 4 Item 6)
    bus_y_top = sy + 1780.0
    for b_idx, (b_lbl, lyr) in enumerate((("L1 (R - 3000 mm2)", "EEHC-BUS-R"), ("L2 (Y - 3000 mm2)", "EEHC-BUS-Y"), ("L3 (B - 3000 mm2)", "EEHC-BUS-B"))):
        by_pos = bus_y_top - b_idx * 210.0
        s.rect(sx + 30, by_pos - 16, sw - 60, 32.0, layer=lyr, width=2, fill=s._color(lyr))
    s.dim_v(sx + sw + 45.0, bus_y_top - 210.0, bus_y_top, "POLE CENTER = 210 mm")
    s.dim_v(sx + sw + 45.0, bus_y_top - 420.0, bus_y_top - 210.0, "POLE CENTER = 210 mm")

    # 10 Vertical Fuse-Switch Disconnectors (Width 72 mm each, Clearance >= 20 mm / 2 cm between units - Clause 6.9)
    for w_idx in range(10):
        wx = sx + 55.0 + w_idx * 92.0  # 72 mm width + 20 mm (2 cm) clearance
        wy = sy + 1180.0
        ww, wh = 72.0, 680.0
        s.rect(wx, wy, ww, wh, layer="EEHC-SWFUSE", width=2, fill="#f5f3ff")
        # 3 Fuse Phases inside vertical switch-fuse
        for p_i in range(3):
            fy = bus_y_top - p_i * 210.0
            s.rect(wx + 16, fy - 45, 40.0, 90.0, layer="EEHC-ENCLOSURE", width=1, fill="#ddd6fe")
        s.text(wx + ww / 2, wy + wh - 30, f"SF{w_idx + 1:02d}", height_mm=11.5, layer="EEHC-SWFUSE", bold=True, align="center")
        s.text(wx + ww / 2, wy + 20, "630A", height_mm=11.0, bold=True, align="center")

        # Mandatory Copper Droppers (CSA = 250 mm2, heat-shrink insulated) down to >=550mm terminal zone (Clause 6.11)
        for p_i, p_lyr in enumerate(("EEHC-BUS-R", "EEHC-BUS-Y", "EEHC-BUS-B")):
            dx = wx + 16.0 + p_i * 20.0
            s.rect(dx - 3, sy + 600.0, 6.0, 580.0, layer=p_lyr, width=1, fill=s._color(p_lyr))
            # Galvanized 12mm (1/2") bolt + 2 flat washers + 1 spring washer + 1 nut at cable terminal (Clause 6.12)
            s.circle(dx, sy + 615.0, 6.0, layer="EEHC-ENCLOSURE", width=2, fill="#fde047")

        # Cable Holder Dia >= 8 cm (80 mm) below each switch-fuse feeder (Clause 6.16)
        s.circle(wx + ww / 2, sy + 410.0, 32.0, layer="EEHC-FRAME", width=2, fill="#e2e8f0")
        s.circle(wx + ww / 2, sy + 410.0, 22.0, layer="EEHC-ENCLOSURE", width=1, fill="#ffffff")

    s.dim_h(sx + 55.0 + 72.0, sx + 55.0 + 92.0, sy + 1890.0, ">=2cm (CL.6.9)")
    s.rect(sx + 65.0, sy + 875.0, sw - 130.0, 72.0, layer="EEHC-PROHIBITED", width=2, fill="#fff1f2")
    s.text(sx + sw / 2, sy + 918.0, "MANDATORY COPPER DROPPERS: CSA = 250 mm2 (HEAT-SHRINK INSULATED) TO PROTECT", height_mm=13.0, layer="EEHC-PROHIBITED", bold=True, align="center")
    s.text(sx + sw / 2, sy + 886.0, "SWITCH-FUSES FROM OVERHEATING DAMAGE (CLAUSE 6.11 - نزلات نحاسية بمساحة مقطع 250 مم2)", height_mm=13.0, layer="EEHC-PROHIBITED", bold=True, align="center")
    s.text(sx + sw / 2, sy + 560.0, "CABLE LUG TERMINALS: GALVANIZED BOLT 12 mm (0.5 INCH) + 2 FLAT WASHERS + 1 SPRING WASHER + 1 NUT (CL. 6.12) | HEIGHT >= 55 cm", height_mm=12.0, layer="EEHC-TITLE", bold=True, align="center")
    s.text(sx + sw / 2, sy + 350.0, "CABLE HOLDERS (قفزان تثبيت الكابلات): DIAMETER >= 8 cm (80 mm) FOR SWITCH-FUSE PANELS (CLAUSE 6.16)", height_mm=13.0, layer="EEHC-SWFUSE", bold=True, align="center")

    # Neutral & Earth bars at bottom of Switch-Fuse section
    s.rect(sx + 45, sy + 240.0, sw - 90, 26.0, layer="EEHC-BUS-N", width=2, fill=s._color("EEHC-BUS-N"))
    s.rect(sx + 45, sy + 140.0, sw - 90, 20.0, layer="EEHC-BUS-PE", width=2, fill=s._color("EEHC-BUS-PE"))

    # 3. Right Column: Full Specification Tables for Outgoing MCCBs & Switch-Fuses (x=2610..4090)
    tx = 2610.0
    s.table(
        tx,
        2620.0,
        "OPTION (A): OUTGOING MCCBs MANDATORY SPECIFICATIONS (2500 KVA - TABLE 1 PAGE 12 & GUARANTEE TABLE 3)",
        [("Parameter", 460.0), ("Mandatory Requirement (2500 KVA Panel)", 600.0), ("البيان العربي المطابق", 420.0)],
        [
            ["Number & Type of Outgoings", "16 x 3-Pole Molded Case C.B. (MCCB) (EDMS 19-303)", "عدد 16 قاطع فرعي مقولب ثلاثي الأوجه"],
            ["Frame & Nominal Current (40 C)", "Frame = 400 A | In = 400 A at 40 C ambient", "تيار الإطار والتيار المقنن 400 أمبير عند 40 م"],
            ["Derated Current (Table-3 p.16)", "440 A (at 25 C) | 372 A (at 50 C) | 340 A (at 60 C)", "التيار عند 50 م = 372 أمبير (جدول الخفض)"],
            ["Nominal Breaking Capacity", "Icu = Ics = 65 kA at 400 V AC", "سعة القطع الاسمية والخدمية 65 ك.أ عند 400 فولت"],
            ["Thermal & Short-Circuit Release", "Thermal: (0.8 - 1.0) In | Magnetic: (5 - 10) In", "حراري (0.8-1) In | مغناطيسي (5-10) In"],
            ["Insulation & Impulse Voltage", "Ui = 750 V | Uimp = 8 kV", "جهد العزل 750 فولت | الصمود الدفعي 8 ك.ف"],
            ["Clearances (Clause 6.9)", ">= 6 cm between MCCBs and between MCCB & panel", "المسافة بين القواطع وجسم اللوحة >= 6 سم"],
            ["Output Extension Bars (Cl. 6.10)", "Additional insulated copper bars on insulators", "قضبان نحاسية إضافية بأطراف الخروج معزولة"],
            ["Cable Terminal Height (Cl. 6.15)", "Not less than 55 cm (550 mm) from panel bottom", "ارتفاع أطراف رباط الكابلات >= 55 سم من الأسفل"],
        ],
        row_h=30.0,
    )

    s.table(
        tx,
        1520.0,
        "OPTION (B): 630A SIZE-3 VERTICAL SWITCH-FUSE SPECIFICATIONS (PAGE 14 & GUARANTEE TABLE 4 PAGE 20)",
        [("Guarantee Table 4 Item", 460.0), ("Mandatory Requirement (EDMS 19-302 / 02-300-3)", 600.0), ("البيان العربي المطابق", 420.0)],
        [
            ["Quantity & Size (Table-1 Item 7)", "20 x Vertical Fuse-Switch 400/630A, Size 3", "عدد 20 سويتش فيوز رأسي 400/630 أمبير مقاس 3"],
            ["Applicable Standards", "IEC 60947-1, IEC 60947-3, EDMS 19-302 & 19-300", "المواصفات القياسية IEC 60947-1/3 و EDMS"],
            ["Rated Voltage & Breaking Cap.", "500 V, 50 Hz | Conditional Short-Circuit = 50 kA", "الجهد 500 فولت | سعة القطع المشروطة 50 ك.أ"],
            ["Busbar Pole Center Distance", "210 mm (Exact pole-to-pole center spacing)", "المسافة بين مراكز البارات = 210 مم"],
            ["Operating Cycles (Endurance)", ">= 200 cycles (with current) | >= 800 (no current)", ">= 200 دورة بحمل | >= 800 دورة بدون حمل"],
            ["Utilization & Overvoltage Cat.", "AC-22B | Overvoltage Category III | Pollution Deg. 3", "فئة التشغيل AC-22B | فئة الجهد الزائد III"],
            ["Arcing Chamber & Flammability", "Arcing chamber included | All insulation V0 class", "غرفة إخماد قوس | جميع العوازل مقاومة للاشتعال V0"],
            ["Protection & Mechanical Shock", "IP20 protection degree | IK07 mechanical shocks", "درجة الحماية IP20 | الصدمات الميكانيكية IK07"],
            ["Contacts Material & Coating", "Copper >= 57 MS/m | Silver coating thickness 5 um", "نحاس >= 57 سيمنز مطلي فضة بسمك 5 ميكرومتر"],
            ["Copper Droppers (Clause 6.11)", "CSA = 250 mm2 copper droppers to protect unit", "نزلات نحاسية بمساحة مقطع 250 مم2 لربط الكابلات"],
            ["Cable Terminal Bolts (Cl. 6.12)", "Galvanized 12 mm (0.5\") bolt + 2 flat + 1 spring + nut", "مسمار مجلفن 12 مم (نصف بوصة) بوردتين وسوستة"],
            ["Clearance & Cable Holders", ">= 2 cm between units | Cable holders Dia >= 8 cm", "مسافة >= 2 سم بين الوحدات | قفزان بقطر >= 8 سم"],
        ],
        row_h=30.0,
    )

    dxf_p = out_dir / "02_Outgoing_Cells_16xMCCB_or_20xSwitchFuse.dxf"
    png_p = out_dir / "02_Outgoing_Cells_16xMCCB_or_20xSwitchFuse.png"
    s.save(dxf_p, png_p)
    return dxf_p, png_p


# ---------------------------------------------------------------------------
# SHEET 03: FIXED CAPACITOR (125 KVAR) & STREET LIGHTING (63A + PHOTOCELL)
# ---------------------------------------------------------------------------
def build_sheet_03_capacitor_and_lighting(out_dir: Path) -> tuple[Path, Path]:
    s = EngineeringSheet(
        "03 / 06",
        "FIXED CAPACITOR CUBICLE (125 KVAR / 320A MCCB) & STREET LIGHTING SECTION (63A + CdS PHOTOCELL)",
        "لوحة تفصيلية لخلية المكثف الثابت (125 ك.فار) وقسم الإنارة العامة (63 أمبير) والخلية الضوئية (CdS Photocell)",
    )

    # 1. Left View: Fixed Capacitor Bank & Street Lighting Cubicle (x=160..1120, y=350..2350)
    cx, cy, cw, ch = 180.0, 350.0, 920.0, 2000.0
    s.rect(cx, cy - 100.0, cw, 100.0, layer="EEHC-FRAME", width=3, fill="#cbd5e1")
    for px in (cx + 20, cx + 260, cx + 580, cx + cw - 100):
        s.rect(px, cy - 100.0, 80.0, 100.0, layer="EEHC-ENCLOSURE", width=2, fill="#64748b")
    s.rect(cx, cy, cw, ch, layer="EEHC-ENCLOSURE", width=4, fill="#ffffff")
    # Sloped Roof (6 deg)
    s.line(cx - 15, cy + ch, cx + cw / 2, cy + ch + 60.0, layer="EEHC-ENCLOSURE", width=3)
    s.line(cx + cw / 2, cy + ch + 60.0, cx + cw + 15, cy + ch, layer="EEHC-ENCLOSURE", width=3)

    # Top Mounted Outdoor Photoelectric Cell Assembly (Page 13 Detail)
    pc_x, pc_y = cx + cw - 190.0, cy + ch + 25.0
    # Corrosion-resistant bracket fixed on top of steel structure
    s.rect(pc_x - 35, pc_y - 25, 70.0, 16.0, layer="EEHC-FRAME", width=2, fill="#475569")
    s.rect(pc_x - 8, pc_y - 9, 16.0, 65.0, layer="EEHC-FRAME", width=2, fill="#475569")
    s.rect(pc_x - 45, pc_y + 56, 90.0, 14.0, layer="EEHC-FRAME", width=2, fill="#475569")
    # Aluminum plug-in twist-lock base/container + Gasket
    s.rect(pc_x - 42, pc_y + 70, 84.0, 36.0, layer="EEHC-ENCLOSURE", width=2, fill="#94a3b8")
    # High-impact UV-stabilized acrylic housing & window with CdS sensor
    s.rect(pc_x - 38, pc_y + 106, 76.0, 82.0, layer="EEHC-LIGHTING", width=3, fill="#ffedd5")
    s.rect(pc_x - 18, pc_y + 128, 36.0, 38.0, layer="EEHC-ACB", width=2, fill="#fef08a")
    s.text(pc_x, pc_y + 142, "CdS", height_mm=12.0, layer="EEHC-LIGHTING", bold=True, align="center")
    s.text(pc_x, pc_y + 205, "360 DEG ROTATION PHOTOCELL + ALUMINUM TWIST-LOCK BASE", height_mm=12.5, layer="EEHC-LIGHTING", bold=True, align="center")
    s.text(pc_x, pc_y + 232, "(خلية ضوئية CdS بقاعدة ألومنيوم وحامل أعلى الهيكل الصلب - دوران 360 درجة)", height_mm=12.5, layer="EEHC-TEXT-AR", bold=True, align="center")

    # Main Busbars at Top (3000 mm2)
    for b_idx, lyr in enumerate(("EEHC-BUS-R", "EEHC-BUS-Y", "EEHC-BUS-B")):
        s.rect(cx + 25, cy + 1860 - b_idx * 45, cw - 50, 24.0, layer=lyr, width=2, fill=s._color(lyr))

    # Upper Compartment: Street Lighting Section (Page 13 Table-1 Item 6)
    s.line(cx + 20, cy + 1220, cx + cw - 20, cy + 1220, layer="EEHC-DOOR", width=2)
    s.text(cx + cw / 2, cy + 1715, "STREET LIGHTING COMPARTMENT (قسم الإنارة العامة - صفحة 13 بند 6)", height_mm=15.5, layer="EEHC-LIGHTING", bold=True, align="center")

    # 1. Street Lighting MCCB (63A, Frame 63A, Icu=Ics=65kA, Ir=(0.8-1)In)
    s.rect(cx + 65, cy + 1430, 230.0, 240.0, layer="EEHC-MCCB", width=3, fill="#eff6ff")
    s.text(cx + 180, cy + 1625, "1x LIGHTING MCCB", height_mm=13.5, layer="EEHC-MCCB", bold=True, align="center")
    s.text(cx + 180, cy + 1590, "Frame = 63 A (at 40 C)", height_mm=12.5, bold=True, align="center")
    s.text(cx + 180, cy + 1560, "In = 63 A (at 40 C)", height_mm=12.5, bold=True, align="center")
    s.text(cx + 180, cy + 1530, "Icu = Ics = 65 kA (400V)", height_mm=12.5, layer="EEHC-PROHIBITED", bold=True, align="center")
    s.text(cx + 180, cy + 1500, "Thermal: (0.8 - 1.0) In", height_mm=12.0, bold=True, align="center")

    # 2. 3-Phase Contactor (In = 63A at 40C, Coil 220V AC, 50Hz)
    s.rect(cx + 345, cy + 1430, 240.0, 240.0, layer="EEHC-LIGHTING", width=3, fill="#fff7ed")
    s.text(cx + 465, cy + 1625, "1x 3PH CONTACTOR", height_mm=13.5, layer="EEHC-LIGHTING", bold=True, align="center")
    s.text(cx + 465, cy + 1590, "In = 63 A (at 40 C)", height_mm=12.5, bold=True, align="center")
    s.text(cx + 465, cy + 1560, "Coil: 220 V AC, 50 Hz", height_mm=12.5, bold=True, align="center")
    s.text(cx + 465, cy + 1530, "Category AC-3 (EEHC)", height_mm=12.0, bold=True, align="center")
    s.text(cx + 465, cy + 1500, "(كونتاكتور ثلاثي 63 أمبير)", height_mm=12.0, layer="EEHC-TEXT-AR", bold=True, align="center")

    # 3. 1P MCB 10A + Digital Timer
    s.rect(cx + 630, cy + 1555, 225.0, 115.0, layer="EEHC-MCCB", width=2, fill="#f0fdf4")
    s.text(cx + 742.5, cy + 1625, "1P MCB 10 A", height_mm=13.0, layer="EEHC-CAPACITOR", bold=True, align="center")
    s.text(cx + 742.5, cy + 1590, "Lighting Control Circuit", height_mm=11.5, bold=True, align="center")
    s.rect(cx + 630, cy + 1430, 225.0, 110.0, layer="EEHC-LIGHTING", width=2, fill="#fefce8")
    s.text(cx + 742.5, cy + 1495, "DIGITAL TIMER /", height_mm=12.5, layer="EEHC-LIGHTING", bold=True, align="center")
    s.text(cx + 742.5, cy + 1465, "PHOTOCELL SELECTOR", height_mm=12.0, bold=True, align="center")

    # Lower Compartment: Fixed Capacitor Bank (125 KVAR + 320A MCCB) (y = cy+25 .. cy+1220)
    s.text(cx + cw / 2, cy + 1170, "FIXED CAPACITOR COMPARTMENT (خلية المكثف الثابت 125 ك.فار - بند 5-3 وجدول 1 بنود 12-14)", height_mm=15.0, layer="EEHC-CAPACITOR", bold=True, align="center")

    # Capacitor Protection MCCB (320A at 40C, Breaking capacity 65kA at 400V)
    s.rect(cx + 310, cy + 900, 300.0, 230.0, layer="EEHC-MCCB", width=3, fill="#eff6ff")
    s.text(cx + 460, cy + 1085, "CAPACITOR MCCB (بند 13، 14)", height_mm=14.0, layer="EEHC-MCCB", bold=True, align="center")
    s.text(cx + 460, cy + 1045, "In = 320 A at 40 C", height_mm=14.0, bold=True, align="center")
    s.text(cx + 460, cy + 1010, "Breaking Capacity = 65 kA (400V)", height_mm=13.5, layer="EEHC-PROHIBITED", bold=True, align="center")
    s.text(cx + 460, cy + 975, "3-Pole Molded Case Circuit Breaker", height_mm=12.5, bold=True, align="center")
    s.text(cx + 460, cy + 940, "Connected to Main Busbars via Cu Links", height_mm=12.0, align="center")

    # 125 KVAR Dry Self-Healing Delta Capacitor Bank Unit
    s.rect(cx + 120, cy + 280, 680.0, 540.0, layer="EEHC-CAPACITOR", width=4, fill="#ecfdf5")
    # 3 Phase Capacitor Cylinders + Delta Connection + Discharge Resistors + Overpressure Disconnector
    for p_idx, (c_x, p_name) in enumerate(((cx + 230, "C-L1L2"), (cx + 460, "C-L2L3"), (cx + 690, "C-L3L1"))):
        s.rect(c_x - 65, cy + 360, 130.0, 280.0, layer="EEHC-CAPACITOR", width=3, fill="#d1fae5")
        # Ceramic/heavy-duty high-current terminals at top
        s.rect(c_x - 35, cy + 640, 22.0, 35.0, layer="EEHC-INSULATOR", width=2, fill="#b45309")
        s.rect(c_x + 13, cy + 640, 22.0, 35.0, layer="EEHC-INSULATOR", width=2, fill="#b45309")
        # Discharge resistor symbol across terminals
        s.rect(c_x - 18, cy + 685, 36.0, 18.0, layer="EEHC-PROHIBITED", width=2, fill="#fee2e2")
        s.text(c_x, cy + 712, "R-DISCH (<=50V / 60s)", height_mm=10.0, layer="EEHC-PROHIBITED", bold=True, align="center")
        s.text(c_x, cy + 530, p_name, height_mm=14.0, layer="EEHC-CAPACITOR", bold=True, align="center")
        s.text(c_x, cy + 495, "DRY SELF-HEALING", height_mm=11.0, bold=True, align="center")
        s.text(c_x, cy + 465, "Un = 525 V", height_mm=12.0, bold=True, align="center")
        s.text(c_x, cy + 435, "Uop = 400 V, 50Hz", height_mm=11.5, bold=True, align="center")
        s.text(c_x, cy + 400, "3-Ph Overpressure", height_mm=10.5, layer="EEHC-PROHIBITED", bold=True, align="center")
        s.text(c_x, cy + 375, "Protection Included", height_mm=10.5, layer="EEHC-PROHIBITED", bold=True, align="center")

    s.text(cx + 460, cy + 765, "FIXED CAPACITOR UNIT: 125 KVAR AT 400 V (DELTA CONNECTION - توصيل دلتا)", height_mm=15.0, layer="EEHC-CAPACITOR", bold=True, align="center")
    s.text(cx + 460, cy + 315, "Losses: Dielectric < 0.2 W/KVAR, Total < 0.5 W/KVAR | Imax >= 1.3x In | Inrush >= 200x In | Life >= 130,000 hrs (Class D)", height_mm=11.5, bold=True, align="center")

    # 2. Middle Schematic Diagram: Internal Delta Capacitor + Street Lighting Control Wiring (x=1200..2260)
    wx, wy, ww, wh = 1200.0, 350.0, 1020.0, 2000.0
    s.rect(wx, wy, ww, wh, layer="EEHC-TITLE", width=3, fill="#f8fafc")
    s.rect(wx, wy + wh - 50.0, ww, 50.0, layer="EEHC-TITLE", width=2, fill="#1e3a8a")
    s.text(wx + ww / 2, wy + wh - 34.0, "SCHEMATIC A: STREET LIGHTING CONTROL (FIG. 1) & SCHEMATIC B: 125 KVAR DELTA CAPACITOR", height_mm=14.5, bold=True, align="center", color="#ffffff")

    # Upper Half of Middle Box: Street Lighting & Photocell Wiring Schematic
    s.text(wx + ww / 2, wy + 1880, "A. STREET LIGHTING FEEDER & PHOTOCELL CONTROL WIRING (مخطط دائرة الإنارة والخلية الضوئية)", height_mm=15.5, layer="EEHC-LIGHTING", bold=True, align="center")
    # 3-Phase Busbar lines
    for p_i, (p_lbl, p_col) in enumerate((("L1", "EEHC-BUS-R"), ("L2", "EEHC-BUS-Y"), ("L3", "EEHC-BUS-B"), ("N", "EEHC-BUS-N"))):
        py_line = wy + 1820 - p_i * 25
        s.line(wx + 60, py_line, wx + ww - 60, py_line, layer=p_col, width=3)
        s.text(wx + 35, py_line - 5, p_lbl, height_mm=13.0, layer=p_col, bold=True)

    # Power branch: 63A MCCB -> 63A Contactor -> Outgoing Street Lighting Feeder
    s.line(wx + 240, wy + 1820, wx + 240, wy + 1650, layer="EEHC-MCCB", width=3)
    s.rect(wx + 160, wy + 1530, 160.0, 120.0, layer="EEHC-MCCB", width=3, fill="#eff6ff")
    s.text(wx + 240, wy + 1605, "3P MCCB 63A", height_mm=13.0, layer="EEHC-MCCB", bold=True, align="center")
    s.text(wx + 240, wy + 1575, "Icu=Ics=65kA", height_mm=12.0, bold=True, align="center")
    s.text(wx + 240, wy + 1545, "Ir=(0.8-1)In", height_mm=11.5, align="center")

    s.line(wx + 240, wy + 1530, wx + 240, wy + 1430, layer="EEHC-LIGHTING", width=3)
    s.rect(wx + 160, wy + 1300, 160.0, 130.0, layer="EEHC-LIGHTING", width=3, fill="#fff7ed")
    s.text(wx + 240, wy + 1390, "3P CONTACTOR", height_mm=13.0, layer="EEHC-LIGHTING", bold=True, align="center")
    s.text(wx + 240, wy + 1360, "In = 63 A (40 C)", height_mm=12.0, bold=True, align="center")
    s.text(wx + 240, wy + 1330, "Coil: 220V, 50Hz", height_mm=12.0, bold=True, align="center")
    s.line(wx + 240, wy + 1300, wx + 240, wy + 1190, layer="EEHC-LIGHTING", width=3)
    s.text(wx + 240, wy + 1160, "TO STREET LIGHTING FEEDER (3PH + N)", height_mm=13.0, layer="EEHC-LIGHTING", bold=True, align="center")

    # Control branch: 1P MCB 10A -> Digital Timer / CdS Photocell (NC in darkness) -> Contactor Coil A1-A2
    s.line(wx + 560, wy + 1820, wx + 560, wy + 1660, layer="EEHC-BUS-R", width=2)
    s.rect(wx + 490, wy + 1560, 140.0, 100.0, layer="EEHC-CAPACITOR", width=2, fill="#f0fdf4")
    s.text(wx + 560, wy + 1615, "1P MCB 10 A", height_mm=13.0, layer="EEHC-CAPACITOR", bold=True, align="center")
    s.text(wx + 560, wy + 1580, "Control Prot.", height_mm=11.5, align="center")
    s.line(wx + 560, wy + 1560, wx + 560, wy + 1480, layer="EEHC-BUS-R", width=2)
    s.rect(wx + 450, wy + 1320, 260.0, 160.0, layer="EEHC-LIGHTING", width=3, fill="#ffedd5")
    s.text(wx + 580, wy + 1445, "OUTDOOR PHOTOCELL (CdS)", height_mm=13.0, layer="EEHC-LIGHTING", bold=True, align="center")
    s.text(wx + 580, wy + 1415, "OR DIGITAL TIMER", height_mm=12.5, layer="EEHC-LIGHTING", bold=True, align="center")
    s.text(wx + 580, wy + 1385, "220V, 50Hz, 1000W, NC Contact", height_mm=11.5, bold=True, align="center")
    s.text(wx + 580, wy + 1355, "Alu Twist-Lock + 360 Bracket", height_mm=11.5, bold=True, align="center")
    # Wire from Photocell LOAD/OUT to Contactor Coil A1
    s.line(wx + 450, wy + 1365, wx + 320, wy + 1365, layer="EEHC-PROHIBITED", width=2, dashed=True)
    # Neutral wire to Photocell N and Contactor A2
    s.line(wx + 820, wy + 1745, wx + 820, wy + 1250, layer="EEHC-BUS-N", width=2)
    s.line(wx + 710, wy + 1365, wx + 820, wy + 1365, layer="EEHC-BUS-N", width=2)
    s.line(wx + 240, wy + 1250, wx + 820, wy + 1250, layer="EEHC-BUS-N", width=2)

    # Divider line
    s.line(wx + 30, wy + 1110, wx + ww - 30, wy + 1110, layer="EEHC-TITLE", width=2)

    # Lower Half of Middle Box: 125 KVAR Delta Capacitor Internal Schematic
    s.text(wx + ww / 2, wy + 1060, "B. 125 KVAR DELTA CAPACITOR BANK & OVERPRESSURE DISCONNECTOR SCHEMATIC", height_mm=15.0, layer="EEHC-CAPACITOR", bold=True, align="center")
    # Delta triangle vertices
    v_top = (wx + 510.0, wy + 920.0)
    v_left = (wx + 230.0, wy + 460.0)
    v_right = (wx + 790.0, wy + 460.0)
    s.line(v_top[0], v_top[1], v_left[0], v_left[1], layer="EEHC-CAPACITOR", width=4)
    s.line(v_left[0], v_left[1], v_right[0], v_right[1], layer="EEHC-CAPACITOR", width=4)
    s.line(v_right[0], v_right[1], v_top[0], v_top[1], layer="EEHC-CAPACITOR", width=4)
    s.circle(v_top[0], v_top[1], 10.0, layer="EEHC-BUS-R", fill=s._color("EEHC-BUS-R"))
    s.circle(v_left[0], v_left[1], 10.0, layer="EEHC-BUS-Y", fill=s._color("EEHC-BUS-Y"))
    s.circle(v_right[0], v_right[1], 10.0, layer="EEHC-BUS-B", fill=s._color("EEHC-BUS-B"))
    s.text(v_top[0], v_top[1] + 22, "L1 (FROM 320A / 65kA MCCB)", height_mm=13.5, layer="EEHC-BUS-R", bold=True, align="center")
    s.text(v_left[0] - 20, v_left[1] - 30, "L2 (FROM 320A MCCB)", height_mm=13.5, layer="EEHC-BUS-Y", bold=True, align="left")
    s.text(v_right[0] + 20, v_right[1] - 30, "L3 (FROM 320A MCCB)", height_mm=13.5, layer="EEHC-BUS-B", bold=True, align="right")

    # Central info box inside Delta triangle
    s.rect(wx + 340, wy + 520, 340.0, 210.0, layer="EEHC-CAPACITOR", width=2, fill="#ffffff")
    s.text(wx + 510, wy + 690, "DELTA (Δ) CONNECTION", height_mm=14.0, layer="EEHC-CAPACITOR", bold=True, align="center")
    s.text(wx + 510, wy + 655, "Qtot = 125 KVAR @ 400V, 50Hz", height_mm=13.0, bold=True, align="center")
    s.text(wx + 510, wy + 622, "Design Voltage Un = 525 V", height_mm=13.0, layer="EEHC-PROHIBITED", bold=True, align="center")
    s.text(wx + 510, wy + 590, "3x Discharge Resistors (<=50V/60s)", height_mm=12.0, bold=True, align="center")
    s.text(wx + 510, wy + 558, "3-Ph Overpressure Disconnector", height_mm=12.0, bold=True, align="center")

    # 3. Right Column: Complete Mandatory Tables for Fixed Capacitor (16 Clauses) & Street Lighting (x=2290..4090)
    tx = 2290.0
    s.table(
        tx,
        2620.0,
        "LOW VOLTAGE FIXED CAPACITOR MANDATORY SPECIFICATION (CLAUSE 5.3 PAGES 6-7, TABLE-1 & GUARANTEE TABLE 5)",
        [("Cl. / No.", 140.0), ("Parameter", 430.0), ("Mandatory Requirement (EDMS 02-300-3)", 660.0), ("البيان العربي المطابق", 540.0)],
        [
            ["Table-1", "Capacitor Rated Power (2500 KVA)", "125 KVAR at rated operating voltage 400 V", "سعة المكثف للوحة 2500 ك.ف.أ = 125 ك.فار عند 400 فولت"],
            ["Table-1", "Protection MCCB Rating & Icu", "In = 320 A at 40 C | Breaking Capacity = 65 kA at 400 V", "قاطع حماية المكثف: 320 أمبير عند 40 م | سعة قطع 65 ك.أ"],
            ["5.3 - 1", "Capacitor Type & Technology", "Dry type with self-healing properties", "من النوع الجاف وله خاصية الالتئام الذاتي"],
            ["5.3 - 2,3", "Dielectric & Terminals", "Thickness per voltage; high-current terminals, thermal stability", "سمك العزل طبقاً للجهد؛ أطراف تتحمل تيارات وحرارة عالية"],
            ["5.3 - 4,5", "Design / Operating Voltage & Freq.", "Design Voltage (Un) = 525 V | Operating = 400 V | 50 Hz", "الجهد التصميمي 525 فولت | جهد التشغيل 400 فولت | 50 هرتز"],
            ["5.3 - 6", "Discharge Resistors", "Discharges to <= 50 V within 60 sec (or < sqrt(2) Un)", "تفريغ الشحنة خلال 60 ثانية إلى أقل من 50 فولت"],
            ["5.3 - 7", "Dielectric & Total Losses", "Dielectric losses < 0.2 W/KVAR | Total losses < 0.5 W/KVAR", "مفاقيد العزل < 0.2 وات/ك.فار | المفاقيد الكلية < 0.5 وات/ك.فار"],
            ["5.3 - 8", "Max Continuous Current", "Not less than 1.3 times design current (at Un & fn)", "أقصى تيار مستمر مسموح به لا يقل عن 1.3 مرة من التيار التصميمي"],
            ["5.3 - 9", "Max Inrush Current", "Not less than 200 times design current (at Un & fn)", "أقصى تيار دفعي مسموح به لا يقل عن 200 مرة من التيار التصميمي"],
            ["5.3 - 10", "Capacitance Tolerance", "-5% to +10%", "السماحية في قيمة سعة المكثف من -5% حتى +10%"],
            ["5.3 - 11,12", "Ambient Temp. & Humidity", "Temperature: -5 C to +55 C | Max Relative Humidity: 95%", "درجة حرارة البيئة المحيطة: -5 إلى 55 م | الرطوبة 95%"],
            ["5.3 - 13", "Operating Lifetime", "Not less than 130,000 hours at continuous normal op. (Class D)", "عمر المكثف لا يقل عن 130,000 ساعة عند درجة حرارة (Class D)"],
            ["5.3 - 14", "Overpressure Protection", "Internal overpressure protection for 3-phase (vertical/horizontal)", "وقاية ضد ارتفاع الضغط بداخله على الأوجه الثلاثة رأسياً أو أفقياً"],
            ["5.3 - 15", "Internal Connection", "Delta (Δ) connection", "يتم توصيل وحدة المكثف في شكل دلتا (Delta)"],
            ["5.3 - 16", "Insulation Level & Tests", "2.5 kV r.m.s. for 1 min (phases to body) | Impulse: 8 kV (1.2/50us)", "اختبار 2.5 ك.ف لمدة دقيقة | جهد الثبوت الدفعي 8 ك.ف (1.2/50us)"],
        ],
        row_h=30.0,
    )

    s.table(
        tx,
        1060.0,
        "STREET LIGHTING & PHOTOCELL MANDATORY SPECIFICATION (PAGE 13 - OPTION AS EDC REQUIREMENTS)",
        [("Component", 420.0), ("Mandatory Requirement (2500 KVA Panel)", 750.0), ("البيان العربي المطابق", 600.0)],
        [
            ["Lighting Feeder MCCB", "1 x 3P MCCB, Frame = 63 A, In = 63 A (40 C), Icu=Ics = 65 kA, Ir=(0.8-1)In", "قاطع فرعي مقولب 63 أمبير، سعة قطع 65 ك.أ، حراري (0.8-1)In"],
            ["3-Phase Contactor", "1 x 3Ph Contactor, In = 63 A at 40 C, Coil Operating Voltage = 220 V", "كونتاكتور ثلاثي الأوجه سعة 63 أمبير عند 40 م، بوبينة 220 فولت"],
            ["Lighting Control MCB", "1 x Single-Phase MCB 10 A for lighting control circuit", "قاطع أحادي 10 أمبير لحماية دائرة التحكم في الإنارة"],
            ["Control Unit", "1 x Digital Timer OR Outdoor Photoelectric Cell (Photocell)", "تايمر رقمي أو خلية ضوئية خارجية"],
            ["Photocell Sensor & Rating", "Cadmium Sulfide (CdS) type | Rated 220 V, 50 c/s, 1000 W | Normally Closed", "خلية كبريتيد الكادميوم (CdS) | 220 فولت، 50 هرتز، 1000 وات، مغلقة طبيعياً"],
            ["Photocell Housing & Window", "High-impact acrylic housing and window with UV inhibitor", "غلاف ونافذة من الأكريليك عالي المقاومة للصدمات مع مانع للأشعة"],
            ["Photocell Base & Rotation", "Complete with Aluminum plug-in twist-lock container + bracket on steel top, 360 deg", "حاضن/قاعدة ألومنيوم بقفل دوراني مع حامل أعلى الهيكل الصلب يدور 360 درجة"],
        ],
        row_h=30.0,
    )

    dxf_p = out_dir / "03_Fixed_Capacitor_125kVAR_and_Street_Lighting_63A.dxf"
    png_p = out_dir / "03_Fixed_Capacitor_125kVAR_and_Street_Lighting_63A.png"
    s.save(dxf_p, png_p)
    return dxf_p, png_p


# ---------------------------------------------------------------------------
# SHEET 04: ENCLOSURE STRUCTURE, SHEET THICKNESSES, BUSBARS & SANDWICH INSULATORS
# ---------------------------------------------------------------------------
def build_sheet_04_enclosure_and_busbars(out_dir: Path) -> tuple[Path, Path]:
    s = EngineeringSheet(
        "04 / 06",
        "ENCLOSURE STRUCTURE, SHEET THICKNESSES, BUSBAR CROSS-SECTIONS (3000/1800/750 mm2) & SANDWICH INSULATORS",
        "لوحة التفاصيل الإنشائية والميكانيكية: سمك الصاج والدعامات، المقاطع العرضية للبارات (3000/1800/750 مم2)، وعازل المسطرة",
    )

    # 1. Left View: Structural Cross-Section & Sheet Metal Thickness Diagram (x=150..1230, y=350..2400)
    ex, ey, ew, eh = 180.0, 350.0, 960.0, 2000.0
    s.text(ex + ew / 2, ey + eh + 210.0, "STRUCTURAL CROSS-SECTION, SHEET THICKNESSES & DOOR HARDWARE (SECTION 6)", height_mm=18.5, layer="EEHC-TITLE", bold=True, align="center")
    s.text(ex + ew / 2, ey + eh + 175.0, "مسقط تفصيلي لهيكل اللوحة، سمك الصاج، الأبواب الداخلية (المرايات)، السقف الهرمي، والقواعد السفلية", height_mm=16.5, layer="EEHC-TEXT-AR", bold=True, align="center")

    # 4 Plinths (100 mm height)
    s.rect(ex, ey - 100.0, ew, 100.0, layer="EEHC-FRAME", width=3, fill="#cbd5e1")
    for px in (ex + 25, ex + 280, ex + 600, ex + ew - 105):
        s.rect(px, ey - 100.0, 80.0, 100.0, layer="EEHC-ENCLOSURE", width=2, fill="#475569")
    s.text(ex + ew / 2, ey - 60.0, "4 PLINTHS PER CUBICLE | HEIGHT >= 10 cm (100 mm) ABOVE GROUND (CL. 6.2)", height_mm=13.5, layer="EEHC-TITLE", bold=True, align="center")

    # Outer Main Structural Frame (>= 2.5 mm cold/hot rolled steel before painting)
    s.rect(ex, ey, ew, eh, layer="EEHC-FRAME", width=5, fill="#f8fafc")
    s.rect(ex + 22, ey + 22, ew - 44, eh - 44, layer="EEHC-ENCLOSURE", width=3, fill="#ffffff")

    # Sloped Roof at angle >= 6 degrees for Outdoor Panels + Lifting Lugs (1.25x Panel Weight)
    s.line(ex - 25, ey + eh, ex + ew / 2, ey + eh + 70.0, layer="EEHC-ENCLOSURE", width=4)
    s.line(ex + ew / 2, ey + eh + 70.0, ex + ew + 25, ey + eh, layer="EEHC-ENCLOSURE", width=4)
    s.text(ex + ew / 2, ey + eh + 88.0, "SLOPED PYRAMIDAL ROOF AT ANGLE >= 6 DEG (OUTDOOR IP54) + WATERPROOF RUBBER GASKETS", height_mm=13.0, layer="EEHC-PROHIBITED", bold=True, align="center")

    # Lifting Lugs
    for lx in (ex + 70, ex + ew - 70):
        s.circle(lx, ey + eh + 32.0, 22.0, layer="EEHC-FRAME", width=3)
        s.circle(lx, ey + eh + 32.0, 10.0, layer="EEHC-ENCLOSURE", width=2, fill="#ffffff")
    s.text(ex + ew / 2, ey + eh + 122.0, "HEAVY-DUTY LIFTING LUGS TESTED FOR 1.25 x TOTAL PANEL WEIGHT (CL. 6.21)", height_mm=13.0, layer="EEHC-FRAME", bold=True, align="center")

    # Left Half of Cubicle: External Door View (Longitudinal Stiffener, 3 Hinges, 3-Point Master Key Lock, Filtered Louvers)
    mid_x = ex + ew / 2
    s.line(mid_x, ey + 22, mid_x, ey + eh - 22, layer="EEHC-FRAME", width=3)
    s.text((ex + mid_x) / 2, ey + eh - 65, "EXTERNAL DOOR VIEW", height_mm=15.0, layer="EEHC-TITLE", bold=True, align="center")
    s.text((ex + mid_x) / 2, ey + eh - 95, "(الباب الخارجي وتفاصيله)", height_mm=14.0, layer="EEHC-TEXT-AR", bold=True, align="center")

    # Longitudinal Support Stiffener (دعامة طولية) inside Outgoing Cubicle Door (Clause 6.1)
    s.rect(ex + 215, ey + 120, 46.0, eh - 260, layer="EEHC-DOOR", width=2, fill="#fef3c7", dashed=True)
    s.rect(ex + 80, ey + eh / 2 + 165, 315.0, 105.0, layer="EEHC-DOOR", width=2, fill="#fffbeb")
    s.text(ex + 238, ey + eh / 2 + 235, "LONGITUDINAL DOOR", height_mm=12.5, layer="EEHC-DOOR", bold=True, align="center")
    s.text(ex + 238, ey + eh / 2 + 205, "STIFFENER (دعامة طولية)", height_mm=12.5, layer="EEHC-DOOR", bold=True, align="center")
    s.text(ex + 238, ey + eh / 2 + 175, "TO PREVENT DEFLECTION", height_mm=11.5, bold=True, align="center")

    # 3 Heavy-Duty Galvanized / Stainless Hinges on left edge (Clause 6.2)
    for hy_off in (380.0, 1000.0, 1680.0):
        s.rect(ex + 14, ey + hy_off, 26.0, 65.0, layer="EEHC-FRAME", width=3, fill="#475569")
        s.text(ex + 46, ey + hy_off + 25, "GALV./SS HINGE", height_mm=10.5, bold=True)

    # 3-Point Rod Lock (سباليونة 3 نقط) with Nickel-Plated Master-Key Handle (Clause 6.4)
    s.rect(mid_x - 55, ey + 960, 32.0, 90.0, layer="EEHC-PROHIBITED", width=3, fill="#cbd5e1")
    s.line(mid_x - 39, ey + 1050, mid_x - 39, ey + eh - 45, layer="EEHC-PROHIBITED", width=2, dashed=True)
    s.line(mid_x - 39, ey + 960, mid_x - 39, ey + 45, layer="EEHC-PROHIBITED", width=2, dashed=True)
    s.rect(mid_x - 325, ey + 855, 255.0, 68.0, layer="EEHC-PROHIBITED", width=1, fill="#fff1f2")
    s.text(mid_x - 197, ey + 895, "NICKEL-PLATED MASTER KEY", height_mm=11.0, layer="EEHC-PROHIBITED", bold=True, align="center")
    s.text(mid_x - 197, ey + 868, "3-POINT ROD LOCK (سباليونة)", height_mm=11.0, layer="EEHC-PROHIBITED", bold=True, align="center")

    # Ventilation Louvers with Removable Fine Stainless Mesh Filter (Clause 6.5)
    for ly_off in (200.0, 1520.0):
        s.rect(ex + 80, ey + ly_off, 310.0, 130.0, layer="EEHC-ENCLOSURE", width=2, fill="#e2e8f0")
        for sl_i in range(5):
            s.line(ex + 95, ey + ly_off + 22 + sl_i * 22, ex + 375, ey + ly_off + 22 + sl_i * 22, layer="EEHC-FRAME", width=2)
        s.text(ex + 235, ey + ly_off - 24, "LOUVERS + STAINLESS MESH FILTER (CL. 6.5)", height_mm=10.5, bold=True, align="center")

    # Right Half of Cubicle: Internal Compartmentalized Hinged Doors ("Mirrors" >= 1.25 mm) (Clause 6.1)
    s.text((mid_x + ex + ew) / 2, ey + eh - 65, "INTERNAL COMPARTMENT DOORS", height_mm=15.0, layer="EEHC-DOOR", bold=True, align="center")
    s.text((mid_x + ex + ew) / 2, ey + eh - 95, "(الأبواب الداخلية / المرايات >= 1.25 مم)", height_mm=13.5, layer="EEHC-TEXT-AR", bold=True, align="center")

    comp_heights = [(1420.0, 380.0, "METERING COMPARTMENT DOOR (>=1.25 mm)"), (620.0, 770.0, "BREAKER COMPARTMENT MIRROR (>=1.25 mm)"), (120.0, 470.0, "CABLE COMPARTMENT SHIELD (>=1.25 mm)")]
    for cy_off, ch_box, c_title in comp_heights:
        s.rect(mid_x + 25, ey + cy_off, ew / 2 - 55, ch_box, layer="EEHC-DOOR", width=3, fill="#fffbeb")
        s.text(mid_x + ew / 4, ey + cy_off + ch_box - 35, c_title, height_mm=11.5, layer="EEHC-DOOR", bold=True, align="center")
        # Hinges on internal mirror door
        s.rect(mid_x + 20, ey + cy_off + 40, 14.0, 35.0, layer="EEHC-FRAME", width=2, fill="#475569")
        s.rect(mid_x + 20, ey + cy_off + ch_box - 75, 14.0, 35.0, layer="EEHC-FRAME", width=2, fill="#475569")

    # Show only breaker operating handles protruding through the middle mirror door
    for r_i in range(3):
        for c_i in range(2):
            hx = mid_x + 95 + c_i * 185
            hy = ey + 1180 - r_i * 190
            s.rect(hx, hy, 75.0, 55.0, layer="EEHC-ENCLOSURE", width=2, fill="#1e293b")
            s.text(hx + 37.5, hy + 20, "HANDLE ONLY", height_mm=9.5, bold=True, align="center", color="#ffffff")
    s.text(mid_x + ew / 4, ey + 650, "ONLY OPERATING HANDLES APPEAR THROUGH", height_mm=11.5, layer="EEHC-PROHIBITED", bold=True, align="center")
    s.text(mid_x + ew / 4, ey + 628, "FRONT COVERS (تظهر منها يد القواطع فقط)", height_mm=11.5, layer="EEHC-TEXT-AR", bold=True, align="center")

    # Callout Banner for Sheet Metal Thicknesses (Clause 6.1) below plinth
    s.rect(ex, ey - 205.0, ew, 85.0, layer="EEHC-TITLE", width=2, fill="#eff6ff")
    s.text(ex + ew / 2, ey - 152.0, "SHEET METAL THICKNESSES (CL. 6.1): FRAME >= 2.5 mm BEFORE PAINT | INDOOR BODY >= 2.0 mm (IP41)", height_mm=12.0, layer="EEHC-TITLE", bold=True, align="center")
    s.text(ex + ew / 2, ey - 185.0, "OUTDOOR BODY >= 2.5 mm BEFORE PAINT OR IK10 (IP54) | INTERNAL HINGED MIRRORS >= 1.25 mm", height_mm=12.0, layer="EEHC-PROHIBITED", bold=True, align="center")

    # 2. Middle Column: Busbar Cross-Sections (3000 / 1800 / >=750 mm2) & Sandwich Insulator Detail (x=1230..2420)
    bx, by, bw, bh = 1230.0, 350.0, 1140.0, 2000.0
    s.rect(bx, by, bw, bh, layer="EEHC-TITLE", width=3, fill="#f8fafc")
    s.rect(bx, by + bh - 55.0, bw, 55.0, layer="EEHC-TITLE", width=2, fill="#0f172a")
    s.text(bx + bw / 2, by + bh - 36.0, "BUSBAR CROSS-SECTIONS (1.5 A/mm2) & SANDWICH INSULATOR (عازل المسطرة - CLAUSE 6.8)", height_mm=15.0, bold=True, align="center", color="#ffffff")

    # Upper Detail: Sandwich Insulator ("عازل المسطرة") Assembly Holding Phase & Neutral Busbars
    s.text(bx + bw / 2, by + 1885, "A. MANDATORY SANDWICH INSULATOR (عازل المسطرة) FOR PANELS >= 500 KVA (CL. 6.8)", height_mm=15.5, layer="EEHC-INSULATOR", bold=True, align="center")
    s.text(bx + bw / 2, by + 1850, "يحزم القضبان العمومية رأسياً وأفقياً لمقاومة القوى الكهرومغناطيسية والاهتزازات أثناء القصر (65 كيلو أمبير)", height_mm=14.0, layer="EEHC-TEXT-AR", bold=True, align="center")

    # Draw 5-Block DMC/SMC Fiberglass-Reinforced Polyester Sandwich Insulator Comb
    ins_x = bx + 140.0
    ins_w = 860.0
    comb_y = by + 1140.0
    comb_h = 660.0
    # Left & Right Steel Mounting Channels + Threaded Tie Rods
    s.rect(ins_x - 35, comb_y - 20, 35.0, comb_h + 40, layer="EEHC-FRAME", width=3, fill="#94a3b8")
    s.rect(ins_x + ins_w, comb_y - 20, 35.0, comb_h + 40, layer="EEHC-FRAME", width=3, fill="#94a3b8")
    # Insulator blocks
    s.rect(ins_x, comb_y, ins_w, comb_h, layer="EEHC-INSULATOR", width=3, fill="#fef3c7")

    # Inside the Sandwich Insulator, draw each phase & neutral with exact cross-section & heat-shrink sleeve
    bars_spec = [
        ("PHASE L1 (RED HEAT-SHRINK)", "3000 mm2 (e.g. 3 x 100x10 mm OR 2 x 150x10 mm)", "EEHC-BUS-R", comb_y + 520.0, 3, 120.0, 26.0),
        ("PHASE L2 (YELLOW HEAT-SHRINK)", "3000 mm2 (Electrolytic Cu >=99.99%, >=57 MS/m)", "EEHC-BUS-Y", comb_y + 370.0, 3, 120.0, 26.0),
        ("PHASE L3 (BLUE HEAT-SHRINK)", "3000 mm2 (Current Density = 1.5 A/mm2 @ 4500A)", "EEHC-BUS-B", comb_y + 220.0, 3, 120.0, 26.0),
        ("NEUTRAL N (BLACK HEAT-SHRINK)", "1800 mm2 (e.g. 2 x 100x10 mm / 1 x 180x10 mm)", "EEHC-BUS-N", comb_y + 70.0, 2, 120.0, 26.0),
    ]
    for b_title, b_sub, b_lyr, b_y, n_subbars, sub_w, sub_h in bars_spec:
        for sb_i in range(n_subbars):
            sb_x = ins_x + 55.0 + sb_i * (sub_w + 22.0)
            # Outer color heat-shrink tube (Non-hygroscopic flame-retardant)
            s.rect(sb_x - 5, b_y - 5, sub_w + 10, sub_h + 10, layer=b_lyr, width=2, fill=s._color(b_lyr))
            # Inner pure electrolytic copper core
            s.rect(sb_x, b_y, sub_w, sub_h, layer="EEHC-INSULATOR", width=1, fill="#f97316")
        s.text(ins_x + 500.0, b_y + 16.0, b_title, height_mm=13.5, layer=b_lyr, bold=True)
        s.text(ins_x + 500.0, b_y - 8.0, b_sub, height_mm=12.0, bold=True)

    # Through-Bolts clamping the Sandwich Insulator blocks
    for ty_off in (comb_y + 30, comb_y + 175, comb_y + 325, comb_y + 475, comb_y + 625):
        s.line(ins_x - 45, ty_off, ins_x + ins_w + 45, ty_off, layer="EEHC-FRAME", width=2, dashed=True)
        s.circle(ins_x - 18, ty_off, 8.0, layer="EEHC-ENCLOSURE", fill="#334155")
        s.circle(ins_x + ins_w + 18, ty_off, 8.0, layer="EEHC-ENCLOSURE", fill="#334155")

    # Lower Detail: Protective Earth (PE) Busbar + Porcelain/Cast-Resin End Insulators + Painting Spec
    s.rect(ins_x, by + 910, ins_w, 165.0, layer="EEHC-BUS-PE", width=3, fill="#f0fdf4")
    s.rect(ins_x + 55, by + 975, 160.0, 28.0, layer="EEHC-BUS-PE", width=2, fill="#16a34a")
    s.circle(ins_x + 95, by + 989, 6.0, layer="EEHC-ENCLOSURE", fill="#ffffff")
    s.circle(ins_x + 175, by + 989, 6.0, layer="EEHC-ENCLOSURE", fill="#ffffff")
    s.text(ins_x + 250, by + 1015, "PROTECTIVE EARTH BUSBAR (PE): CSA >= 25% OF PHASE = >= 750 mm2", height_mm=13.5, layer="EEHC-BUS-PE", bold=True)
    s.text(ins_x + 250, by + 980, "• Directly bolted to panel frame along entire width + 2 Earthing Bolts at ends.", height_mm=12.0, bold=True)
    s.text(ins_x + 250, by + 948, "• Every metallic part (doors, body, gland plates) equipped with earthing bolt (Cl. 6.14).", height_mm=12.0, bold=True)

    # Electrostatic Powder Coating & Mechanical Processing Rule Box (Clauses 6.17, 6.18, 6.19)
    s.rect(bx + 45, by + 390, bw - 90, 470.0, layer="EEHC-ENCLOSURE", width=3, fill="#ffffff")
    s.rect(bx + 45, by + 810, bw - 90, 50.0, layer="EEHC-PROHIBITED", width=2, fill="#991b1b")
    s.text(bx + bw / 2, by + 826, "SURFACE TREATMENT, ELECTROSTATIC PAINTING & NAMEPLATE RULES (CL. 6.17 - 6.19)", height_mm=14.0, bold=True, align="center", color="#ffffff")
    paint_rules = [
        ("1. Chemical Pre-Treatment:", "Degreasing, derusting, complete cleaning & phosphating before painting (Cl. 6.17)."),
        ("2. Electrostatic Painting:", "Oven-baked epoxy/polyester powder coating inside & outside (dark grey) (Cl. 6.17)."),
        ("3. STRICT PROHIBITION (Cl. 6.18):", "NO drilling, cutting, or welding is permitted AFTER painting (يمنع أي عمليات ميكانيكية بعد الدهان)."),
        ("4. Panel Nameplate (Cl. 6.19):", "Fixed in prominent position on front door with non-removable rivets (مسامير برشام):"),
        ("   • Distribution Company Name", "• Manufacturer Name & Trademark   • Panel Capacity: 2500 KVA / 4500 A, 400 V, 50 Hz"),
        ("   • Serial Number & Prod. Date", "• Supply Contract No. & Date      • Total Weight (kg) & IP Rating (IP41 / IP54)"),
    ]
    for r_i, (r_head, r_body) in enumerate(paint_rules):
        ry = by + 760 - r_i * 58
        s.text(bx + 70, ry, r_head, height_mm=13.0, layer="EEHC-TITLE", bold=True)
        s.text(bx + 390, ry, r_body, height_mm=12.5, bold=True)

    # 3. Right Column: Full Guarantee Table No. 1 (Enclosure & Main Busbars - Page 17) (x=2460..4090)
    tx = 2460.0
    s.table(
        tx,
        2620.0,
        "GUARANTEE TABLE NO. (1): ENCLOSURE & MAIN BUSBARS FOR 2500 KVA PANEL (PAGE 17 + SECTION 6)",
        [("No.", 90.0), ("Characteristics (EDMS 02-300-3)", 480.0), ("Mandatory Guaranteed Value (2500 KVA)", 580.0), ("البيان العربي المطابق للمواصفة", 460.0)],
        [
            ["1", "Manufacturer's Name & Origin", "According to EDC Vendor List (To be stated)", "اسم المصنع وبلد المنشأ للمكونات"],
            ["2", "Type / Panel Designation", "LV Distribution Panel 2500 KVA (Indoor / Outdoor)", "لوحة توزيع جهد منخفض بقدرة 2500 ك.ف.أ"],
            ["3", "Applicable Standards", "IEC 61439-1,2 | IEC 60947-1,2,3 | IEC 60529 | EDMS 02-300-3", "المواصفات القياسية العالمية ومواصفة القابضة"],
            ["4", "Max Equipment Voltage (Ui)", "750 V AC", "أقصى جهد للمهمات (جهد العزل) 750 فولت"],
            ["5", "Rated Operating Voltage / Freq.", "400 V AC (3-Phase, 4-Wire) | 50 Hz", "جهد التشغيل المقنن 400 فولت | التردد 50 هرتز"],
            ["6", "Rated Impulse Withstand (Uimp)", "8 kV (1.2/50 us) | 1-sec Power Freq. Test = 2.5 kV", "جهد الصمود الدفعي 8 ك.ف | اختبار التردد الصناعي 2.5 ك.ف"],
            ["7", "Rated Panel & Busbar Current", "4500 A (for 2500 KVA Transformer)", "التيار المقنن للوحة والقضبان = 4500 أمبير"],
            ["8", "Busbar Current Density", "1.5 A / mm2 (Natural Air Cooled)", "كثافة التيار في القضبان النحاسية = 1.5 أمبير/مم2"],
            ["9", "Busbar Purity & Conductivity", "Electrolytic Copper >= 99.99% | >= 57 Siemens (MS/m)", "نحاس إلكتروليتي بنقاوة >= 99.99% وتوصيلية >= 57 سيمنز"],
            ["10", "Main Phase Busbars CSA", "3000 mm2 (Red, Yellow, Blue Heat-Shrink Insulated)", "مساحة مقطع بارات الأوجه = 3000 مم2 معزولة ريكم"],
            ["11", "Neutral & Earth Busbars CSA", "Neutral = 1800 mm2 (Black) | Earth (PE) >= 750 mm2 (>=25%)", "مقطع التعادل = 1800 مم2 | الأرضي >= 750 مم2"],
            ["12", "Busbar Insulators (Clause 6.8)", "Sandwich Insulator (عازل المسطرة) + Porcelain/Cast Resin", "عازل المسطرة الأساسي + عوازل نهاية وعوازل فواصل"],
            ["13", "Short-Circuit Withstand (1 sec)", "65 kA r.m.s. at 400 V AC", "تيار القصر المقنن لمدة ثانية واحدة = 65 كيلو أمبير"],
            ["14", "Enclosure Protection Degree", "IP41 (Indoor Panels) | IP54 (Outdoor Panels)", "درجة الحماية: IP41 للداخلي | IP54 للخارجي"],
            ["15", "Steel Frame Thickness (Cl. 6.1)", ">= 2.5 mm Cold/Hot Rolled Steel before painting", "سمك صاج الهيكل الأساسي >= 2.5 مم قبل الدهان"],
            ["16", "Enclosure Sheet Thickness", "Indoor >= 2.0 mm | Outdoor >= 2.5 mm before paint or IK10", "سمك الصاج: >= 2 مم للداخلي | >= 2.5 مم أو IK10 للخارجي"],
            ["17", "Internal Compartment Doors", ">= 1.25 mm hinged internal doors (only handles visible)", "الأبواب الداخلية المفصلية (المرايات) >= 1.25 مم"],
            ["18", "Cubicle Width & Plinths", "Outgoing width <= 1 m + door stiffener | 4 Plinths >= 10 cm", "عرض الخلية <= 1 م بدعامة | 4 قواعد بارتفاع >= 10 سم"],
            ["19", "Door Hinges & Locking System", ">= 3 Galv./SS Hinges + Nickel-Plated Master-Key 3-Point Lock", "3 مفصلات غير قابلة للصدأ + كالون ماستر بسباليونة 3 نقط"],
            ["20", "Outdoor Roof & Ventilation", "Sloped Pyramidal Roof >= 6 deg + Filtered Louvers (SS Mesh)", "سقف هرمي بميل >= 6 درجات + فتحات تهوية بفلتر صلب"],
        ],
        row_h=30.0,
    )

    dxf_p = out_dir / "04_Enclosure_Busbars_and_Structural_Details.dxf"
    png_p = out_dir / "04_Enclosure_Busbars_and_Structural_Details.png"
    s.save(dxf_p, png_p)
    return dxf_p, png_p


# ---------------------------------------------------------------------------
# SHEET 05: COMPLETE SINGLE LINE DIAGRAM (SLD) FOR 2500 KVA PANEL
# ---------------------------------------------------------------------------
def build_sheet_05_single_line_diagram(out_dir: Path) -> tuple[Path, Path]:
    s = EngineeringSheet(
        "05 / 06",
        "COMPLETE SINGLE LINE DIAGRAM (SLD) & METERING / PROTECTION ARCHITECTURE - 2500 KVA LV PANEL",
        "المخطط الخطي الأحادي الكامل (Single Line Diagram) ومنظومة القياس والوقاية للوحة توزيع جهد منخفض 2500 ك.ف.أ",
    )

    # Main SLD Canvas (x=140..2850, y=340..2620)
    # 1. Distribution Transformer 2500 KVA at Top Center (x=1150, y=2420)
    tx_c, ty_c = 1150.0, 2430.0
    s.circle(tx_c, ty_c + 45, 55.0, layer="EEHC-ACB", width=4)
    s.circle(tx_c, ty_c - 25, 55.0, layer="EEHC-ACB", width=4)
    s.text(tx_c + 85, ty_c + 45, "MV / LV DISTRIBUTION TRANSFORMER: 2500 KVA (11 or 22 kV / 0.4 kV, 50 Hz, Dyn11)", height_mm=16.5, layer="EEHC-TITLE", bold=True)
    s.text(tx_c + 85, ty_c + 12, "محول توزيع بقدرة 2500 ك.ف.أ - التيار المقنن للوحة القابضة = 4500 أمبير (EDMS 02-300-3)", height_mm=15.0, layer="EEHC-TEXT-AR", bold=True)

    # Incoming Busbar Tree / Busway / Bottom Cables (Clause 6.25)
    s.line(tx_c, ty_c - 80, tx_c, 2060.0, layer="EEHC-BUS-R", width=5)
    s.rect(tx_c - 170, 2240.0, 340.0, 65.0, layer="EEHC-INSULATOR", width=2, fill="#fef3c7")
    s.text(tx_c, 2278.0, "PROTRUDED BUSBAR TREE (شجرة بارتفاع 20 سم) THROUGH PAXOLIN PLATE", height_mm=11.5, layer="EEHC-INSULATOR", bold=True, align="center")
    s.text(tx_c, 2252.0, "OR BUSWAY FLANGE (بدون نزلات) OR BOTTOM CABLES (HOLES >=8cm + GASKETS)", height_mm=11.0, bold=True, align="center")

    # 2. Metering & Indication Branch on Incoming Supply (Items 8, 9, 10, 11 & Clause 6.20)
    # CT Set 1 & CT Set 2 (2 Sets x 3 Ring CTs, 5000/5 A, Class 0.5, 10 VA, Ith=10 kA)
    for ct_i, ct_y in enumerate((2175.0, 2125.0)):
        s.circle(tx_c, ct_y, 20.0, layer="EEHC-METER", width=3)
        s.circle(tx_c, ct_y, 13.0, layer="EEHC-METER", width=2)
        s.line(tx_c + 20, ct_y, tx_c + 290, ct_y, layer="EEHC-METER", width=2)

    s.rect(tx_c + 290, 2065.0, 620.0, 160.0, layer="EEHC-METER", width=3, fill="#ecfeff")
    s.text(tx_c + 600, 2195.0, "METERING & ENERGY PACKAGE (بنود 8، 9، 10، 11 وبند 6-20)", height_mm=13.5, layer="EEHC-METER", bold=True, align="center")
    s.text(tx_c + 600, 2165.0, "• 2 Sets x 3 Ring CTs: 5000 / 5 A, Class 0.5, 10 VA, Ith = 10 kA", height_mm=12.5, bold=True, align="center")
    s.text(tx_c + 600, 2135.0, "• 3x Digital Ammeters (0 - 5000 A) + 1x Digital Voltmeter (0 - 500 V) [or Multimeter]", height_mm=12.0, bold=True, align="center")
    s.text(tx_c + 600, 2105.0, "• 3x Phase Indication LED Lamps with Protection Fuses (L1, L2, L3)", height_mm=12.0, bold=True, align="center")
    s.text(tx_c + 600, 2078.0, "• Dedicated Space + Wired Terminal Blocks for 3-Ph KWH Energy Meter + Spare T.B.", height_mm=11.5, bold=True, align="center")

    # 3. Main Circuit Breaker Q0: 3P ACB 5000A, 65kA (Item 4 & Clauses 4, 5.1, 5.2, 6.6, 6.7)
    s.rect(tx_c - 180, 1760.0, 360.0, 270.0, layer="EEHC-ACB", width=4, fill="#eff6ff")
    # Breaker symbol inside Q0 box
    s.line(tx_c, 2030.0, tx_c, 1965.0, layer="EEHC-ACB", width=4)
    s.line(tx_c, 1965.0, tx_c - 32.0, 1895.0, layer="EEHC-PROHIBITED", width=4)
    s.line(tx_c - 10, 1955.0, tx_c + 10, 1975.0, layer="EEHC-PROHIBITED", width=3)
    s.line(tx_c - 10, 1975.0, tx_c + 10, 1955.0, layer="EEHC-PROHIBITED", width=3)
    s.line(tx_c, 1895.0, tx_c, 1760.0, layer="EEHC-ACB", width=4)

    s.rect(tx_c - 168, 1990.0, 336.0, 30.0, layer="EEHC-ACB", width=1, fill="#dbeafe")
    s.text(tx_c, 1998.0, "MAIN INCOMING ACB (Q0 - SEPARATE CUBICLE)", height_mm=13.0, layer="EEHC-ACB", bold=True, align="center")
    s.rect(tx_c - 168, 1768.0, 336.0, 118.0, layer="EEHC-ACB", width=1, fill="#ffffff")
    s.text(tx_c, 1860.0, "3P ACB | Frame = 5000 A | In = 5000 A (40 C)", height_mm=12.5, bold=True, align="center")
    s.text(tx_c, 1832.0, "Derated: 5500A (25C) | 4700A (50C) | 4125A (60C)", height_mm=11.5, layer="EEHC-PROHIBITED", bold=True, align="center")
    s.text(tx_c, 1804.0, "Icu = Ics = 65 kA (400V) | Ui = 750V | Uimp = 8kV", height_mm=11.5, bold=True, align="center")
    s.text(tx_c, 1776.0, "Ir = (0.4 - 1.0) In | Im = (2 - 10) In | NO UV COIL", height_mm=11.5, layer="EEHC-PROHIBITED", bold=True, align="center")

    # Left Accessory Box attached to Main ACB (Clause 6.6 & 6.7)
    s.rect(tx_c - 790, 1775.0, 560.0, 240.0, layer="EEHC-PROHIBITED", width=3, fill="#fef2f2")
    s.line(tx_c - 230, 1895.0, tx_c - 180, 1895.0, layer="EEHC-PROHIBITED", width=2, dashed=True)
    s.text(tx_c - 510, 1980.0, "MANDATORY ACB RULES & PROTECTION OPTIONS:", height_mm=13.5, layer="EEHC-PROHIBITED", bold=True, align="center")
    s.text(tx_c - 510, 1945.0, "1. WITHOUT Under-Voltage Release Coil (بدون ملف هبوط الجهد)", height_mm=12.5, bold=True, align="center")
    s.text(tx_c - 510, 1915.0, "   to prevent nuisance tripping during voltage dips (Cl. 5.1).", height_mm=11.5, align="center")
    s.text(tx_c - 510, 1880.0, "2. Installed in SEPARATE CUBICLE; STRICTLY FORBIDDEN to", height_mm=12.5, layer="EEHC-PROHIBITED", bold=True, align="center")
    s.text(tx_c - 510, 1850.0, "   place any MCCBs or Switch-Fuses below it (Cl. 5.2.A).", height_mm=12.0, bold=True, align="center")
    s.text(tx_c - 510, 1815.0, "3. Equipped with Shunt Trip + 2 Flag Relays (Main CB & MV LBS)", height_mm=12.0, bold=True, align="center")
    s.text(tx_c - 510, 1790.0, "   when requested by Distribution Company (Cl. 6.7).", height_mm=11.5, align="center")

    # 4. Main Horizontal Busbar System (4500 A, 400 V, 50 Hz, 65 kA / 1 sec) (y=1580..1680)
    s.line(tx_c, 1760.0, tx_c, 1680.0, layer="EEHC-ACB", width=5)
    bus_x1, bus_x2 = 160.0, 2720.0
    bus_bars = [
        (1680.0, "EEHC-BUS-R", "L1 PHASE BUSBAR: 3000 mm2 Cu (>=99.99%, >=57 MS/m, 1.5 A/mm2) - RED HEAT-SHRINK"),
        (1650.0, "EEHC-BUS-Y", "L2 PHASE BUSBAR: 3000 mm2 Cu - YELLOW HEAT-SHRINK"),
        (1620.0, "EEHC-BUS-B", "L3 PHASE BUSBAR: 3000 mm2 Cu - BLUE HEAT-SHRINK"),
        (1585.0, "EEHC-BUS-N", "NEUTRAL (N) BUSBAR: 1800 mm2 Cu - BLACK HEAT-SHRINK (Pre-drilled with Galv. Bolts)"),
        (1550.0, "EEHC-BUS-PE", "PROTECTIVE EARTH (PE) BUSBAR: >= 750 mm2 Cu (>= 25% of Phase CSA) + Earthing Bolts"),
    ]
    for by_line, b_lyr, b_desc in bus_bars:
        s.line(bus_x1, by_line, bus_x2, by_line, layer=b_lyr, width=4 if "PE" not in b_lyr else 3, dashed=("PE" in b_lyr))
        s.text(bus_x1 + 15, by_line + 5, b_desc, height_mm=11.0, layer=b_lyr, bold=True)

    # Sandwich Insulator symbols along the Main Busbar
    for si_x in (480.0, 1020.0, 1560.0, 2100.0, 2580.0):
        s.rect(si_x - 10, 1575.0, 20.0, 120.0, layer="EEHC-INSULATOR", width=2, fill="#fde68a")
    s.text(1440.0, 1710.0, "MAIN BUSBAR SYSTEM: 4500 A, 400 V, 50 Hz, Icw = 65 kA / 1s — SUPPORTED BY MANDATORY SANDWICH INSULATORS (عازل المسطرة - CL. 6.8)", height_mm=14.0, layer="EEHC-TITLE", bold=True, align="center")

    # 5. Five Outgoing & Auxiliary Branches Below Main Busbars (y=420..1580)
    # Branch 1 (x=170..830): Option (A) 16 x 400A MCCB Outgoing Feeders
    b1_x = 500.0
    s.line(b1_x, 1680.0, b1_x, 1340.0, layer="EEHC-MCCB", width=4)
    s.rect(170.0, 560.0, 660.0, 780.0, layer="EEHC-MCCB", width=3, fill="#eff6ff")
    s.rect(170.0, 1280.0, 660.0, 60.0, layer="EEHC-MCCB", width=2, fill="#1d4ed8")
    s.text(b1_x, 1312.0, "OUTGOING OPTION (A): 16 x 400A MCCBs", height_mm=14.5, bold=True, align="center", color="#ffffff")
    s.text(b1_x, 1288.0, "(الخيار الأول للخروجات: عدد 16 قاطع فرعي مقولب 400 أمبير)", height_mm=13.0, layer="EEHC-TEXT-AR", bold=True, align="center", color="#ffffff")
    b1_lines = [
        "• Quantity & Type: 16 x 3-Pole MCCB (EDMS 19-303)",
        "• Frame Current: 400 A at 40 C ambient",
        "• Nominal Current (In): 400 A at 40 C",
        "• Derated (Table-3): 440A (25C) | 372A (50C) | 340A (60C)",
        "• Breaking Capacity: Icu = Ics = 65 kA at 400 V AC",
        "• Thermal Overload Release: Adjustable (0.8 - 1.0) In",
        "• Short-Circuit Release: Fixed/Adj. (5 - 10) In",
        "• Insulation / Impulse: Ui = 750 V | Uimp = 8 kV",
        "• Clearances (Cl. 6.9): >= 6 cm between MCCBs & body",
        "• Output Extension Bars (Cl. 6.10): Insulated Cu bars",
        "  mounted on insulators at MCCB exit terminals",
        "• Cable Terminal Height (Cl. 6.15): >= 55 cm from bottom",
        "• Cubicle Width (Cl. 6.1): <= 1.0 m with door stiffener",
    ]
    for l_i, l_str in enumerate(b1_lines):
        s.text(195.0, 1235.0 - l_i * 46.0, l_str, height_mm=12.5, bold=True)

    # Branch 2 (x=870..1550): Option (B) 20 x 400/630A Size-3 Vertical Switch-Fuses
    b2_x = 1210.0
    s.line(b2_x, 1680.0, b2_x, 1340.0, layer="EEHC-SWFUSE", width=4)
    s.rect(870.0, 560.0, 680.0, 780.0, layer="EEHC-SWFUSE", width=3, fill="#f5f3ff")
    s.rect(870.0, 1280.0, 680.0, 60.0, layer="EEHC-SWFUSE", width=2, fill="#6d28d9")
    s.text(b2_x, 1312.0, "OUTGOING OPTION (B): 20 x 400/630A SWITCH-FUSES", height_mm=14.0, bold=True, align="center", color="#ffffff")
    s.text(b2_x, 1288.0, "(الخيار الثاني: عدد 20 سويتش فيوز رأسي 630 أمبير مقاس 3)", height_mm=13.0, layer="EEHC-TEXT-AR", bold=True, align="center", color="#ffffff")
    b2_lines = [
        "• Quantity & Size: 20 x Vertical Switch-Fuse 400/630A, Size 3",
        "• Standards: IEC 60947-1,3 & EDMS 19-302 / 19-300",
        "• Rated Voltage & Short-Circuit: 500 V, 50 Hz, 50 kA",
        "• Busbar Pole Center Distance: 210 mm",
        "• Operating Cycles: >= 200 (loaded) | >= 800 (unloaded)",
        "• Category: AC-22B | Overvoltage III | Pollution Degree 3",
        "• Arcing Chamber: Included | Insulation Flammability: V0",
        "• Degree of Protection: IP20 | Mechanical Shock: IK07",
        "• Contacts: Copper >= 57 MS/m | Silver Coating = 5 um",
        "• MANDATORY Copper Droppers (Cl. 6.11): CSA = 250 mm2",
        "  (heat-shrink insulated) to prevent switch-fuse damage",
        "• Terminal Bolts (Cl. 6.12): Galv. 12 mm (0.5\") + 2 flat",
        "  washers + 1 spring washer + 1 nut | Height >= 55 cm",
        "• Clearance >= 2 cm (Cl. 6.9) | Cable Holders Dia >= 8 cm",
    ]
    for l_i, l_str in enumerate(b2_lines):
        s.text(895.0, 1235.0 - l_i * 45.0, l_str, height_mm=12.0, bold=True)

    # Branch 3 (x=1590..2150): Street Lighting Feeder (63A MCCB + 63A Contactor + 10A MCB + Timer/Photocell)
    b3_x = 1870.0
    s.line(b3_x, 1680.0, b3_x, 1340.0, layer="EEHC-LIGHTING", width=4)
    s.rect(1590.0, 560.0, 560.0, 780.0, layer="EEHC-LIGHTING", width=3, fill="#fff7ed")
    s.rect(1590.0, 1280.0, 560.0, 60.0, layer="EEHC-LIGHTING", width=2, fill="#c2410c")
    s.text(b3_x, 1312.0, "STREET LIGHTING SECTION (OPTION - ITEM 6)", height_mm=14.0, bold=True, align="center", color="#ffffff")
    s.text(b3_x, 1288.0, "(خلية الإنارة العامة والخلية الضوئية - صفحة 13)", height_mm=13.0, layer="EEHC-TEXT-AR", bold=True, align="center", color="#ffffff")
    b3_lines = [
        "• 1x 3P MCCB: Frame = 63 A, In = 63 A at 40 C",
        "  Breaking Capacity Icu = Ics = 65 kA at 400 V",
        "  Thermal Overload Release: (0.8 - 1.0) In",
        "• 1x 3-Phase Contactor: In = 63 A at 40 C",
        "  Coil Operating Voltage = 220 V AC, 50 Hz",
        "• 1x 1P MCB: 10 A (Lighting Control Protection)",
        "• 1x Digital Timer OR Outdoor Photoelectric Cell:",
        "  - Cadmium Sulfide (CdS) Sensing Element",
        "  - Rated 220 V, 50 c/s, 1000 W",
        "  - Normally Closed (NC) Contact Arrangement",
        "  - High-Impact UV-Stabilized Acrylic Housing",
        "  - Aluminum Plug-in Twist-Lock Base/Container",
        "  - Top Steel Bracket permitting 360 deg Rotation",
    ]
    for l_i, l_str in enumerate(b3_lines):
        s.text(1615.0, 1235.0 - l_i * 46.0, l_str, height_mm=12.0, bold=True)

    # Branch 4 (x=2190..2780): Fixed Capacitor Bank (125 KVAR + 320A / 65kA MCCB) & Panel Auxiliaries
    b4_x = 2485.0
    s.line(b4_x, 1680.0, b4_x, 1340.0, layer="EEHC-CAPACITOR", width=4)
    s.rect(2190.0, 560.0, 590.0, 780.0, layer="EEHC-CAPACITOR", width=3, fill="#ecfdf5")
    s.rect(2190.0, 1280.0, 590.0, 60.0, layer="EEHC-CAPACITOR", width=2, fill="#15803d")
    s.text(b4_x, 1312.0, "FIXED CAPACITOR (125 KVAR) & AUXILIARIES", height_mm=14.0, bold=True, align="center", color="#ffffff")
    s.text(b4_x, 1288.0, "(خلية المكثف الثابت 125 ك.فار وخدمات اللوحة)", height_mm=13.0, layer="EEHC-TEXT-AR", bold=True, align="center", color="#ffffff")
    b4_lines = [
        "• Protection MCCB: In = 320 A at 40 C, 65 kA at 400 V",
        "• Capacitor Power: 125 KVAR at 400 V Operating Voltage",
        "• Design Voltage (Un): 525 V AC, 50 Hz (Delta Δ)",
        "• Type: Dry Self-Healing | Tolerance: -5% to +10%",
        "• Discharge Resistors: <= 50 V within 60 seconds",
        "• Losses: Dielectric < 0.2 W/KVAR | Total < 0.5 W/KVAR",
        "• Overcurrent / Inrush: >= 1.3x In | >= 200x In",
        "• Temp / Humidity: -5 C to +55 C | RH up to 95%",
        "• Lifetime: >= 130,000 hrs (Class D) + 3-Ph Overpressure",
        "• Dielectric Test: 2.5 kV (1 min) | Impulse: 8 kV",
        "----------------------------------------------------",
        "• Panel Services (Cl. 6.23 & 6.24): 220V Socket + MCB",
        "  & Internal LED Lamp (>= 9W) with separate MCB",
    ]
    for l_i, l_str in enumerate(b4_lines):
        s.text(2215.0, 1235.0 - l_i * 46.0, l_str, height_mm=12.0, bold=True)

    # 6. Right Column: Guarantee Table No. 2 (Main Circuit Breaker) & Table-3 Derating (x=2820..4090)
    tx = 2820.0
    s.table(
        tx,
        2620.0,
        "GUARANTEE TABLE NO. (2): MAIN CIRCUIT BREAKER (PAGE 18)",
        [("No.", 80.0), ("Characteristics", 460.0), ("Guaranteed Value (2500 KVA)", 690.0)],
        [
            ["1", "Manufacturer's Name & Origin", "According to EDC Vendor List (To be stated)"],
            ["2", "Type / Model Designation", "3-Pole Air Circuit Breaker (ACB - Open Type)"],
            ["3", "Number of Poles", "3 Poles"],
            ["4", "Applicable Standards", "IEC 60947-1, IEC 60947-2, EDMS 02-300-3"],
            ["5", "Frame Current at 40 C (A)", "5000 A"],
            ["6", "Nominal Current In at 40 C (A)", "5000 A (Derated at 50 C = 4700 A per Table-3)"],
            ["7", "Rated Operating Voltage (V)", "400 V AC, 50 Hz"],
            ["8", "Max Equipment Voltage Ui (V)", "750 V AC"],
            ["9", "Rated Impulse Voltage Uimp (kV)", "8 kV"],
            ["10", "Breaking Capacity Icu=Ics (kA)", "65 kA at 400 V AC (Icu = Ics = 65 kA)"],
            ["11", "Overload Thermal Release Ir (A)", "Adjustable (0.4 - 1.0) In at 40 C"],
            ["12", "Short-Circuit Release Im (kA)", "Adjustable (2 - 10) In"],
            ["13", "Under-Voltage Release Coil", "WITHOUT Under-Voltage Release Coil"],
            ["14", "Optional Protection Accessories", "Shunt Trip + 2 Flag Relays (Main CB & LBS)"],
        ],
        row_h=30.0,
    )

    s.table(
        tx,
        1360.0,
        "TABLE (3) PAGE 16: AMBIENT TEMPERATURE DERATING FOR 2500 KVA PANEL",
        [("Breaker Position", 360.0), ("25 C", 210.0), ("40 C (Ref)", 230.0), ("50 C", 210.0), ("60 C", 220.0)],
        [
            ["Main ACB (5000 A)", "5500 A", "5000 A", "4700 A", "4125 A"],
            ["Outgoing MCCB (400 A)", "440 A", "400 A", "372 A", "340 A"],
            ["Capacitor MCCB (320 A)", "352 A", "320 A", "298 A", "272 A"],
            ["Street Light MCCB (63 A)", "69 A", "63 A", "58.6 A", "53.5 A"],
        ],
        row_h=32.0,
    )

    # Bottom Summary Banner across Left/Center (y=360..510)
    s.rect(170.0, 360.0, 2610.0, 150.0, layer="EEHC-TITLE", width=3, fill="#f1f5f9")
    s.text(1475.0, 470.0, "GENERAL MANDATORY WIRING & TESTING NOTES (CLAUSES 6.20, 6.22, 6.26 & SECTION 7):", height_mm=14.5, layer="EEHC-TITLE", bold=True, align="center")
    s.text(1475.0, 430.0, "• All internal auxiliary & metering wiring: Non-flammable PVC Copper >= 2 mm2, insulated for 450/750 V, terminated with numbered lugs inside plastic ducts.", height_mm=12.5, bold=True, align="center")
    s.text(1475.0, 392.0, "• Routine & Acceptance Tests (IEC 61439-1 & IEC 60947): 1. Visual/Dimensional & Thickness Inspection  2. Mechanical Operation  3. Insulation Resistance  4. Dielectric Test (2.5 kV / 1s).", height_mm=12.5, layer="EEHC-PROHIBITED", bold=True, align="center")

    dxf_p = out_dir / "05_Single_Line_Diagram_2500kVA_Complete.dxf"
    png_p = out_dir / "05_Single_Line_Diagram_2500kVA_Complete.png"
    s.save(dxf_p, png_p)
    return dxf_p, png_p


# ---------------------------------------------------------------------------
# SHEET 06: MASTER COMBINED DRAWING (ALL CUBICLES + SLD + DETAILS + 5 GUARANTEE TABLES)
# ---------------------------------------------------------------------------
def build_sheet_06_master_composite(out_dir: Path) -> tuple[Path, Path]:
    s = EngineeringSheet(
        "06 / 06 (MASTER)",
        "MASTER GENERAL ARRANGEMENT, SLD, SUBSYSTEM DETAILS & COMPLETE GUARANTEE TABLES - 2500 KVA LV PANEL (EDMS 02-300-3)",
        "المخطط العام المجمع الشامل (Master Drawing): الواجهة الأمامية والقطاع الداخلي لكامل الخلايا، المخطط الأحادي، التفاصيل، وجداول الضمان الخمسة",
        width_mm=5800.0,
        height_mm=4000.0,
    )

    # =========================================================================
    # ZONE 1 (TOP LEFT, x=140..3350, y=2250..3660): FULL MULTI-CUBICLE LINEUP
    # Shows the complete 2500 KVA Panel Lineup (4 Cubicles Side-by-Side):
    # Cubicle 1: Main Incoming ACB 5000A (Separate Cubicle - No MCCBs/Switch-fuses below!)
    # Cubicle 2: Outgoing Section #1 (8x400A MCCBs OR 10x630A Vertical Switch-Fuses)
    # Cubicle 3: Outgoing Section #2 (8x400A MCCBs OR 10x630A Vertical Switch-Fuses)
    # Cubicle 4: Fixed Capacitor (125 KVAR + 320A MCCB) & Street Lighting (63A + Photocell)
    # =========================================================================
    zx, zy = 160.0, 2300.0
    s.text(1750.0, 3630.0, "1. COMPLETE 2500 KVA / 4500 A PANEL GENERAL ARRANGEMENT (4-CUBICLE LINEUP: INTERNAL SECTION & FRONT ELEVATION)", height_mm=21.0, layer="EEHC-TITLE", bold=True, align="center")
    s.text(1750.0, 3588.0, "الترتيب العام الكامل للوحة جهد منخفض 2500 ك.ف.أ / 4500 أمبير (خلية القاطع الهوائي المنفصلة + خليتي الخروج + خلية المكثف 125 ك.فار والإنارة العامة)", height_mm=18.5, layer="EEHC-TEXT-AR", bold=True, align="center")

    cubicles = [
        ("CUBICLE 1: INCOMING ACB", "خلية القاطع الرئيسي 5000A", 760.0),
        ("CUBICLE 2: OUTGOING #1", "خلية الخروج الأولى (<=1m)", 760.0),
        ("CUBICLE 3: OUTGOING #2", "خلية الخروج الثانية (<=1m)", 760.0),
        ("CUBICLE 4: 125kVAR + LIGHT", "خلية المكثف والإنارة", 760.0),
    ]
    cur_x = zx
    cub_h = 1120.0
    for c_idx, (c_en, c_ar, c_w) in enumerate(cubicles):
        # 4 Plinths (100 mm)
        s.rect(cur_x, zy - 65.0, c_w, 65.0, layer="EEHC-FRAME", width=3, fill="#cbd5e1")
        for p_off in (20, 220, 480, c_w - 75):
            s.rect(cur_x + p_off, zy - 65.0, 55.0, 65.0, layer="EEHC-ENCLOSURE", width=2, fill="#475569")
        # Main Cubicle Body (>=2.5mm frame, >=2.0mm indoor / >=2.5mm outdoor)
        s.rect(cur_x, zy, c_w, cub_h, layer="EEHC-ENCLOSURE", width=4, fill="#ffffff")
        # Sloped Roof >= 6 deg
        s.line(cur_x, zy + cub_h, cur_x + c_w / 2, zy + cub_h + 42.0, layer="EEHC-ENCLOSURE", width=3)
        s.line(cur_x + c_w / 2, zy + cub_h + 42.0, cur_x + c_w, zy + cub_h, layer="EEHC-ENCLOSURE", width=3)
        # Lifting lugs
        s.circle(cur_x + 40, zy + cub_h + 18.0, 12.0, layer="EEHC-FRAME", width=2)
        s.circle(cur_x + c_w - 40, zy + cub_h + 18.0, 12.0, layer="EEHC-FRAME", width=2)
        # Header label
        s.text(cur_x + c_w / 2, zy + cub_h + 82.0, c_en, height_mm=14.0, layer="EEHC-TITLE", bold=True, align="center")
        s.text(cur_x + c_w / 2, zy + cub_h + 54.0, c_ar, height_mm=13.5, layer="EEHC-TEXT-AR", bold=True, align="center")

        # Main Horizontal Busbars running continuously across all 4 cubicles (3000 mm2 L1,L2,L3)
        for b_i, b_lyr in enumerate(("EEHC-BUS-R", "EEHC-BUS-Y", "EEHC-BUS-B")):
            s.rect(cur_x, zy + cub_h - 90.0 - b_i * 28.0, c_w, 16.0, layer=b_lyr, width=2, fill=s._color(b_lyr))
        # Sandwich Insulator in every cubicle
        s.rect(cur_x + c_w - 28, zy + cub_h - 160.0, 18.0, 95.0, layer="EEHC-INSULATOR", width=2, fill="#fde68a")

        # Continuous Neutral (1800 mm2) & Earth (>=750 mm2) Busbars near bottom
        s.rect(cur_x + 15, zy + 125.0, c_w - 30, 16.0, layer="EEHC-BUS-N", width=2, fill=s._color("EEHC-BUS-N"))
        s.rect(cur_x + 15, zy + 75.0, c_w - 30, 12.0, layer="EEHC-BUS-PE", width=2, fill=s._color("EEHC-BUS-PE"))

        if c_idx == 0:
            # Cubicle 1: Protruded Busbar Tree (20 cm) at Top + Metering + Main ACB 5000A + Prohibited Zone Below
            s.rect(cur_x + 210, zy + cub_h - 6.0, 340.0, 12.0, layer="EEHC-INSULATOR", width=2, fill="#d97706")
            for t_i, t_lyr in enumerate(("EEHC-BUS-R", "EEHC-BUS-Y", "EEHC-BUS-B", "EEHC-BUS-N")):
                bx_tree = cur_x + 250 + t_i * 80
                s.rect(bx_tree - 8, zy + cub_h + 6.0, 16.0, 115.0, layer=t_lyr, width=2, fill=s._color(t_lyr))
            s.text(cur_x + c_w / 2, zy + cub_h + 132.0, "20cm B.B. TREE (شجرة)", height_mm=12.5, layer="EEHC-PROHIBITED", bold=True, align="center")

            # Metering strip (3 Ammeters, 1 Voltmeter, 3 LEDs, KWH space)
            s.rect(cur_x + 40, zy + 820.0, c_w - 80, 110.0, layer="EEHC-METER", width=2, fill="#ecfeff")
            s.text(cur_x + c_w / 2, zy + 885.0, "3x AMMETER (0-5000A) + VOLTMETER (0-500V) + 3 LEDs + KWH SPACE", height_mm=11.0, layer="EEHC-METER", bold=True, align="center")
            s.text(cur_x + c_w / 2, zy + 845.0, "2 SETS x 3 RING CTs (5000/5A, Cl 0.5, 10VA, Ith=10kA) + 220V SOCKET + >=9W LED", height_mm=10.5, bold=True, align="center")

            # Main ACB 5000A
            s.rect(cur_x + 130, zy + 450.0, c_w - 260, 330.0, layer="EEHC-ACB", width=4, fill="#dbeafe")
            s.text(cur_x + c_w / 2, zy + 725.0, "MAIN 3P ACB (Q0)", height_mm=15.5, layer="EEHC-ACB", bold=True, align="center")
            s.text(cur_x + c_w / 2, zy + 680.0, "Frame = 5000 A | In = 5000 A (40 C)", height_mm=13.0, bold=True, align="center")
            s.text(cur_x + c_w / 2, zy + 640.0, "4700A (50C) | Icu = Ics = 65 kA", height_mm=13.0, layer="EEHC-PROHIBITED", bold=True, align="center")
            s.text(cur_x + c_w / 2, zy + 600.0, "Ir=(0.4-1)In | Im=(2-10)In", height_mm=12.5, bold=True, align="center")
            s.text(cur_x + c_w / 2, zy + 560.0, "WITHOUT Under-Voltage Release Coil", height_mm=12.0, layer="EEHC-PROHIBITED", bold=True, align="center")
            s.text(cur_x + c_w / 2, zy + 520.0, "With Shunt Trip + 2 Flag Relays", height_mm=12.0, bold=True, align="center")

            # Prohibited Zone Below Main ACB (Clause 5.2.A)
            s.rect(cur_x + 40, zy + 170.0, c_w - 80, 250.0, layer="EEHC-PROHIBITED", width=3, fill="#fef2f2", dashed=True)
            s.line(cur_x + 40, zy + 170.0, cur_x + c_w - 40, zy + 420.0, layer="EEHC-PROHIBITED", width=2, dashed=True)
            s.line(cur_x + 40, zy + 420.0, cur_x + c_w - 40, zy + 170.0, layer="EEHC-PROHIBITED", width=2, dashed=True)
            s.text(cur_x + c_w / 2, zy + 310.0, "PROHIBITED ZONE BELOW MAIN ACB (CL. 5.2.A)", height_mm=13.0, layer="EEHC-PROHIBITED", bold=True, align="center")
            s.text(cur_x + c_w / 2, zy + 270.0, "NO MCCBs OR SWITCH-FUSES ALLOWED", height_mm=12.5, layer="EEHC-PROHIBITED", bold=True, align="center")

        elif c_idx == 1:
            # Cubicle 2: Show Option A (8 x 400A MCCBs per cubicle, 2 cubicles = 16 x 400A MCCBs)
            s.text(cur_x + c_w / 2, zy + 915.0, "OPTION (A): 16 x 400A MCCB (8 PER CUBICLE)", height_mm=13.0, layer="EEHC-MCCB", bold=True, align="center")
            for r_i in range(4):
                for col_i in range(2):
                    mx = cur_x + 85.0 + col_i * 330.0
                    my = zy + 760.0 - r_i * 125.0
                    s.rect(mx, my, 250.0, 95.0, layer="EEHC-MCCB", width=2, fill="#eff6ff")
                    s.text(mx + 125.0, my + 55.0, f"MCCB #{r_i*2+col_i+1:02d}: 400A / 65kA", height_mm=11.0, layer="EEHC-MCCB", bold=True, align="center")
                    s.text(mx + 125.0, my + 22.0, "Ir=(0.8-1)In | Im=(5-10)In", height_mm=10.0, bold=True, align="center")
            s.text(cur_x + c_w / 2, zy + 315.0, "Clearance >= 6 cm | Additional Cu Output Bars", height_mm=11.5, layer="EEHC-PROHIBITED", bold=True, align="center")
            s.text(cur_x + c_w / 2, zy + 280.0, "Cable Terminals Height >= 55 cm from Bottom", height_mm=11.5, bold=True, align="center")

        elif c_idx == 2:
            # Cubicle 3: Show Option B (10 x 630A Size-3 Vertical Switch-Fuses per section, x2 = 20 ways)
            s.text(cur_x + c_w / 2, zy + 915.0, "OPTION (B): 20 x 630A SIZE-3 SWITCH-FUSES", height_mm=13.0, layer="EEHC-SWFUSE", bold=True, align="center")
            for sf_i in range(10):
                sfx = cur_x + 45.0 + sf_i * 68.0
                s.rect(sfx, zy + 540.0, 52.0, 350.0, layer="EEHC-SWFUSE", width=2, fill="#f5f3ff")
                s.text(sfx + 26.0, zy + 710.0, f"S{sf_i+1}", height_mm=9.5, layer="EEHC-SWFUSE", bold=True, align="center")
                # 250 mm2 Copper Dropper down to >=55cm terminal zone
                s.rect(sfx + 20.0, zy + 310.0, 12.0, 230.0, layer="EEHC-BUS-R", width=1, fill="#f97316")
                s.circle(sfx + 26.0, zy + 320.0, 5.0, layer="EEHC-ENCLOSURE", fill="#fde047")
                # Cable holder Dia >= 8 cm
                s.circle(sfx + 26.0, zy + 215.0, 18.0, layer="EEHC-FRAME", width=2)
            s.text(cur_x + c_w / 2, zy + 265.0, "250 mm2 COPPER DROPPERS + 12mm (0.5\") GALV. BOLTS", height_mm=11.5, layer="EEHC-PROHIBITED", bold=True, align="center")
            s.text(cur_x + c_w / 2, zy + 170.0, "Pole Center = 210mm | Clearance >=2cm | Holders >=8cm", height_mm=11.0, bold=True, align="center")

        elif c_idx == 3:
            # Cubicle 4: Street Lighting (Upper) + 125 KVAR Fixed Capacitor (Lower) + Top Photocell
            # Top Outdoor Photocell on aluminum twist-lock base + bracket
            pcx = cur_x + c_w - 130.0
            pcy = zy + cub_h + 25.0
            s.rect(pcx - 25, pcy, 50.0, 28.0, layer="EEHC-FRAME", width=2, fill="#64748b")
            s.rect(pcx - 22, pcy + 28.0, 44.0, 52.0, layer="EEHC-LIGHTING", width=3, fill="#ffedd5")
            s.text(pcx, pcy + 48.0, "CdS", height_mm=10.5, layer="EEHC-LIGHTING", bold=True, align="center")
            s.text(pcx, pcy + 92.0, "PHOTOCELL (360°)", height_mm=11.0, layer="EEHC-LIGHTING", bold=True, align="center")

            # Street Lighting Compartment
            s.rect(cur_x + 35, zy + 650.0, c_w - 70, 260.0, layer="EEHC-LIGHTING", width=3, fill="#fff7ed")
            s.text(cur_x + c_w / 2, zy + 875.0, "STREET LIGHTING COMPARTMENT (بند 6 ص 13)", height_mm=12.5, layer="EEHC-LIGHTING", bold=True, align="center")
            s.text(cur_x + c_w / 2, zy + 835.0, "• 1x 3P MCCB 63A (Frame 63A, 65kA, Ir=0.8-1 In)", height_mm=11.5, bold=True, align="center")
            s.text(cur_x + c_w / 2, zy + 800.0, "• 1x 3Ph Contactor 63A (40C), Coil 220V, 50Hz", height_mm=11.5, bold=True, align="center")
            s.text(cur_x + c_w / 2, zy + 765.0, "• 1x 1P MCB 10A + Digital Timer / CdS Photocell", height_mm=11.5, bold=True, align="center")
            s.text(cur_x + c_w / 2, zy + 730.0, "• Photocell: 220V, 1000W, NC, Acrylic UV, Alu Base", height_mm=11.0, bold=True, align="center")

            # Fixed Capacitor Compartment (125 KVAR + 320A MCCB)
            s.rect(cur_x + 35, zy + 170.0, c_w - 70, 455.0, layer="EEHC-CAPACITOR", width=3, fill="#ecfdf5")
            s.text(cur_x + c_w / 2, zy + 590.0, "FIXED CAPACITOR BANK: 125 KVAR @ 400V", height_mm=13.0, layer="EEHC-CAPACITOR", bold=True, align="center")
            s.rect(cur_x + 210, zy + 465.0, c_w - 420, 105.0, layer="EEHC-MCCB", width=2, fill="#eff6ff")
            s.text(cur_x + c_w / 2, zy + 530.0, "CAPACITOR MCCB: 320 A (40 C)", height_mm=12.0, layer="EEHC-MCCB", bold=True, align="center")
            s.text(cur_x + c_w / 2, zy + 490.0, "Breaking Capacity = 65 kA at 400V", height_mm=11.5, layer="EEHC-PROHIBITED", bold=True, align="center")

            for cap_i in range(3):
                cap_x = cur_x + 95 + cap_i * 205
                s.rect(cap_x, zy + 220.0, 160.0, 215.0, layer="EEHC-CAPACITOR", width=2, fill="#d1fae5")
                s.text(cap_x + 80, zy + 365.0, f"DELTA C{cap_i+1}", height_mm=11.5, layer="EEHC-CAPACITOR", bold=True, align="center")
                s.text(cap_x + 80, zy + 330.0, "Un = 525 V", height_mm=11.0, bold=True, align="center")
                s.text(cap_x + 80, zy + 295.0, "<=50V / 60s", height_mm=10.5, layer="EEHC-PROHIBITED", bold=True, align="center")
                s.text(cap_x + 80, zy + 260.0, "130,000 hrs", height_mm=10.5, bold=True, align="center")

        cur_x += c_w

    # Overall Lineup Dimensions
    s.dim_h(zx, cur_x, zy - 125.0, "COMPLETE 2500 KVA PANEL LINEUP (SEPARATE INCOMING ACB CUBICLE + OUTGOING CUBICLES <=1m EACH + CAPACITOR/LIGHTING CUBICLE)")
    s.dim_v(zx - 65.0, zy, zy + cub_h, "PANEL BODY")
    s.dim_v(zx - 65.0, zy - 65.0, zy, "PLINTH >=10cm")

    # =========================================================================
    # ZONE 2 (BOTTOM LEFT, x=160..3200, y=340..2110): COMPACT MASTER SLD & SUBSYSTEMS
    # =========================================================================
    s.rect(160.0, 340.0, 3040.0, 1760.0, layer="EEHC-TITLE", width=3, fill="#f8fafc")
    s.rect(160.0, 2040.0, 3040.0, 60.0, layer="EEHC-TITLE", width=2, fill="#0f172a")
    s.text(1680.0, 2060.0, "2. MASTER SINGLE LINE DIAGRAM (SLD), BUSBAR / SANDWICH INSULATOR SECTION & TRANSFORMER CONNECTION METHODS", height_mm=17.5, bold=True, align="center", color="#ffffff")

    # Draw Master SLD on left half of Zone 2 (x=200..1950, y=380..2010)
    ms_x = 1050.0
    s.circle(ms_x, 1950.0, 35.0, layer="EEHC-ACB", width=3)
    s.circle(ms_x, 1900.0, 35.0, layer="EEHC-ACB", width=3)
    s.text(ms_x + 55, 1935.0, "2500 KVA TRANSFORMER (4500 A, 400 V, 50 Hz)", height_mm=14.0, layer="EEHC-TITLE", bold=True)
    s.line(ms_x, 1865.0, ms_x, 1460.0, layer="EEHC-BUS-R", width=4)
    # 2 CT sets
    s.circle(ms_x, 1815.0, 14.0, layer="EEHC-METER", width=2)
    s.circle(ms_x, 1780.0, 14.0, layer="EEHC-METER", width=2)
    s.text(ms_x + 30, 1790.0, "2 Sets x 3 Ring CTs (5000/5A, Cl 0.5, 10VA, Ith=10kA) + 3 Ammeters + Voltmeter + 3 LEDs + KWH Space", height_mm=11.5, layer="EEHC-METER", bold=True)

    # Main ACB box in SLD
    s.rect(ms_x - 210, 1520.0, 420.0, 210.0, layer="EEHC-ACB", width=3, fill="#eff6ff")
    s.text(ms_x, 1690.0, "MAIN 3P ACB: Frame=5000A, In=5000A (40C) / 4700A (50C)", height_mm=12.5, layer="EEHC-ACB", bold=True, align="center")
    s.text(ms_x, 1650.0, "Icu = Ics = 65 kA at 400V | Ui = 750V | Uimp = 8kV", height_mm=12.0, bold=True, align="center")
    s.text(ms_x, 1610.0, "Thermal Ir = (0.4 - 1.0) In | Magnetic Im = (2 - 10) In", height_mm=12.0, bold=True, align="center")
    s.text(ms_x, 1570.0, "WITHOUT Under-Voltage Coil | With Shunt Trip & 2 Flag Relays", height_mm=11.5, layer="EEHC-PROHIBITED", bold=True, align="center")
    s.text(ms_x, 1535.0, "Installed in Separate Cubicle (No MCCB/Switch-Fuse Below)", height_mm=11.5, layer="EEHC-PROHIBITED", bold=True, align="center")

    # Main Busbar Line in SLD (y=1420..1460)
    for b_i, b_lyr in enumerate(("EEHC-BUS-R", "EEHC-BUS-Y", "EEHC-BUS-B", "EEHC-BUS-N", "EEHC-BUS-PE")):
        s.line(220.0, 1460.0 - b_i * 12.0, 1980.0, 1460.0 - b_i * 12.0, layer=b_lyr, width=3)
    s.text(1100.0, 1475.0, "MAIN BUSBARS: 4500A, 65kA/1s | Phases=3000 mm2, Neutral=1800 mm2, Earth>=750 mm2 | Cu >=99.99%, >=57 MS/m + SANDWICH INSULATORS", height_mm=11.5, layer="EEHC-TITLE", bold=True, align="center")

    # 4 SLD Branches below Main Busbar
    branches_master = [
        (420.0, "EEHC-MCCB", "OPTION A: 16 x MCCB", ["16 x 3P MCCB 400A (40C)", "372A at 50C (Table-3)", "Icu = Ics = 65 kA (400V)", "Ir=(0.8-1)In, Im=(5-10)In", "Ui=750V, Uimp=8kV", "Clearance >= 6 cm", "Ext. Cu Bars on Insulators", "Terminals Height >= 55 cm"]),
        (870.0, "EEHC-SWFUSE", "OPTION B: 20 x SW-FUSE", ["20 x Vertical 400/630A", "Size 3, 500V, 50kA, AC-22B", "Pole Center = 210 mm", "Cycles >=200 (load) / >=800", "Arcing Chamber, V0, IP20, IK07", "Cu >=57 MS/m, 5um Silver", "250 mm2 Copper Droppers", "12mm Galv. Bolt + Holders >=8cm"]),
        (1320.0, "EEHC-LIGHTING", "STREET LIGHTING (63A)", ["1x 3P MCCB 63A, 65kA", "Ir = (0.8 - 1.0) In", "1x 3Ph Contactor 63A, 220V", "1x 1P MCB 10A Control", "Digital Timer OR Photocell", "CdS 220V, 50c/s, 1000W, NC", "Acrylic UV Housing/Window", "Alu Twist-Lock + 360° Bracket"]),
        (1760.0, "EEHC-CAPACITOR", "FIXED CAPACITOR 125kVAR", ["1x 3P MCCB 320A, 65kA", "125 KVAR @ 400V (Un=525V)", "Dry Self-Healing, Delta (Δ)", "Discharge <=50V in 60 sec", "Losses <0.2 / <0.5 W/KVAR", "Imax >=1.3In, Inrush >=200In", "-5% to +10%, -5C to +55C", ">=130,000h (Cl.D) + Overpress."]),
    ]
    for br_x, br_lyr, br_hdr, br_items in branches_master:
        s.line(br_x, 1460.0, br_x, 1280.0, layer=br_lyr, width=3)
        s.rect(br_x - 205, 760.0, 410.0, 520.0, layer=br_lyr, width=3, fill="#ffffff")
        s.rect(br_x - 205, 1225.0, 410.0, 55.0, layer=br_lyr, width=2, fill=s._color(br_lyr))
        s.text(br_x, 1245.0, br_hdr, height_mm=13.5, bold=True, align="center", color="#ffffff")
        for it_i, it_txt in enumerate(br_items):
            s.text(br_x - 190, 1185.0 - it_i * 50.0, "• " + it_txt, height_mm=12.0, bold=True)

    # Right Side of Zone 2 (x=2020..3170, y=380..2010): Mandatory Structural & Connection Summary Box
    s.rect(2020.0, 760.0, 1150.0, 1240.0, layer="EEHC-ENCLOSURE", width=3, fill="#ffffff")
    s.rect(2020.0, 1945.0, 1150.0, 55.0, layer="EEHC-ENCLOSURE", width=2, fill="#1e3a8a")
    s.text(2595.0, 1965.0, "MANDATORY STRUCTURAL, WIRING & CONNECTION CHECKLIST (SECTION 6)", height_mm=14.0, bold=True, align="center", color="#ffffff")
    chk_lines = [
        "1. Steel Thicknesses (Cl. 6.1): Frame >= 2.5 mm before paint; Indoor Body >= 2.0 mm (IP41);",
        "   Outdoor Body >= 2.5 mm before paint or IK10 with cert. (IP54); Internal Hinged Mirrors >= 1.25 mm.",
        "2. Cubicle Width & Plinths (Cl. 6.1, 6.2): Outgoing cubicle width <= 1 m with longitudinal door",
        "   stiffener; Each cubicle mounted on 4 Plinths height >= 10 cm (100 mm) above ground.",
        "3. Hinges, Locks & Roof (Cl. 6.2-6.5): >= 3 anti-rust galvanized/SS hinges per door; Nickel-plated",
        "   Master-Key handle with 3-point rod (سباليونة); Sloped roof >= 6 deg; Filtered louvers (SS mesh).",
        "4. Sandwich Insulator (عازل المسطرة - Cl. 6.8): Mandatory for all panels >= 500 KVA to brace",
        "   main busbars vertically & horizontally, plus porcelain/cast-resin insulators at ends/partitions.",
        "5. Clearances (Cl. 6.9): >= 6 cm between MCCBs & body; >= 2 cm between vertical switch-fuses.",
        "6. Switch-Fuse Protection (Cl. 6.11, 6.12, 6.16): Mandatory 250 mm2 copper droppers; Galvanized",
        "   12 mm (0.5\") bolt + 2 flat washers + 1 spring washer + 1 nut; Cable holders Dia >= 8 cm.",
        "7. Neutral & Earth (Cl. 6.13, 6.14): Pre-drilled neutral bar (1800 mm2) with galvanized bolts;",
        "   Earth bar >= 750 mm2 (>=25% of phase) with 2 end bolts + earthing bolt on every metal part.",
        "8. Painting Rule (Cl. 6.17, 6.18): Chemical treatment + electrostatic powder paint; STRICTLY",
        "   FORBIDDEN to perform any mechanical work (drilling/cutting/welding) after painting.",
        "9. Internal Services & Wiring (Cl. 6.20, 6.23, 6.24): Non-flammable PVC Cu wires >= 2 mm2",
        "   (450/750V) in plastic ducts; 220V Socket with MCB + Internal LED Lamp (>=9W) with MCB.",
        "10. Transformer Connection (Cl. 6.25): (A) Top 20 cm Busbar Tree through Paxolin plate,",
        "    OR (B) Top Busway Flange (no extension links), OR (C) Bottom Cables (Holes >=8cm + Gaskets).",
    ]
    for c_i, c_line in enumerate(chk_lines):
        s.text(2040.0, 1900.0 - c_i * 58.0, c_line, height_mm=11.8, bold=True)

    # Bottom of Zone 2 (x=200..3170, y=370..720): Official Riveted Nameplate Detail (Clause 6.19) & Derating Table-3
    s.rect(200.0, 370.0, 1450.0, 350.0, layer="EEHC-FRAME", width=3, fill="#e2e8f0")
    s.circle(230.0, 690.0, 8.0, layer="EEHC-ENCLOSURE", fill="#334155")
    s.circle(1620.0, 690.0, 8.0, layer="EEHC-ENCLOSURE", fill="#334155")
    s.circle(230.0, 400.0, 8.0, layer="EEHC-ENCLOSURE", fill="#334155")
    s.circle(1620.0, 400.0, 8.0, layer="EEHC-ENCLOSURE", fill="#334155")
    s.text(925.0, 680.0, "OFFICIAL PANEL NAMEPLATE FIXED BY RIVETS (مسامير برشام - CLAUSE 6.19)", height_mm=14.0, layer="EEHC-TITLE", bold=True, align="center")
    s.text(925.0, 635.0, "EGYPTIAN ELECTRICITY HOLDING COMPANY (EEHC) / DISTRIBUTION COMPANY: [....................]", height_mm=12.5, bold=True, align="center")
    s.text(925.0, 590.0, "MANUFACTURER NAME & TRADEMARK: [..............................] | SERIAL NO.: [................]", height_mm=12.0, bold=True, align="center")
    s.text(925.0, 545.0, "PANEL RATING: 2500 KVA | RATED CURRENT: 4500 A | 400 V AC, 3-PH, 4-WIRE, 50 Hz | Icw = 65 kA / 1s", height_mm=12.5, layer="EEHC-PROHIBITED", bold=True, align="center")
    s.text(925.0, 500.0, "MAIN ACB: 5000 A (40 C) / 65 kA | BUSBARS: 3000 / 1800 / >=750 mm2 | IP41 (INDOOR) / IP54 (OUTDOOR)", height_mm=12.0, bold=True, align="center")
    s.text(925.0, 455.0, "SUPPLY CONTRACT NO. & DATE: [........................] | PRODUCTION DATE: [..........] | WEIGHT: [...... kg]", height_mm=12.0, bold=True, align="center")

    s.table(
        1700.0,
        720.0,
        "TABLE (3) PAGE 16: AMBIENT TEMPERATURE DERATING VALUES FOR 2500 KVA PANEL",
        [("Circuit Breaker", 450.0), ("25 C", 240.0), ("40 C (Ref)", 260.0), ("50 C", 240.0), ("60 C", 240.0)],
        [
            ["Main Incoming ACB (5000 A)", "5500 A", "5000 A", "4700 A", "4125 A"],
            ["Outgoing Feeder MCCB (400 A)", "440 A", "400 A", "372 A", "340 A"],
            ["Fixed Capacitor MCCB (320 A)", "352 A", "320 A", "298 A", "272 A"],
            ["Street Lighting MCCB (63 A)", "69 A", "63 A", "58.6 A", "53.5 A"],
        ],
        row_h=34.0,
    )

    # =========================================================================
    # ZONE 3 (RIGHT COLUMN, x=3250..5690, y=340..3660): ALL 5 EEHC GUARANTEE TABLES
    # Guarantee Tables 1, 2, 3, 4, 5 (Pages 17, 18, 19, 20, 21 of EDMS 02-300-3)
    # =========================================================================
    gtx = 3250.0
    s.text(4470.0, 3630.0, "3. COMPLETE EEHC GUARANTEE TABLES NO. (1, 2, 3, 4, 5) - PAGES 17 TO 21 OF EDMS 02-300-3 (2500 KVA)", height_mm=19.5, layer="EEHC-TITLE", bold=True, align="center")
    s.text(4470.0, 3590.0, "جداول الضمان الفنية الخمسة كاملة (صفحات 17 إلى 21 من المواصفة) محققة بالكامل للوحة توزيع 2500 ك.ف.أ", height_mm=17.5, layer="EEHC-TEXT-AR", bold=True, align="center")

    # Guarantee Table 1 (Page 17)
    s.table(
        gtx,
        3550.0,
        "GUARANTEE TABLE NO. (1) - PAGE 17: ENCLOSURE & MAIN BUSBARS (2500 KVA PANEL)",
        [("Item", 110.0), ("Characteristics", 780.0), ("Mandatory Guaranteed Value (EDMS 02-300-3)", 1510.0)],
        [
            ["1, 2", "Manufacturer's Name & Type Designation", "According to EDC Vendor List | LV Distribution Panel 2500 KVA (4500 A)"],
            ["3", "Applicable Standards", "IEC 61439-1,2 | IEC 60947-1,2,3 | IEC 60529 | IEC 60831-1,2 | EDMS 02-300-3"],
            ["4, 5, 6", "Max Voltage (Ui) / Operating Voltage / Impulse (Uimp)", "Ui = 750 V | Operating = 400 V AC, 50 Hz | Uimp = 8 kV | 1-sec P.F. = 2.5 kV"],
            ["7, 8", "Rated Current / Busbar Current Density", "Panel & Busbar Current = 4500 A | Current Density = 1.5 A/mm2"],
            ["9, 10", "Main Busbars CSA & Material Purity / Conductivity", "Phases = 3000 mm2 | Neutral = 1800 mm2 | Earth >= 750 mm2 | Cu >= 99.99%, >= 57 MS/m"],
            ["11", "Short-Circuit Withstand Current (1 sec)", "65 kA r.m.s. at 400 V AC (Supported by Mandatory Sandwich Insulators - عازل المسطرة)"],
            ["12", "Protection Degree of Enclosure (IP)", "IP41 (Indoor Panels) | IP54 (Outdoor Panels with >= 6 deg Sloped Roof)"],
            ["13", "Enclosure Sheet Steel Thicknesses", "Frame >= 2.5 mm | Indoor >= 2.0 mm | Outdoor >= 2.5 mm (or IK10) | Internal Mirrors >= 1.25 mm"],
        ],
        row_h=27.0,
    )

    # Guarantee Table 2 (Page 18)
    s.table(
        gtx,
        2910.0,
        "GUARANTEE TABLE NO. (2) - PAGE 18: MAIN CIRCUIT BREAKER (ACB 5000 A)",
        [("Item", 110.0), ("Characteristics", 780.0), ("Mandatory Guaranteed Value (EDMS 02-300-3)", 1510.0)],
        [
            ["1, 2, 3", "Manufacturer, Type & Number of Poles", "According to EDC Vendor List | 3-Pole Air Circuit Breaker (ACB) in Separate Cubicle"],
            ["4", "Applicable Standards", "IEC 60947-1, IEC 60947-2, EDMS 02-300-3 (NO MCCBs/Switch-Fuses allowed below ACB)"],
            ["5, 6", "Frame Current & Nominal Current (In) at 40 C", "Frame = 5000 A at 40 C | In = 5000 A at 40 C (5500A @ 25C, 4700A @ 50C, 4125A @ 60C)"],
            ["7, 8, 9", "Rated Voltage / Max Voltage (Ui) / Impulse (Uimp)", "Operating = 400 V, 50 Hz | Ui = 750 V | Uimp = 8 kV"],
            ["10", "Nominal Breaking Capacity (Icu = Ics)", "65 kA at 400 V AC (Icu = Ics = 65 kA)"],
            ["11, 12", "Overload (Thermal) & Short-Circuit Releases", "Thermal Overload: Adjustable (0.4 - 1.0) In | Short-Circuit: Adjustable (2 - 10) In"],
            ["13", "Under-Voltage Release / Shunt Trip & Flag Relays", "WITHOUT Under-Voltage Release Coil | With Shunt Trip + 2 Flag Relays (as requested)"],
        ],
        row_h=27.0,
    )

    # Guarantee Table 3 (Page 19)
    s.table(
        gtx,
        2310.0,
        "GUARANTEE TABLE NO. (3) - PAGE 19: OUTGOING MOLDED CASE CIRCUIT BREAKERS (16 x 400 A MCCB)",
        [("Item", 110.0), ("Characteristics", 780.0), ("Mandatory Guaranteed Value (EDMS 02-300-3)", 1510.0)],
        [
            ["1, 2, 3", "Manufacturer, Type, Quantity & Poles", "According to EDC Vendor List | 16 x 3-Pole MCCB (EDMS 19-303) + 1x63A Lighting + 1x320A Cap."],
            ["4, 5", "Frame Current & Nominal Current (In) at 40 C", "Outgoing: Frame = 400 A, In = 400 A at 40 C (372 A at 50 C) | Clearance >= 6 cm"],
            ["6, 7, 8", "Rated Voltage / Max Voltage (Ui) / Impulse (Uimp)", "Operating = 400 V, 50 Hz | Ui = 750 V | Uimp = 8 kV"],
            ["9", "Nominal Breaking Capacity (Icu = Ics)", "65 kA at 400 V AC (Icu = Ics = 65 kA)"],
            ["10, 11", "Overload (Thermal) & Short-Circuit Releases", "Thermal: (0.8 - 1.0) In | Short-Circuit: (5 - 10) In | Additional Insulated Cu Output Bars"],
        ],
        row_h=27.0,
    )

    # Guarantee Table 4 (Page 20)
    s.table(
        gtx,
        1770.0,
        "GUARANTEE TABLE NO. (4) - PAGE 20: VERTICAL FUSE-SWITCH DISCONNECTORS (20 x 400/630 A, SIZE 3)",
        [("Item", 110.0), ("Characteristics", 780.0), ("Mandatory Guaranteed Value (EDMS 19-302 & 02-300-3)", 1510.0)],
        [
            ["1, 2, 3", "Manufacturer, Model, Quantity & Standards", "20 x Vertical Switch-Fuse Size 3 | IEC 60947-1, IEC 60947-3, EDMS 19-302 & 19-300"],
            ["4, 5, 6", "Rated Current / Voltage / Pole Center / Breaking Cap.", "400/630 A | 500 V, 50 Hz | Busbar Pole Center = 210 mm | Short-Circuit = 50 kA"],
            ["7, 8, 9", "Operating Cycles / Utilization / Overvoltage / Pollution", ">= 200 cycles (with current), >= 800 (no current) | AC-22B | Overvoltage Cat. III | Pollution 3"],
            ["10, 11", "Arcing Chamber / Flammability / IP & IK Ratings", "Arcing Chamber: YES | Insulation Flammability: V0 | Protection: IP20 | Mech. Shock: IK07"],
            ["12, 13", "Contacts Material, Silver Coating & Copper Droppers", "Cu >= 57 MS/m, Silver = 5 um | 250 mm2 Cu Droppers + 12mm (0.5\") Galv. Bolts + Holders >=8cm"],
        ],
        row_h=27.0,
    )

    # Guarantee Table 5 (Page 21)
    s.table(
        gtx,
        1220.0,
        "GUARANTEE TABLE NO. (5) - PAGE 21: FIXED CAPACITOR BANK (125 KVAR AT 400 V)",
        [("Item", 110.0), ("Characteristics", 780.0), ("Mandatory Guaranteed Value (EDMS 02-300-3 CLAUSE 5.3)", 1510.0)],
        [
            ["1, 2, 3", "Manufacturer, Country of Origin & Standards", "To be stated | IEC 60831-1,2 & EDMS 02-300-3 | Protected by 320 A, 65 kA MCCB"],
            ["4, 5, 6", "Technology, Rated Power (KVAR) & Voltages", "Dry Self-Healing | 125 KVAR at 400 V Operating Voltage | Design Voltage (Un) = 525 V, 50 Hz"],
            ["7, 8", "Internal Connection & Discharge Time/Method", "Delta (Δ) Connection | Discharge Resistors to <= 50 V within 60 seconds"],
            ["9, 10", "Total Losses, Dielectric Losses & Max Currents", "Dielectric < 0.2 W/KVAR, Total < 0.5 W/KVAR | Imax >= 1.3x In | Peak Inrush >= 200x In"],
            ["11, 12", "Capacitance Tolerance, Temp., Humidity & Lifetime", "-5% to +10% | -5 C to +55 C | 95% RH | Life >= 130,000 hrs (Class D) + 3-Ph Overpressure"],
            ["13", "Insulation Test Voltage & Impulse Level", "2.5 kV r.m.s. for 1 minute (between phases & body) | Impulse Withstand = 8 kV (1.2/50 us)"],
        ],
        row_h=27.0,
    )

    dxf_p = out_dir / "06_MASTER_DRAWING_2500KVA_EEHC_COMPLETE.dxf"
    png_p = out_dir / "06_MASTER_DRAWING_2500KVA_EEHC_COMPLETE.png"
    s.save(dxf_p, png_p)
    return dxf_p, png_p


# ---------------------------------------------------------------------------
# AUTOCAD 2021 CORE CONSOLE DXF -> DWG CONVERTER
# ---------------------------------------------------------------------------
def convert_dxf_to_dwg(dxf_path: Path, dwg_path: Path) -> bool:
    accore = Path(r"C:\Program Files\Autodesk\AutoCAD 2021\accoreconsole.exe")
    if not accore.exists():
        print(f"[WARN] accoreconsole.exe not found at {accore}")
        return False

    if dwg_path.exists():
        try:
            dwg_path.unlink()
        except OSError:
            pass

    scr_path = dxf_path.with_suffix(".scr")
    # Write AutoCAD script to import DXF, zoom extents, and save as 2018 DWG
    scr_content = (
        f'_.DXFIN "{dxf_path.resolve()}"\n'
        f"_.ZOOM _E\n"
        f'_.SAVEAS 2018 "{dwg_path.resolve()}"\n'
        f"_.QUIT _Y\n"
    )
    scr_path.write_text(scr_content, encoding="utf-8")

    try:
        cmd = [str(accore), "/s", str(scr_path.resolve()), "/l", "en-US"]
        res = subprocess.run(cmd, capture_output=True, timeout=90)
        if dwg_path.exists() and dwg_path.stat().st_size > 0:
            try:
                scr_path.unlink()
            except OSError:
                pass
            return True
        else:
            print(f"[WARN] DWG conversion did not produce {dwg_path.name} (rc={res.returncode})")
            return False
    except Exception as exc:
        print(f"[ERROR] accoreconsole failed for {dxf_path.name}: {exc}")
        return False


def main() -> None:
    out_dir = Path(__file__).resolve().parent.parent / "workspace" / "eehc-2500kva-panel-drawings"
    out_dir.mkdir(parents=True, exist_ok=True)

    builders = [
        ("Sheet 01 - Main Breaker ACB 5000A Cubicle", build_sheet_01_main_acb),
        ("Sheet 02 - Outgoing Cells (16xMCCB & 20xSwitch-Fuse)", build_sheet_02_outgoing_cells),
        ("Sheet 03 - Fixed Capacitor 125kVAR & Street Lighting 63A", build_sheet_03_capacitor_and_lighting),
        ("Sheet 04 - Enclosure, Sheet Thicknesses & Busbars", build_sheet_04_enclosure_and_busbars),
        ("Sheet 05 - Complete Single Line Diagram (SLD)", build_sheet_05_single_line_diagram),
        ("Sheet 06 - MASTER COMBINED DRAWING (All-in-One)", build_sheet_06_master_composite),
    ]

    for label, fn in builders:
        print(f"Building {label} ...")
        dxf_p, png_p = fn(out_dir)
        dwg_p = dxf_p.with_suffix(".dwg")
        ok = convert_dxf_to_dwg(dxf_p, dwg_p)
        print(f"  -> DXF: {dxf_p.name} ({dxf_p.stat().st_size:,} bytes)")
        print(f"  -> PNG: {png_p.name} ({png_p.stat().st_size:,} bytes)")
        if ok:
            print(f"  -> DWG: {dwg_p.name} ({dwg_p.stat().st_size:,} bytes)")

    print("\nALL 6 EEHC 2500 KVA DRAWINGS COMPLETED SUCCESSFULLY.")


if __name__ == "__main__":
    main()
