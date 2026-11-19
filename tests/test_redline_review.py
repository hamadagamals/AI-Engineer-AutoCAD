"""Tests for color-coded redline review lifecycle and natural approval engine."""

from __future__ import annotations

import pytest

from core.redline_review import (
    DEFAULT_REVIEW_COLOR_INDEX,
    REVIEW_LAYER_ID,
    REVIEW_LAYER_NAME,
    RedlineReviewManager,
)
from tests.test_electrical_drafting import _proposal


def test_intent_detection() -> None:
    mgr = RedlineReviewManager()

    # Arabic approvals
    assert mgr.detect_intent("تمام") == "APPROVE"
    assert mgr.detect_intent("تم يا هندسة") == "APPROVE"
    assert mgr.detect_intent("اعتمد التعديل") == "APPROVE"
    assert mgr.detect_intent("موافق") == "APPROVE"
    assert mgr.detect_intent("احفظ الشغل") == "APPROVE"
    assert mgr.detect_intent("تمام التمام") == "APPROVE"

    # English approvals
    assert mgr.detect_intent("ok") == "APPROVE"
    assert mgr.detect_intent("looks good, approve") == "APPROVE"
    assert mgr.detect_intent("confirm and commit") == "APPROVE"

    # Arabic rejections
    assert mgr.detect_intent("تراجع") == "REJECT"
    assert mgr.detect_intent("الغاء التعديل") == "REJECT"
    assert mgr.detect_intent("احذفها مش تمام") == "REJECT"
    assert mgr.detect_intent("ارفض الخطة") == "REJECT"

    # English rejections
    assert mgr.detect_intent("cancel") == "REJECT"
    assert mgr.detect_intent("rollback changes") == "REJECT"
    assert mgr.detect_intent("reject") == "REJECT"

    # Neutral
    assert mgr.detect_intent("ما هو مقاس الكابل؟") == "NEUTRAL"
    assert mgr.detect_intent("احسب لي هبوط الجهد") == "NEUTRAL"


def test_convert_to_review_proposal() -> None:
    mgr = RedlineReviewManager()
    orig = _proposal()

    review_prop, mappings = mgr.convert_to_review_proposal(orig)

    # Review layer was added with Red color (index 1)
    review_layers = [l for l in review_prop.layers if l.layer_id == REVIEW_LAYER_ID]
    assert len(review_layers) == 1
    assert review_layers[0].name == REVIEW_LAYER_NAME
    assert review_layers[0].color_index == DEFAULT_REVIEW_COLOR_INDEX

    # Devices and routes mapped to review layer
    assert all(d.layer_id == REVIEW_LAYER_ID for d in review_prop.devices)
    assert all(r.layer_id == REVIEW_LAYER_ID for r in review_prop.routes)

    # Mappings recorded correctly
    assert len(mappings) == len(orig.devices) + len(orig.routes)
    dev_map = [m for m in mappings if m.entity_id == "device:light-1"][0]
    assert dev_map.preview_layer_name == REVIEW_LAYER_NAME
    assert dev_map.target_layer_name == "E-LITE-WIRE"

    # Commit instruction generated
    commit_instr = mgr.build_commit_instruction(mappings)
    assert commit_instr["action"] == "commit_review"
    assert len(commit_instr["entities"]) == len(mappings)
    assert commit_instr["entities"][0]["set_layer"] == "E-LITE-WIRE"

    # Rollback instruction generated
    rollback_instr = mgr.build_rollback_instruction()
    assert rollback_instr["action"] == "rollback_review"
    assert rollback_instr["erase_layer_entities"] is True
