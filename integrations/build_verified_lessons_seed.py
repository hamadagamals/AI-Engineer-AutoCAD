"""Build the reviewed operational lessons captured during the 2026-08-24 CAD trial."""

from __future__ import annotations

import argparse
from pathlib import Path

from core.verified_lessons import (
    VerifiedLessonRegistry,
    create_registry_snapshot,
    create_verified_lesson,
)


REVIEW_DATE = "2026-08-24"
REVIEWER = "engineer:gamal"
DXF_EVIDENCE = "a302e2b9120aca76f3f3dd91ef2faef4e782da3da1ab2b8c7717dc0f716c3198"
STEP_EVIDENCE = "a85a4c82c8f05e5c94cc732b534866e4e98cd67e8c39d504e5b763f49f001e65"
POSTCHECK_EVIDENCE = "a609268796f4bc6b2886efa558dc6a904fec9c3cdb8f82c8e959ec7aeb406531"


def build(project_root: Path) -> Path:
    """Write a deterministic, self-hashed active registry beneath *project_root*."""
    lessons = (
        create_verified_lesson(
            lesson_id="lesson:autocad-dxf-compatibility",
            scope="autocad.cad_import",
            trigger_codes=("dxf.open_failed",),
            problem="A handwritten DXF passed a library parse but AutoCAD 2021 rejected it.",
            root_cause="Syntactic parsing alone did not prove native AutoCAD compatibility.",
            required_actions=("Generate DXF through the pinned standards-aware writer.", "Require library audit and native AutoCAD open acceptance."),
            forbidden_actions=("dxf.handwritten_serialization",),
            verification_requirements=("Require non-empty audited entities and a DBMOD=0 native reopen.",),
            evidence_sha256=(DXF_EVIDENCE,), reviewed_by=REVIEWER, reviewed_on=REVIEW_DATE,
        ),
        create_verified_lesson(
            lesson_id="lesson:autocad-step-import-command",
            scope="autocad.cad_import",
            trigger_codes=("step.import_failed", "step.modal_timeout"),
            problem="ActiveX and interactive STEP import produced an unsupported call or blocking modal workflow.",
            root_cause="The ActiveX Import method does not support STEP and GUI automation is not deterministic.",
            required_actions=("Use FILEDIA=0 and the allowlisted IMPORT command through AutoCAD Core Console.",),
            forbidden_actions=("activex.step_import", "automation.interactive_import"),
            verification_requirements=("Require Import Job Complete, an output hash, and a read-only postcheck.",),
            evidence_sha256=(STEP_EVIDENCE, POSTCHECK_EVIDENCE), reviewed_by=REVIEWER, reviewed_on=REVIEW_DATE,
        ),
        create_verified_lesson(
            lesson_id="lesson:manufacturer-exact-cad-match",
            scope="manufacturer.cad_intake",
            trigger_codes=("cad.part_mismatch", "cad.duplicate"),
            problem="Similar Schneider references and duplicate downloads could be mistaken for approved BOM items.",
            root_cause="Filename similarity is not proof of exact commercial-reference identity.",
            required_actions=("Match normalized commercial references exactly and deduplicate by SHA-256.",),
            forbidden_actions=("cad.fuzzy_substitution",),
            verification_requirements=("Leave every non-exact asset unbound and report exact duplicates.",),
            evidence_sha256=(STEP_EVIDENCE,), reviewed_by=REVIEWER, reviewed_on=REVIEW_DATE,
        ),
        create_verified_lesson(
            lesson_id="lesson:autocad-artifact-identity",
            scope="autocad.cad_import",
            trigger_codes=("drawing.wrong_tab", "drawing.unverified_handoff"),
            problem="A similarly named prior tab could be mistaken for the newly produced 3D drawing.",
            root_cause="Visible filename similarity did not bind the review to exact artifact bytes.",
            required_actions=("Bind handoff to the full path and SHA-256 and verify the active document identity.",),
            forbidden_actions=("artifact.name_only_open", "drawing.unverified_handoff"),
            verification_requirements=("Require expected entity counts, exact STEP instances, DBMOD=0, and unchanged source hash.",),
            evidence_sha256=(POSTCHECK_EVIDENCE,), reviewed_by=REVIEWER, reviewed_on=REVIEW_DATE,
        ),
    )
    snapshot = create_registry_snapshot(lessons)
    output = project_root.resolve() / "data" / "verified_lessons.json"
    VerifiedLessonRegistry(output).write_or_verify_snapshot(snapshot)
    return output


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("project_root", nargs="?", type=Path, default=Path(__file__).resolve().parents[1])
    print(build(parser.parse_args().project_root))
