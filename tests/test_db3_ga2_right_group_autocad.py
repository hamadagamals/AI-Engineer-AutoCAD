"""Tests for the exact DB3-GA2 right-group AutoCAD overlay."""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from integrations.db3_ga2_right_group_autocad import (
    EXPECTED_ENTITY_COUNT,
    build_right_group_autocad_plan,
)


PREVIEW = (
    Path(__file__).resolve().parents[1]
    / "workspace" / "reports" / "db3-ga2-right-group-highlight-preview-20260824.json"
)

pytestmark = pytest.mark.skipif(
    not PREVIEW.is_file(),
    reason="Field-project DB3-GA2 preview JSON is absent in this checkout (gitignored workspace/reports).",
)


def test_exact_overlay_plan_is_preview_bound_and_non_destructive_to_source() -> None:
    preview = json.loads(PREVIEW.read_text(encoding="utf-8"))
    plan = build_right_group_autocad_plan(
        preview, expected_preview_sha256=preview["preview_sha256"]
    )

    assert plan.entity_count == EXPECTED_ENTITY_COUNT == 65
    assert plan.route_count == 30
    assert plan.block_count == 28
    assert plan.script.count("(aieng-copy-route \"") == 30
    assert plan.script.count("(aieng-halo \"") == 32
    assert "SOURCE CCT 5 ASSIGNED TO CCT 7" in plan.script
    assert '(command-s "_.QSAVE")' in plan.script
    assert "_.ERASE" not in plan.script
    assert "vla-copy" not in plan.script
    assert "(entmakex data)" in plan.script

    changed = dict(preview)
    changed["selected_route_count"] = 29
    with pytest.raises(ValueError, match="approved SHA-256"):
        build_right_group_autocad_plan(
            changed, expected_preview_sha256=preview["preview_sha256"]
        )
