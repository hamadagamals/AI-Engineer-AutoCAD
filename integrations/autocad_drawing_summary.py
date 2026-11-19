"""Bounded, approval-gated summaries of validated AutoCAD drawing indexes."""

from __future__ import annotations

import json
from collections import Counter
from pathlib import Path
from typing import Any

from integrations.autocad_context import parse_autocad_context
from integrations.autocad_drawing_index import load_drawing_index


_MAX_NAMED_ITEMS = 16
_MAX_NOTES = 10
_MAX_NOTE_CANDIDATES = 256
_MAX_NOTE_CHARACTERS = 120
_ENGINEERING_NOTE_TERMS = (
    "enclosure",
    "overall",
    "body h",
    "plinth",
    "depth",
    "cable",
    "busbar",
    "terminal",
    "form 3",
    "fabrication",
    "configuration tbd",
    "mcb basis",
    "in:",
    "out-",
)


def summarize_drawing_index_for_ai(
    workspace_root: Path,
    *,
    index_id: str,
    evidence_sha256: str,
) -> dict[str, Any]:
    """Return a compact engineering inventory suitable for approved provider egress.

    The exact hash-bound index is validated first. The returned value intentionally
    excludes local paths, complete entity records, attributes, and arbitrary geometry.
    Point bounds describe exported entity points and block insertion points only; they
    are not native AutoCAD geometric extents and cannot certify clearance or fit.
    """
    loaded = load_drawing_index(
        workspace_root,
        index_id=index_id,
        evidence_sha256=evidence_sha256,
    )
    entity_types: Counter[str] = Counter()
    layers: Counter[str] = Counter()
    layouts: Counter[str] = Counter()
    blocks: Counter[str] = Counter()
    note_candidates: list[str] = []
    seen_notes: set[str] = set()
    minimum: list[float] | None = None
    maximum: list[float] | None = None
    visible_count = 0

    for path in loaded.context_paths:
        context = parse_autocad_context(json.loads(path.read_text(encoding="utf-8")))
        for entity in context.entities:
            entity_types[entity.entity_type] += 1
            layers[entity.layer] += 1
            layouts[entity.layout] += 1
            if entity.visible:
                visible_count += 1
            if entity.block_name:
                blocks[_bounded_text(entity.block_name, 80)] += 1
            if entity.text and len(note_candidates) < _MAX_NOTE_CANDIDATES:
                note = _bounded_text(" ".join(entity.text.split()), _MAX_NOTE_CHARACTERS)
                if note and note not in seen_notes:
                    seen_notes.add(note)
                    note_candidates.append(note)
            for point in entity.points:
                coordinates = [round(float(value), 3) for value in point]
                if minimum is None:
                    minimum = coordinates.copy()
                    maximum = coordinates.copy()
                    continue
                assert maximum is not None
                for index, value in enumerate(coordinates):
                    minimum[index] = min(minimum[index], value)
                    maximum[index] = max(maximum[index], value)

    point_bounds: dict[str, list[float]] | None = None
    if minimum is not None and maximum is not None:
        point_bounds = {
            "minimum": minimum,
            "maximum": maximum,
            "span": [round(high - low, 3) for low, high in zip(minimum, maximum)],
        }

    manifest = loaded.manifest
    return {
        "summary_version": "autocad.ai_drawing_summary.v1",
        "index_id": manifest.index_id,
        "document_id": manifest.document_id,
        "drawing_name": _bounded_text(manifest.drawing_name, 160),
        "source_drawing_sha256": manifest.source_drawing_sha256,
        "drawing_units_code": manifest.drawing_units,
        "database_modified": manifest.database_modified_after,
        "entity_count": manifest.entity_count,
        "visible_entity_count": visible_count,
        "chunk_count": manifest.chunk_count,
        "layouts": _top_counts(layouts),
        "entity_types": _top_counts(entity_types),
        "layers": _top_counts(layers),
        "block_references": _top_counts(blocks),
        "point_bounds": point_bounds,
        "notable_text": _select_engineering_notes(note_candidates),
        "scope": {
            "top_level_model_and_paper_space": True,
            "nested_block_definitions": False,
            "external_references": False,
        },
        "engineering_hold_points": [
            "Point bounds are not native entity or block extents.",
            "Thermal losses, ambient conditions, ventilation, cable bending radii, and manufacturer clearances are not established by this summary.",
            "No spacing, temperature-rise, fabrication, or standards compliance is certified.",
        ],
        "read_only": True,
        "drawing_mutation_performed": False,
        "provider_egress_performed": True,
    }


def _top_counts(values: Counter[str]) -> list[dict[str, object]]:
    return [
        {"name": name, "count": count}
        for name, count in sorted(values.items(), key=lambda item: (-item[1], item[0]))[
            :_MAX_NAMED_ITEMS
        ]
    ]


def _select_engineering_notes(values: list[str]) -> list[str]:
    ranked = sorted(
        enumerate(values),
        key=lambda item: (
            -sum(term in item[1].casefold() for term in _ENGINEERING_NOTE_TERMS),
            item[0],
        ),
    )
    return [value for _ordinal, value in ranked[:_MAX_NOTES]]


def _bounded_text(value: str, maximum_characters: int) -> str:
    cleaned = "".join(character for character in value if character >= " " or character == "\t")
    if len(cleaned) <= maximum_characters:
        return cleaned
    return cleaned[: maximum_characters - 1] + "…"
