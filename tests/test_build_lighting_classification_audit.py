"""Regression tests for the lighting read-only classification audit."""

import copy
import hashlib
import json
from pathlib import Path

import pytest

from integrations.build_lighting_classification_audit import build_audit, render_markdown


EVIDENCE = Path("workspace/reports/lighting-panel-schedule-candidate-evidence-20260824.json")

pytestmark = pytest.mark.skipif(
    not EVIDENCE.is_file(),
    reason="Field-project AutoCAD lighting evidence JSON is absent in this checkout (gitignored workspace/reports).",
)


def _evidence():
    return json.loads(EVIDENCE.read_text(encoding="utf-8"))


def test_exact_lighting_audit_preserves_candidate_and_unknown_boundaries():
    report = build_audit(_evidence())
    candidate = report["candidate_classification"]

    assert report["accepted_inventory"]["total_entities"] == 4848
    assert report["accepted_inventory"]["reviewed_routes"] == 7
    assert report["accepted_inventory"]["route_candidates"] == 647
    assert report["accepted_inventory"]["unclassified_entities"] == 3856
    assert candidate["circuit_reference_count"] == 45
    assert candidate["conflicting_circuit_count"] == 7
    assert candidate["reported_unresolved_load_count"] == 35
    assert candidate["candidate_route_occurrence_total"] == 251
    assert report["safety"]["spatial_membership_promoted_to_connectivity"] is False
    assert report["interpretation"]["candidate_route_occurrence_total_is_not_a_unique_route_count"] is True


def test_audit_records_panel_schedule_and_sld_deferral_and_is_deterministic():
    first = build_audit(_evidence())
    second = build_audit(_evidence())

    assert first == second
    unhashed = dict(first)
    claimed = unhashed.pop("result_sha256")
    actual = hashlib.sha256(
        json.dumps(unhashed, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
    ).hexdigest()
    assert claimed == actual
    assert first["engineer_scope"]["panel_schedule_review_deferred_to_clearer_drawing"] is True
    assert first["engineer_scope"]["sld_review_deferred_to_clearer_drawing"] is True
    assert first["safety"]["drawing_mutation_performed"] is False
    assert "deferred to another drawing" in render_markdown(first)


def test_tampered_candidate_evidence_fails_closed():
    evidence = copy.deepcopy(_evidence())
    evidence["circuits"][0]["panel"] = "TAMPERED"

    with pytest.raises(ValueError, match="SHA-256"):
        build_audit(evidence)
