"""Color-coded redline review lifecycle and natural language approval engine for CAD/BIM.

Implements the user's core workflow:
1. Draft / Modify in distinct review state with redline color (AutoCAD ACI 1 = Red).
2. Human inspects visually on the drawing.
3. Natural confirmation ('تمام', 'اعتمد', 'ok', 'approve') commits to production layers.
4. Natural rejection ('تراجع', 'الغاء', 'cancel', 'revert') rolls back cleanly.
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Any, Mapping

from agent.exceptions import DraftingContractError
from core.electrical_drafting import (
    DraftingProposal,
    ProposedCircuit,
    ProposedDevice,
    ProposedLayer,
    ProposedRoute,
    ProposedTerminal,
    create_drafting_proposal,
)

REVIEW_LAYER_ID = "layer:ai-preview-review"
REVIEW_LAYER_NAME = "_AI_PREVIEW_REVIEW"
DEFAULT_REVIEW_COLOR_INDEX = 1  # AutoCAD Color Index 1 is Red


_REJECT_PATTERNS = re.compile(
    r"\b(تراجع|الغاء|إلغاء|ارفض|رفض|مش\s+تمام|احذف|احذفها|حذف|امسح|امسحها|مسح|لا|cancel|cancelled|revert|reverted|rollback|reject|rejected|delete|abort|not\s+ok)\b",
    re.IGNORECASE | re.UNICODE,
)

_APPROVE_PATTERNS = re.compile(
    r"\b(تمام|تم|اعتمد|اعتمدها|موافق|ثبت|تثبيت|احفظ|احفظها|حفظ|نعم|ممتاز|approve|approved|confirm|confirmed|commit|committed|ok|looks\s+good|proceed)\b",
    re.IGNORECASE | re.UNICODE,
)


@dataclass(frozen=True, slots=True)
class ReviewCommitMapping:
    """Records the target production layer and properties for each reviewed entity."""

    entity_id: str
    entity_kind: str  # device, route, etc.
    preview_layer_name: str
    target_layer_name: str
    target_color_index: int


class RedlineReviewManager:
    """Manages the lifecycle of color-coded redline previews and intent-based commit."""

    def __init__(self, review_layer_name: str = REVIEW_LAYER_NAME, review_color_index: int = DEFAULT_REVIEW_COLOR_INDEX):
        self.review_layer_name = review_layer_name
        self.review_color_index = review_color_index

    def detect_intent(self, user_message: str) -> str:
        """Analyze user text for approval or rejection intent."""
        if not user_message or not isinstance(user_message, str):
            return "NEUTRAL"
        cleaned = user_message.strip()
        # Always check rejection/negation first
        if _REJECT_PATTERNS.search(cleaned):
            return "REJECT"
        if _APPROVE_PATTERNS.search(cleaned):
            return "APPROVE"
        return "NEUTRAL"

    def convert_to_review_proposal(
        self,
        proposal: DraftingProposal,
    ) -> tuple[DraftingProposal, tuple[ReviewCommitMapping, ...]]:
        """Transform a proposal so that all devices and routes render on the review layer."""
        review_layer = ProposedLayer(
            layer_id=REVIEW_LAYER_ID,
            name=self.review_layer_name,
            color_index=self.review_color_index,
            linetype="Continuous",
            create_if_missing=True,
        )

        layer_name_by_id = {layer.layer_id: layer.name for layer in proposal.layers}
        layer_color_by_id = {layer.layer_id: layer.color_index for layer in proposal.layers}

        mappings: list[ReviewCommitMapping] = []

        # Convert devices to review layer
        review_devices: list[ProposedDevice] = []
        for dev in proposal.devices:
            orig_layer_name = layer_name_by_id.get(dev.layer_id, dev.layer_id)
            orig_color = layer_color_by_id.get(dev.layer_id, 256)  # 256 = ByLayer
            mappings.append(
                ReviewCommitMapping(
                    entity_id=dev.device_id,
                    entity_kind="device",
                    preview_layer_name=self.review_layer_name,
                    target_layer_name=orig_layer_name,
                    target_color_index=orig_color,
                )
            )
            review_devices.append(
                ProposedDevice(
                    device_id=dev.device_id,
                    symbol_id=dev.symbol_id,
                    symbol_definition_sha256=dev.symbol_definition_sha256,
                    block_name=dev.block_name,
                    layer_id=REVIEW_LAYER_ID,
                    insertion_point=dev.insertion_point,
                    rotation_degrees=dev.rotation_degrees,
                    scale=dev.scale,
                    attributes=dev.attributes,
                )
            )

        # Convert routes to review layer
        review_routes: list[ProposedRoute] = []
        for route in proposal.routes:
            orig_layer_name = layer_name_by_id.get(route.layer_id, route.layer_id)
            orig_color = layer_color_by_id.get(route.layer_id, 256)
            mappings.append(
                ReviewCommitMapping(
                    entity_id=route.route_id,
                    entity_kind="route",
                    preview_layer_name=self.review_layer_name,
                    target_layer_name=orig_layer_name,
                    target_color_index=orig_color,
                )
            )
            review_routes.append(
                ProposedRoute(
                    route_id=route.route_id,
                    circuit_id=route.circuit_id,
                    layer_id=REVIEW_LAYER_ID,
                    start_terminal_id=route.start_terminal_id,
                    end_terminal_id=route.end_terminal_id,
                    points=route.points,
                )
            )

        new_layers = (review_layer,) + tuple(
            l for l in proposal.layers if l.layer_id != REVIEW_LAYER_ID
        )

        review_proposal = create_drafting_proposal(
            proposal_id=proposal.proposal_id,
            binding=proposal.binding,
            layers=new_layers,
            devices=tuple(review_devices),
            terminals=proposal.terminals,
            circuits=proposal.circuits,
            routes=tuple(review_routes),
            input_issues=proposal.input_issues,
            created_at=proposal.created_at,
            expires_at=proposal.expires_at,
        )

        return review_proposal, tuple(mappings)

    def build_commit_instruction(self, mappings: tuple[ReviewCommitMapping, ...]) -> dict[str, Any]:
        """Generate structured AutoCAD/Revit transaction instructions to finalize the drawing."""
        entities_to_update = [
            {
                "entity_id": m.entity_id,
                "kind": m.entity_kind,
                "set_layer": m.target_layer_name,
                "set_color_index": 256,  # ByLayer
            }
            for m in mappings
        ]
        return {
            "action": "commit_review",
            "review_layer": self.review_layer_name,
            "entities": entities_to_update,
            "save_drawing": True,
            "message_ar": "تم اعتماد وتثبيت التعديل بنجاح وتحويل العناصر للطبقات المعتمدة وحفظ المخطط.",
            "message_en": "Drafting changes approved and committed to production layers. Drawing saved.",
        }

    def build_rollback_instruction(self) -> dict[str, Any]:
        """Generate structured instructions to remove all preview redline elements."""
        return {
            "action": "rollback_review",
            "review_layer": self.review_layer_name,
            "erase_layer_entities": True,
            "message_ar": "تم التراجع عن التعديل المقترح وحذف عناصر المعاينة بالكامل دون تعديل المخطط الأصلي.",
            "message_en": "Drafting proposal rejected and preview entities removed.",
        }
