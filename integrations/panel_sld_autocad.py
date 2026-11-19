"""Validate one reviewed panel-SLD preview and build its fixed AutoCAD script."""

from __future__ import annotations

import hashlib
import json
import re
import textwrap
from dataclasses import dataclass
from typing import Any


PROFILE = "AIENG_DB3_GA2_SLD_V1"
BOUNDS = (38_000.0, -35_000.0, 61_000.0, -22_000.0)
LAYERS = (
    ("AIENG-SLD-DRAFT", 7),
    ("AIENG-SLD-ESTIMATED", 1),
    ("AIENG-SLD-TBD", 30),
    ("AIENG-SLD-BLANK", 8),
)
_SHA256 = re.compile(r"[0-9a-f]{64}")


@dataclass(frozen=True, slots=True)
class PanelSldAutoCADPlan:
    """Exact script and verification facts for one reviewed SLD insertion."""

    script: str
    entity_count: int
    preview_sha256: str
    source_drawing_sha256: str
    bounds: tuple[float, float, float, float]


def canonical_sha256(value: dict[str, Any]) -> str:
    """Return the canonical digest used by the no-mutation preview."""
    encoded = json.dumps(
        value, ensure_ascii=False, sort_keys=True, separators=(",", ":")
    ).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest()


def build_panel_sld_autocad_plan(
    preview: dict[str, Any], *, expected_preview_sha256: str
) -> PanelSldAutoCADPlan:
    """Validate the exact approved DB3-GA2 preview and build a fail-closed script."""
    if _SHA256.fullmatch(expected_preview_sha256) is None:
        raise ValueError("Expected SLD preview SHA-256 is invalid.")
    if not isinstance(preview, dict):
        raise ValueError("SLD preview must be a JSON object.")
    required = {
        "schema_version", "preview_type", "panel", "system", "frequency_hz",
        "rated_ways", "source_evidence_sha256", "source_drawing_sha256",
        "way_count_shown", "observed_way_count", "blank_way_count", "assumptions",
        "geometry", "drawing_mutation_performed", "preview_sha256",
    }
    if set(preview) != required:
        raise ValueError("SLD preview schema is invalid.")
    embedded = preview["preview_sha256"]
    unsigned = dict(preview)
    unsigned.pop("preview_sha256")
    if (
        embedded != expected_preview_sha256
        or canonical_sha256(unsigned) != expected_preview_sha256
    ):
        raise ValueError("SLD preview differs from the approved SHA-256.")
    source_sha = preview["source_drawing_sha256"]
    if _SHA256.fullmatch(source_sha or "") is None:
        raise ValueError("SLD preview source drawing SHA-256 is invalid.")
    geometry = preview["geometry"]
    if (
        preview["schema_version"] != 1
        or preview["preview_type"] != "panel-single-line-diagram-draft"
        or preview["panel"] != "DB3-GA2"
        or preview["system"] != "400/230 V, 3 Phase, 4 Wire"
        or preview["drawing_mutation_performed"] is not False
        or not isinstance(geometry, list)
        or len(geometry) != 19
        or preview["way_count_shown"] != 19
        or preview["observed_way_count"] != 7
        or preview["blank_way_count"] != 12
    ):
        raise ValueError("SLD preview is not the reviewed DB3-GA2 profile.")
    if [item.get("way") for item in geometry] != list(range(1, 20)):
        raise ValueError("SLD preview ways must be exactly 1 through 19.")
    if any(set(item) != {
        "way", "x", "observed", "description", "connected_w", "connected_va",
        "current_a", "status",
    } for item in geometry):
        raise ValueError("SLD preview way schema is invalid.")

    commands: list[str] = []
    entity_count = 0

    def add(expression: str) -> None:
        nonlocal entity_count
        commands.append(expression)
        entity_count += 1

    def point(px: float, py: float) -> tuple[float, float]:
        left, bottom, right, top = BOUNDS
        return (left + px / 1900.0 * (right - left), top - py / 1050.0 * (top - bottom))

    def line(a: tuple[float, float], b: tuple[float, float], layer: str = LAYERS[0][0]) -> None:
        add(f'(aieng-line "{layer}" {_pt(a)} {_pt(b)})')

    def text(px: float, py: float, height: float, value: str, layer: str = LAYERS[0][0]) -> None:
        add(f'(aieng-text "{layer}" {_pt(point(px, py))} {height:.3f} "{_escape(value)}")')

    def rect(px1: float, py1: float, px2: float, py2: float, layer: str = LAYERS[0][0]) -> None:
        a, b, c, d = point(px1, py1), point(px2, py1), point(px2, py2), point(px1, py2)
        line(a, b, layer); line(b, c, layer); line(c, d, layer); line(d, a, layer)

    def circle(px: float, py: float, radius_px: float, layer: str) -> None:
        radius = radius_px / 1900.0 * (BOUNDS[2] - BOUNDS[0])
        add(f'(aieng-circle "{layer}" {_pt(point(px, py))} {radius:.3f})')

    text(950, 45, 360, "SINGLE LINE DIAGRAM - DB3-GA2")
    text(55, 78, 210, "SYSTEM: 400/230 V, 3 PHASE, 4 WIRE")
    text(55, 103, 150, "FREQUENCY / INCOMER / BUSBAR / PROTECTION / CABLE / PHASE: TBD", LAYERS[2][0])
    line(point(950, 115), point(950, 145))
    rect(858, 145, 1042, 200)
    text(900, 170, 190, "INCOMER")
    text(875, 192, 140, "RATING / TYPE TBD", LAYERS[2][0])
    line(point(950, 200), point(950, 260))
    line(point(55, 260), point(1845, 260))
    text(1600, 225, 140, "BUSBAR RATING TBD", LAYERS[2][0])

    for item in geometry:
        px = float(item["x"])
        observed = item["observed"] is True
        layer = LAYERS[0][0] if observed else LAYERS[3][0]
        text(px - 8, 245, 135, str(item["way"]), layer)
        line(point(px, 260), point(px, 310), layer)
        rect(px - 31, 310, px + 31, 348, layer)
        if observed:
            text(px - 27, 336, 105, "CB TBD", LAYERS[2][0])
        line(point(px, 348), point(px, 435), layer)
        circle(px, 455, 19, layer)
        if not observed:
            continue
        text(px - 7, 462, 120, str(item["way"]), layer)
        y = 500.0
        for description in _compact_description(item.get("description")):
            text(px - 43, y, 85, description, layer)
            y += 16
        if item.get("connected_w") is None:
            text(px - 43, y + 8, 110, "LOAD TBD", LAYERS[2][0])
        else:
            text(px - 43, y + 5, 105, f'{float(item["connected_w"]):.1f} W', LAYERS[1][0])
            text(px - 43, y + 22, 105, f'{float(item["connected_va"]):.1f} VA', LAYERS[1][0])
            text(px - 43, y + 39, 105, f'{float(item["current_a"]):.3f} A', LAYERS[1][0])
        text(px - 43, y + 59, 80, "PHASE / CABLE TBD", LAYERS[2][0])

    rect(55, 900, 1845, 1005)
    text(75, 930, 180, "DRAFT NOTES")
    text(75, 955, 135, "RED NUMBERS = PROVISIONAL ESTIMATED LOAD VALUES.", LAYERS[1][0])
    text(75, 978, 130, "TBD = NOT PRESENT IN REVIEWED EVIDENCE. BLANK WAYS ARE NOT SPARES.", LAYERS[2][0])
    text(75, 1000, 90, f"PREVIEW SHA-256: {embedded}")

    layer_setup = "\n".join(
        f'(entmake (list \'(0 . "LAYER") \'(100 . "AcDbSymbolTableRecord") '
        f'\'(100 . "AcDbLayerTableRecord") (cons 2 "{name}") (cons 70 0) (cons 62 {color}) (cons 6 "Continuous")))'
        for name, color in LAYERS
    )
    layer_names = ",".join(name for name, _ in LAYERS)
    body = "\n".join(commands)
    left, bottom, right, top = BOUNDS
    script = f'''(progn
(vl-load-com)
(defun aieng-line (layer p1 p2) (entmakex (list '(0 . "LINE") (cons 8 layer) (cons 10 p1) (cons 11 p2))))
(defun aieng-circle (layer center radius) (entmakex (list '(0 . "CIRCLE") (cons 8 layer) (cons 10 center) (cons 40 radius))))
(defun aieng-text (layer point height value) (entmakex (list '(0 . "TEXT") (cons 8 layer) (cons 10 point) (cons 40 height) (cons 1 value) (cons 7 "Standard"))))
(setq aieng-before-selection (ssget "_X"))
(setq aieng-before-count (if aieng-before-selection (sslength aieng-before-selection) 0))
(setq aieng-before-selection nil)
(setq aieng-region-selection (ssget "_C" (list {left:.3f} {bottom:.3f} 0.0) (list {right:.3f} {top:.3f} 0.0)))
(setq aieng-region-before (if aieng-region-selection (sslength aieng-region-selection) 0))
(setq aieng-region-selection nil)
(setq aieng-layer-preexisting 0)
{_layer_preexisting_lisp()}
(if (and (= (getvar "INSUNITS") 4) (= (getvar "TILEMODE") 1) (= aieng-region-before 0) (= aieng-layer-preexisting 0))
  (progn
{layer_setup}
{body}
  )
)
(setq aieng-created-selection (ssget "_X" (list (cons 8 "{layer_names}"))))
(setq aieng-created-count (if aieng-created-selection (sslength aieng-created-selection) 0))
(setq aieng-created-selection nil)
(setq aieng-after-selection (ssget "_X"))
(setq aieng-after-count (if aieng-after-selection (sslength aieng-after-selection) 0))
(setq aieng-after-selection nil)
(setq aieng-content-valid (if (and (= aieng-created-count {entity_count}) (= aieng-after-count (+ aieng-before-count {entity_count}))) 1 0))
(if (= aieng-content-valid 1) (command-s "_.QSAVE"))
(princ (strcat "\\nAIENG_ACADVER=" (getvar "ACADVER")))
(princ (strcat "\\nAIENG_INSUNITS=" (itoa (getvar "INSUNITS"))))
(princ (strcat "\\nAIENG_TILEMODE=" (itoa (getvar "TILEMODE"))))
(princ "\\nAIENG_PROFILE={PROFILE}")
(princ "\\nAIENG_PREVIEWSHA256={embedded}")
(princ "\\nAIENG_SOURCESHA256={source_sha}")
(princ (strcat "\\nAIENG_REGIONBEFORE=" (itoa aieng-region-before)))
(princ (strcat "\\nAIENG_LAYERPREEXISTING=" (itoa aieng-layer-preexisting)))
(princ "\\nAIENG_EXPECTEDCOUNT={entity_count}")
(princ (strcat "\\nAIENG_CREATEDCOUNT=" (itoa aieng-created-count)))
(princ (strcat "\\nAIENG_CONTENTVALID=" (itoa aieng-content-valid)))
(princ (strcat "\\nAIENG_BEFORECOUNT=" (itoa aieng-before-count)))
(princ (strcat "\\nAIENG_AFTERCOUNT=" (itoa aieng-after-count)))
(princ (strcat "\\nAIENG_DBMOD=" (itoa (getvar "DBMOD"))))
(princ "\\nAIENG_DONE=1\\n")
(princ)
)
'''
    return PanelSldAutoCADPlan(script, entity_count, embedded, source_sha, BOUNDS)


