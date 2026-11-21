"""Regression checks for the V5 load ledger and its fail-closed evidence gates."""

from __future__ import annotations

import copy
import json
import tempfile
from pathlib import Path

import pytest

from integrations.revit_school_v5_load_readiness import (
    SchoolV5ReadinessError,
    _workspace_path,
    _write_new,
    build_readiness,
    render_markdown,
)


ROOT = Path(__file__).resolve().parents[1]
EVIDENCE = ROOT / "workspace" / "revit-school" / "previews" / "electrical-distribution"


@pytest.fixture(scope="module")
def evidence() -> tuple[dict, dict, dict]:
    required = (
        "School-Electrical-V5-Execution-Preview-20261031.json",
        "School-Electrical-V5-Live-Handoff-20261031.json",
        "School-V5-Existing-Lighting-Count-20261031.json",
        "School-V5-Existing-Outlets-Count-20261031.json",
        "School-V5-Existing-Equipment-Count-20261031.json",
    )
    if any(not (EVIDENCE / name).is_file() for name in required):
        pytest.skip("Field-project Revit V5 load-readiness evidence is absent in this checkout (gitignored workspace/revit-school).")
    preview = json.loads((EVIDENCE / required[0]).read_text(encoding="utf-8"))
    handoff = json.loads((EVIDENCE / required[1]).read_text(encoding="utf-8"))
    inventory = {
        "lighting_fixtures": json.loads((EVIDENCE / required[2]).read_text(encoding="utf-8")),
        "electrical_fixtures": json.loads((EVIDENCE / required[3]).read_text(encoding="utf-8")),
        "electrical_equipment": json.loads((EVIDENCE / required[4]).read_text(encoding="utf-8")),
    }
    return preview, handoff, inventory


def test_live_v5_totals_are_reconciled_but_not_circuit_values(evidence: tuple[dict, dict, dict]) -> None:
    result = build_readiness(*evidence)
    assert result["totals"] == {
        "rooms": 108,
        "lighting_symbols": 947,
        "preliminary_connected_lighting_w": 25730.0,
        "duplex_candidates": 426,
        "gfi_candidates": 21,
        "dedicated_candidates": 78,
    }
    assert sum(zone["lighting_symbols"] for zone in result["lighting_zone_candidates"].values()) == 947
    assert sum(zone["duplex_candidates"] for zone in result["power_zone_candidates"].values()) == 426
    assert result["existing_physical_revit_category_counts"] == {
        "lighting_fixtures": 53, "electrical_fixtures": 16, "electrical_equipment": 0,
    }
    assert all(room["circuit_id"] is None and room["lighting_va"] is None for room in result["rooms"])
    assert "no new Revit circuits" in render_markdown(result)


def test_rejects_modified_preview(evidence: tuple[dict, dict, dict]) -> None:
    preview, handoff, inventory = copy.deepcopy(evidence)
    preview["rooms"][0]["lighting_w"] = 999.0
    with pytest.raises(SchoolV5ReadinessError, match="identity"):
        build_readiness(preview, handoff, inventory)


def test_rejects_missing_saved_handoff(evidence: tuple[dict, dict, dict]) -> None:
    preview, handoff, inventory = copy.deepcopy(evidence)
    handoff["saved_as_new_file"] = False
    with pytest.raises(SchoolV5ReadinessError, match="Save As"):
        build_readiness(preview, handoff, inventory)


def test_rejects_handoff_quantity_mismatch(evidence: tuple[dict, dict, dict]) -> None:
    preview, handoff, inventory = copy.deepcopy(evidence)
    handoff["lighting_point_count"] = 946
    with pytest.raises(SchoolV5ReadinessError, match="totals"):
        build_readiness(preview, handoff, inventory)


def test_rejects_inventory_from_wrong_model(evidence: tuple[dict, dict, dict]) -> None:
    preview, handoff, inventory = copy.deepcopy(evidence)
    inventory["lighting_fixtures"]["document_title"] = "other.rvt"
    with pytest.raises(SchoolV5ReadinessError, match="inventory"):
        build_readiness(preview, handoff, inventory)


def test_output_is_new_atomic_and_workspace_bounded() -> None:
    with tempfile.TemporaryDirectory(dir=ROOT / "workspace") as folder:
        test_root = Path(folder).resolve(strict=True)
        assert test_root.is_relative_to((ROOT / "workspace").resolve(strict=True))
        workspace = test_root / "workspace"
        workspace.mkdir()
        target = _workspace_path(workspace / "report.json", workspace.resolve(), must_exist=False)
        _write_new(target, '{"ok": true}\n')
        assert target.read_text(encoding="utf-8") == '{"ok": true}\n'
        with pytest.raises(SchoolV5ReadinessError, match="unsafe state"):
            _workspace_path(target, workspace.resolve(), must_exist=False)
        with pytest.raises(SchoolV5ReadinessError, match="outside the workspace"):
            _workspace_path(test_root / "escape.json", workspace.resolve(), must_exist=False)
