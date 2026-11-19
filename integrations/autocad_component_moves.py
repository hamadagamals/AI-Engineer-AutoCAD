"""Source-bound, read-only previews for existing AutoCAD component relocation.

This is the first modification contract, not an execution approval. Native extents,
source/capture reconciliation, and the existing drafting lifecycle remain required
before a later supported-API transaction may apply any preview.
"""

from __future__ import annotations

import hashlib
import json
import math
import re
from dataclasses import dataclass
from typing import Any

from agent.exceptions import AutoCADContextError
from integrations.autocad_context import ContextImportResult

_HANDLE = re.compile(r"[0-9A-F]{1,32}")
_SHA256 = re.compile(r"[0-9a-f]{64}")
MAXIMUM_COMPONENT_MOVES = 32
MAXIMUM_COORDINATE_MM = 1_000_000


@dataclass(frozen=True, slots=True)
class ComponentMoveTarget:
    """One explicitly identified block and an absolute target in millimetres."""

    object_id: str
    target_mm: tuple[float, float, float]

    def __post_init__(self) -> None:
        if not isinstance(self.object_id, str) or not _HANDLE.fullmatch(self.object_id):
            raise AutoCADContextError("Component move requires an exact uppercase block handle.")
        if not isinstance(self.target_mm, tuple) or len(self.target_mm) != 3:
            raise AutoCADContextError("Component move target must be an immutable XYZ tuple.")
        for number in self.target_mm:
            if (
                isinstance(number, bool)
                or not isinstance(number, (int, float))
                or abs(number) > MAXIMUM_COORDINATE_MM
                or not math.isfinite(number)
            ):
                raise AutoCADContextError("Component move coordinates must be bounded finite millimetres.")


def create_component_move_preview(
    imported: ContextImportResult,
    *,
    source_sha256: str,
    source_name: str,
    moves: tuple[ComponentMoveTarget, ...],
) -> dict[str, Any]:
    """Preview exact block translations without claiming collision checks or mutation.

    Source SHA and context evidence SHA are distinct identities. Matching the filename
    alone does not prove the capture came from the saved DWG; the result explicitly
    blocks execution until native reconciliation supplies that proof.
    """
    if not isinstance(imported, ContextImportResult):
        raise AutoCADContextError("Component move preview requires validated imported context.")
    if imported.read_only is not True or not _SHA256.fullmatch(imported.evidence_sha256):
        raise AutoCADContextError("Component move preview requires exact read-only capture evidence.")
    if not isinstance(source_sha256, str) or not _SHA256.fullmatch(source_sha256):
        raise AutoCADContextError("Component move source SHA-256 is invalid.")
    if not isinstance(moves, tuple) or not 1 <= len(moves) <= MAXIMUM_COMPONENT_MOVES:
        raise AutoCADContextError("Component move preview supports 1..32 exact targets.")
    if any(not isinstance(move, ComponentMoveTarget) for move in moves):
        raise AutoCADContextError("Component move preview requires typed targets.")
    if len({move.object_id for move in moves}) != len(moves):
        raise AutoCADContextError("A component handle cannot appear twice in one preview.")
    context = imported.context
    if context.truncated or context.truncation_reasons:
        raise AutoCADContextError("Truncated context cannot bind component relocation.")
    if context.drawing_units != 4:
        raise AutoCADContextError("Initial component relocation requires explicit millimetre drawing units.")
    if context.layout != "Model":
        raise AutoCADContextError("Initial component relocation supports Model space only.")
    if context.database_modified & ~(8 | 16):
        raise AutoCADContextError("Save or discard persistent drawing changes before relocation preview.")
    if source_name != context.document_name:
        raise AutoCADContextError("Source DWG filename differs from the captured document name.")
    entities = {entity.object_id: entity for entity in context.entities}
    if len(entities) != len(context.entities):
        raise AutoCADContextError("Component context contains duplicate handles.")
    layers = {layer.name: layer for layer in context.layers}
    items: list[dict[str, Any]] = []
    for move in moves:
        entity = entities.get(move.object_id)
        if entity is None:
            raise AutoCADContextError(f"Component handle {move.object_id} is absent from exact context.")
        if entity.entity_type != "INSERT" or entity.original_type != "BlockReference":
            raise AutoCADContextError(f"Component {move.object_id} is not a supported top-level block reference.")
        if entity.layout != "Model" or not entity.visible:
            raise AutoCADContextError(f"Component {move.object_id} must be visible in Model space.")
        if not entity.block_name or entity.block_name.startswith("*"):
            raise AutoCADContextError("Anonymous/dynamic block references require separate reviewed support.")
        layer = layers.get(entity.layer)
        if layer is None or layer.is_locked or layer.is_off or layer.is_frozen:
            raise AutoCADContextError(f"Component {move.object_id} layer is missing, locked, off, or frozen.")
        if len(entity.points) != 1 or entity.scale is None or entity.rotation_degrees is None:
            raise AutoCADContextError("Component preview is missing insertion, scale, or rotation evidence.")
        original = entity.points[0]
        if any(not math.isfinite(number) for number in original):
            raise AutoCADContextError("Component original insertion is invalid.")
        if move.target_mm[2] != original[2]:
            raise AutoCADContextError("Initial front-view relocation preserves Z elevation.")
        if tuple(move.target_mm) == tuple(original):
            raise AutoCADContextError("A component move must change its insertion point.")
        items.append({
            "object_id": move.object_id,
            "block_name": entity.block_name,
            "layer": entity.layer,
            "original_mm": list(original),
            "target_mm": list(move.target_mm),
            "displacement_mm": [target - start for target, start in zip(move.target_mm, original, strict=True)],
            "preserved_rotation_degrees": entity.rotation_degrees,
            "preserved_scale": list(entity.scale),
            "attached_attributes": [attribute.to_dict() for attribute in entity.attributes],
        })
    target_points = {tuple(item["target_mm"]) for item in items}
    if len(target_points) != len(items):
        raise AutoCADContextError("Two components cannot share the same proposed insertion point.")
    preview: dict[str, Any] = {
        "schema_version": 1,
        "preview_type": "autocad.component_move_preview.v1",
        "source_sha256": source_sha256,
        "document_id": context.document_id,
        "document_name": context.document_name,
        "context_id": context.context_id,
        "context_evidence_sha256": imported.evidence_sha256,
        "units": "millimeter",
        "items": items,
        "drawing_mutation_performed": False,
        "execution_approved": False,
        "execution_ready": False,
        "hold_points": [
            "Reconcile native saved-DWG fingerprint, block identity and transform against this exact capture.",
            "Capture native geometric extents and check component collisions, enclosure zones and reviewed clearances.",
            "Review Xref/dynamic/group dependencies; standalone labels, leaders and wires are not implicitly moved.",
            "Bind the exact preview to the drafting lifecycle, one-use approval, scratch transaction and recovery/postcheck.",
        ],
    }
    preview["preview_sha256"] = hashlib.sha256(
        json.dumps(preview, ensure_ascii=False, sort_keys=True, separators=(",", ":"), allow_nan=False).encode("utf-8")
    ).hexdigest()
    return preview