def _pt(value: tuple[float, float]) -> str:
    return f"(list {value[0]:.3f} {value[1]:.3f} 0.0)"


def _escape(value: str) -> str:
    if not isinstance(value, str) or len(value) > 200:
        raise ValueError("SLD text is invalid or too long.")
    return value.replace("\\", "\\\\").replace('"', '\\"')


def _compact_description(value: str | None) -> list[str]:
    if not value:
        return []
    replacements = (
        ("Rectangular lighting fixture", "RECT. LIGHT"),
        ("Toilet spot lighting fixture", "TOILET LIGHT"),
        ("L03 spot lighting fixture", "L03 LIGHT"),
        ("L04 outlet spot lighting fixture", "L04 LIGHT"),
        ("Pendant lighting fixture", "PENDANT"),
        ("Extract fan", "EF"),
        ("LED tape segment", "LED TAPE"),
    )
    result: list[str] = []
    for item in value.split(";"):
        item = item.strip()
        for source, target in replacements:
            item = item.replace(source, target)
        result.extend(textwrap.wrap(item.upper(), width=15, break_long_words=False))
    if len(result) > 5:
        result = result[:5]
        result[-1] = result[-1].rstrip(".; ") + "..."
    return result


def _layer_preexisting_lisp() -> str:
    return "\n".join(
        f'(if (tblsearch "LAYER" "{name}") (setq aieng-layer-preexisting (+ aieng-layer-preexisting 1)))'
        for name, _ in LAYERS
    )
