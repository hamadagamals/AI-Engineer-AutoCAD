"""Validated STEP-to-AutoCAD Core Console planning and acceptance checks."""

from __future__ import annotations

import hashlib
import re
from dataclasses import dataclass
from pathlib import Path

from agent.exceptions import AutoCADIntegrationError
from core.verified_lessons import OperationalIncidentJournal, VerifiedLessonRegistry


_SHA256 = re.compile(r"[0-9a-f]{64}")


@dataclass(frozen=True, slots=True)
class StepImportPlan:
    source_step: Path
    source_sha256: str
    output_dwg: Path
    timeout_seconds: int = 120

    def __post_init__(self) -> None:
        source = self.source_step.resolve()
        output = self.output_dwg.resolve()
        if source.suffix.lower() not in {".stp", ".step", ".ste"}:
            raise AutoCADIntegrationError("STEP import source must use .stp, .step, or .ste.")
        if output.suffix.lower() != ".dwg" or source == output:
            raise AutoCADIntegrationError("STEP output must be a distinct DWG path.")
        if _SHA256.fullmatch(self.source_sha256) is None:
            raise AutoCADIntegrationError("STEP source SHA-256 is invalid.")
        if not 10 <= self.timeout_seconds <= 600:
            raise AutoCADIntegrationError("STEP import timeout must be between 10 and 600 seconds.")
        if not source.is_file() or _hash(source) != self.source_sha256:
            raise AutoCADIntegrationError("STEP source is missing or differs from its approved SHA-256.")


def build_core_console_script(
    plan: StepImportPlan, *, lessons: VerifiedLessonRegistry | None = None
) -> str:
    """Build fixed command input; arbitrary AutoCAD command text is not accepted."""
    if lessons is not None:
        lessons.enforce_allowed_action(
            scope="autocad.cad_import", action_code="activex.step_import"
        )
        lessons.enforce_allowed_action(
            scope="autocad.cad_import", action_code="automation.interactive_import"
        )
    if not plan.source_step.is_file() or _hash(plan.source_step) != plan.source_sha256:
        raise AutoCADIntegrationError("STEP source changed after the reviewed plan was created.")
    source = _quoted_autocad_path(plan.source_step.resolve())
    output = _quoted_autocad_path(plan.output_dwg.resolve())
    return "\n".join(("_.FILEDIA", "0", "_.IMPORT", source, "_.SAVEAS", "2018", output, "_.QUIT", "Y", ""))


def verify_core_console_import(
    *,
    plan: StepImportPlan,
    console_output: str,
    lessons: VerifiedLessonRegistry | None = None,
    incidents: OperationalIncidentJournal | None = None,
    observed_at: str | None = None,
) -> str:
    """Require deterministic completion text and the expected non-empty output identity."""
    if lessons is not None:
        lessons.enforce_allowed_action(
            scope="autocad.cad_import", action_code="drawing.unverified_handoff"
        )
    if not plan.source_step.is_file() or _hash(plan.source_step) != plan.source_sha256:
        raise AutoCADIntegrationError("STEP source changed after the reviewed plan was created.")
    if not isinstance(console_output, str):
        raise AutoCADIntegrationError("Core Console output must be text.")
    lowered = console_output.lower()
    if "import job complete" not in lowered or any(
        marker in lowered for marker in ("fatal error", "unhandled exception", "function cancelled")
    ):
        if incidents is not None and observed_at is not None:
            incidents.record_failure(
                scope="autocad.cad_import",
                trigger_code="step.import_failed",
                attempted_action="core_console.step_import",
                summary="Core Console STEP import lacked a clean completion result.",
                observed_at=observed_at,
                evidence_sha256=(plan.source_sha256,),
            )
        raise AutoCADIntegrationError("STEP import did not produce a clean Import Job Complete result.")
    output = plan.output_dwg.resolve()
    if not output.is_file() or output.stat().st_size <= 0:
        raise AutoCADIntegrationError("Expected imported DWG was not created.")
    return _hash(output)


def verify_hybrid_postcheck(
    *, exact_instance_count: int, expected_instance_count: int, dbmod: int, source_unchanged: bool
) -> None:
    """Fail closed before a hybrid drawing is handed off to the engineer."""
    if expected_instance_count <= 0 or exact_instance_count != expected_instance_count:
        raise AutoCADIntegrationError("Exact STEP instance count does not match the reviewed plan.")
    if dbmod != 0:
        raise AutoCADIntegrationError("Read-only postcheck requires DBMOD=0.")
    if source_unchanged is not True:
        raise AutoCADIntegrationError("Source drawing identity was not preserved.")


def _quoted_autocad_path(path: Path) -> str:
    text = str(path)
    if any(character in text for character in ('"', "\r", "\n")):
        raise AutoCADIntegrationError("AutoCAD import path contains unsafe characters.")
    return f'"{text}"'


def _hash(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        while chunk := handle.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()
