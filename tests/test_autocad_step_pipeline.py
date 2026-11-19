"""Regression tests for non-interactive, hash-bound AutoCAD STEP import."""

import hashlib
from pathlib import Path

import pytest

from agent.exceptions import AutoCADIntegrationError
from core.verified_lessons import OperationalIncidentJournal
from integrations.autocad_step_pipeline import (
    StepImportPlan,
    build_core_console_script,
    verify_core_console_import,
    verify_hybrid_postcheck,
)


def _plan(tmp_path: Path) -> StepImportPlan:
    source = tmp_path / "RE22R1MYMR.stp"
    source.write_text("ISO-10303-21;\nEND-ISO-10303-21;\n", encoding="ascii")
    digest = hashlib.sha256(source.read_bytes()).hexdigest()
    return StepImportPlan(source_step=source, source_sha256=digest, output_dwg=tmp_path / "out.dwg")


def test_script_is_non_interactive_hash_bound_and_uses_import_command(tmp_path: Path) -> None:
    plan = _plan(tmp_path)
    script = build_core_console_script(plan)
    assert "_.FILEDIA\n0\n_.IMPORT" in script
    assert "ActiveX" not in script
    plan.source_step.write_text("changed", encoding="ascii")
    with pytest.raises(AutoCADIntegrationError, match="changed"):
        build_core_console_script(plan)
    with pytest.raises(AutoCADIntegrationError, match="differs"):
        StepImportPlan(
            source_step=plan.source_step,
            source_sha256=plan.source_sha256,
            output_dwg=plan.output_dwg,
        )


def test_console_and_hybrid_acceptance_fail_closed(tmp_path: Path) -> None:
    plan = _plan(tmp_path)
    plan.output_dwg.write_bytes(b"DWG")
    assert len(verify_core_console_import(plan=plan, console_output="Import Job Complete")) == 64
    with pytest.raises(AutoCADIntegrationError, match="clean"):
        verify_core_console_import(
            plan=plan,
            console_output="Function cancelled",
            incidents=OperationalIncidentJournal(tmp_path / "candidates.json"),
            observed_at="2026-08-24T16:00:00Z",
        )
    assert OperationalIncidentJournal(tmp_path / "candidates.json").load()["candidates"][0][
        "promotion_status"
    ] == "candidate_only"
    verify_hybrid_postcheck(exact_instance_count=2, expected_instance_count=2, dbmod=0, source_unchanged=True)
    with pytest.raises(AutoCADIntegrationError, match="instance count"):
        verify_hybrid_postcheck(exact_instance_count=1, expected_instance_count=2, dbmod=0, source_unchanged=True)
