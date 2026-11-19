"""Tests for bounded reviewed operational learning."""

from dataclasses import replace

import pytest

from agent.exceptions import VerifiedLessonError
from core.verified_lessons import (
    OperationalIncidentJournal,
    VerifiedLessonRegistry,
    create_verified_lesson,
)
from integrations.build_verified_lessons_seed import build


def _lesson(identifier: str = "lesson:step-import"):
    return create_verified_lesson(
        lesson_id=identifier,
        scope="autocad.cad_import",
        trigger_codes=("step.import",),
        problem="A supported STEP file opened an interactive dialog during automation.",
        root_cause="The generic ActiveX import method does not support STEP.",
        required_actions=("Use the bounded Core Console IMPORT workflow.",),
        forbidden_actions=("activex.step_import",),
        verification_requirements=("Require Import Job Complete and a read-only postcheck.",),
        evidence_sha256=("a" * 64,),
        reviewed_by="engineer:gamal",
        reviewed_on="2026-08-24",
    )


def test_reviewed_lesson_is_atomically_registered_and_enforced(tmp_path):
    registry = VerifiedLessonRegistry(tmp_path / "lessons.json")
    snapshot = registry.register_reviewed(_lesson())

    assert len(snapshot.lessons) == 1
    assert registry.lookup(scope="autocad.cad_import", trigger_code="step.import") == (
        snapshot.lessons[0],
    )
    with pytest.raises(VerifiedLessonError, match="forbidden"):
        registry.enforce_allowed_action(
            scope="autocad.cad_import", action_code="activex.step_import"
        )


def test_tampering_duplicate_identity_and_unreviewed_state_fail_closed(tmp_path):
    registry = VerifiedLessonRegistry(tmp_path / "lessons.json")
    lesson = _lesson()
    registry.register_reviewed(lesson)
    with pytest.raises(VerifiedLessonError, match="cannot be overwritten"):
        registry.register_reviewed(lesson)
    with pytest.raises(VerifiedLessonError, match="SHA-256"):
        replace(lesson, problem="tampered")
    with pytest.raises(VerifiedLessonError, match="Only active"):
        replace(lesson, active=False, lesson_sha256="0" * 64)


def test_missing_registry_is_safe_empty_state(tmp_path):
    registry = VerifiedLessonRegistry(tmp_path / "missing.json")
    snapshot = registry.load()
    assert snapshot.lessons == ()


def test_reviewed_seed_is_reproducible_and_loadable(tmp_path):
    path = build(tmp_path)
    first = path.read_bytes()
    snapshot = VerifiedLessonRegistry(path).load()
    build(tmp_path)
    assert path.read_bytes() == first
    assert len(snapshot.lessons) == 4
    assert all(lesson.active for lesson in snapshot.lessons)


def test_incident_candidates_increment_but_never_auto_promote(tmp_path):
    journal = OperationalIncidentJournal(tmp_path / "candidates.json")
    arguments = {
        "scope": "autocad.cad_import",
        "trigger_code": "step.modal_timeout",
        "attempted_action": "automation.interactive_import",
        "summary": "Interactive import timed out.",
        "observed_at": "2026-08-24T16:00:00Z",
        "evidence_sha256": ("b" * 64,),
    }
    first = journal.record_failure(**arguments)
    second = journal.record_failure(**{**arguments, "observed_at": "2026-08-24T16:01:00Z"})
    assert first["promotion_status"] == "candidate_only"
    assert second["occurrence_count"] == 2
    assert VerifiedLessonRegistry(tmp_path / "lessons.json").load().lessons == ()
