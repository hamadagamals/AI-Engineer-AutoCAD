"""Reviewed operational lessons that prevent known failures without self-modification."""

from __future__ import annotations

import hashlib
import json
import os
import re
import tempfile
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any, Iterable, Mapping

from agent.exceptions import VerifiedLessonError


LESSON_VERSION = "safe_learning.lesson.v1"
REGISTRY_VERSION = "safe_learning.registry.v1"
_IDENTIFIER = re.compile(r"[a-z][a-z0-9_.:-]{0,127}")
_SHA256 = re.compile(r"[0-9a-f]{64}")


@dataclass(frozen=True, slots=True)
class VerifiedLesson:
    """One reviewed problem/solution invariant with immutable evidence."""

    schema_version: int
    lesson_version: str
    lesson_id: str
    scope: str
    trigger_codes: tuple[str, ...]
    problem: str
    root_cause: str
    required_actions: tuple[str, ...]
    forbidden_actions: tuple[str, ...]
    verification_requirements: tuple[str, ...]
    evidence_sha256: tuple[str, ...]
    reviewed_by: str
    reviewed_on: str
    active: bool
    lesson_sha256: str

    def __post_init__(self) -> None:
        if self.schema_version != 1 or self.lesson_version != LESSON_VERSION:
            raise VerifiedLessonError("Unsupported verified-lesson version.")
        _identifier(self.lesson_id, "lesson ID")
        _identifier(self.scope, "lesson scope")
        _identifiers(self.trigger_codes, "lesson trigger codes", 1, 16)
        _texts((self.problem,), "lesson problem", 1, 1, 1024)
        _texts((self.root_cause,), "lesson root cause", 1, 1, 1024)
        _texts(self.required_actions, "lesson required actions", 1, 16, 512)
        _identifiers(self.forbidden_actions, "lesson forbidden actions", 1, 16)
        _texts(
            self.verification_requirements,
            "lesson verification requirements",
            1,
            16,
            512,
        )
        if not isinstance(self.evidence_sha256, tuple) or not self.evidence_sha256:
            raise VerifiedLessonError("Verified lesson requires immutable evidence hashes.")
        for digest in self.evidence_sha256:
            if not isinstance(digest, str) or _SHA256.fullmatch(digest) is None:
                raise VerifiedLessonError("Verified lesson evidence SHA-256 is invalid.")
        if len(self.evidence_sha256) != len(set(self.evidence_sha256)):
            raise VerifiedLessonError("Verified lesson evidence hashes must be unique.")
        _texts((self.reviewed_by,), "lesson reviewer", 1, 1, 128)
        if re.fullmatch(r"\d{4}-\d{2}-\d{2}", self.reviewed_on) is None:
            raise VerifiedLessonError("Verified lesson review date must use YYYY-MM-DD.")
        if self.active is not True:
            raise VerifiedLessonError("Only active reviewed lessons belong in the active registry.")
        expected = _digest(_lesson_payload(self))
        if self.lesson_sha256 != expected:
            raise VerifiedLessonError("Verified lesson SHA-256 does not match its content.")


@dataclass(frozen=True, slots=True)
class LessonRegistrySnapshot:
    """Immutable, self-hashed collection of active reviewed lessons."""

    schema_version: int
    registry_version: str
    lessons: tuple[VerifiedLesson, ...]
    registry_sha256: str

    def __post_init__(self) -> None:
        if self.schema_version != 1 or self.registry_version != REGISTRY_VERSION:
            raise VerifiedLessonError("Unsupported verified-lesson registry version.")
        if not isinstance(self.lessons, tuple):
            raise VerifiedLessonError("Verified-lesson registry lessons must be a tuple.")
        identifiers = [lesson.lesson_id for lesson in self.lessons]
        if len(identifiers) != len(set(identifiers)):
            raise VerifiedLessonError("Verified-lesson IDs must be unique.")
        expected = _digest(
            {
                "schema_version": self.schema_version,
                "registry_version": self.registry_version,
                "lessons": [asdict(lesson) for lesson in self.lessons],
            }
        )
        if self.registry_sha256 != expected:
            raise VerifiedLessonError("Verified-lesson registry SHA-256 does not match its content.")

    def to_dict(self) -> dict[str, Any]:
        return {
            "schema_version": self.schema_version,
            "registry_version": self.registry_version,
            "lessons": [asdict(lesson) for lesson in self.lessons],
            "registry_sha256": self.registry_sha256,
        }


class VerifiedLessonRegistry:
    """Load and append explicitly reviewed lessons using atomic persistence."""

    def __init__(self, path: Path) -> None:
        self.path = path

    def load(self) -> LessonRegistrySnapshot:
        if not self.path.exists():
            return create_registry_snapshot(())
        try:
            value = json.loads(self.path.read_text(encoding="utf-8"))
        except (OSError, UnicodeError, json.JSONDecodeError) as exc:
            raise VerifiedLessonError(f"Cannot read verified-lesson registry: {exc}") from exc
        return parse_registry_snapshot(value)

    def lookup(self, *, scope: str, trigger_code: str) -> tuple[VerifiedLesson, ...]:
        _identifier(scope, "lesson lookup scope")
        _identifier(trigger_code, "lesson lookup trigger")
        return tuple(
            lesson
            for lesson in self.load().lessons
            if lesson.scope == scope and trigger_code in lesson.trigger_codes
        )

    def enforce_allowed_action(self, *, scope: str, action_code: str) -> None:
        """Fail closed when an active reviewed lesson forbids an action."""
        _identifier(scope, "lesson enforcement scope")
        _identifier(action_code, "lesson action code")
        blockers = [
            lesson.lesson_id
            for lesson in self.load().lessons
            if lesson.scope == scope and action_code in lesson.forbidden_actions
        ]
        if blockers:
            raise VerifiedLessonError(
                f"Action {action_code!r} is forbidden by reviewed lessons: {', '.join(blockers)}."
            )

    def register_reviewed(self, lesson: VerifiedLesson) -> LessonRegistrySnapshot:
        """Append one pre-hashed reviewed lesson; never overwrite or auto-promote."""
        if not isinstance(lesson, VerifiedLesson):
            raise VerifiedLessonError("Only a typed reviewed lesson can be registered.")
        current = self.load()
        if any(item.lesson_id == lesson.lesson_id for item in current.lessons):
            raise VerifiedLessonError("Verified lesson identity cannot be overwritten.")
        snapshot = create_registry_snapshot((*current.lessons, lesson))
        _atomic_write(self.path, snapshot.to_dict())
        return snapshot

    def write_or_verify_snapshot(
        self, snapshot: LessonRegistrySnapshot
    ) -> LessonRegistrySnapshot:
        """Create a deterministic reviewed seed, never replacing different bytes."""
        if not isinstance(snapshot, LessonRegistrySnapshot):
            raise VerifiedLessonError("A typed verified-lesson snapshot is required.")
        if self.path.exists():
            current = self.load()
            if current.registry_sha256 != snapshot.registry_sha256:
                raise VerifiedLessonError(
                    "Existing verified-lesson registry differs and was not overwritten."
                )
            return current
        _atomic_write(self.path, snapshot.to_dict())
        return snapshot


class OperationalIncidentJournal:
    """Automatically retain bounded failure candidates without promoting them."""

    def __init__(self, path: Path) -> None:
        self.path = path

    def load(self) -> dict[str, Any]:
        if not self.path.exists():
            return {"schema_version": 1, "candidates": []}
        try:
            value = json.loads(self.path.read_text(encoding="utf-8"))
        except (OSError, UnicodeError, json.JSONDecodeError) as exc:
            raise VerifiedLessonError(f"Cannot read lesson-candidate journal: {exc}") from exc
        if not isinstance(value, dict) or set(value) != {"schema_version", "candidates"}:
            raise VerifiedLessonError("Lesson-candidate journal fields are invalid.")
        if value["schema_version"] != 1 or not isinstance(value["candidates"], list):
            raise VerifiedLessonError("Lesson-candidate journal version or candidates are invalid.")
        return value

    def record_failure(
        self,
        *,
        scope: str,
        trigger_code: str,
        attempted_action: str,
        summary: str,
        observed_at: str,
        evidence_sha256: tuple[str, ...] = (),
    ) -> dict[str, Any]:
        """Record/increment one candidate; never add it to the active lesson registry."""
        _identifier(scope, "incident scope")
        _identifier(trigger_code, "incident trigger")
        _identifier(attempted_action, "incident attempted action")
        _texts((summary,), "incident summary", 1, 1, 1024)
        if re.fullmatch(r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z", observed_at) is None:
            raise VerifiedLessonError("Incident timestamp must use UTC YYYY-MM-DDTHH:MM:SSZ.")
        for digest in evidence_sha256:
            if _SHA256.fullmatch(digest) is None:
                raise VerifiedLessonError("Incident evidence SHA-256 is invalid.")
        identity = _digest(
            {"scope": scope, "trigger_code": trigger_code, "attempted_action": attempted_action}
        )
        value = self.load()
        candidates = value["candidates"]
        existing = next((item for item in candidates if item.get("incident_id") == identity), None)
        if existing is None:
            existing = {
                "incident_id": identity,
                "scope": scope,
                "trigger_code": trigger_code,
                "attempted_action": attempted_action,
                "summary": summary,
                "occurrence_count": 0,
                "first_observed_at": observed_at,
                "last_observed_at": observed_at,
                "evidence_sha256": [],
                "promotion_status": "candidate_only",
            }
            candidates.append(existing)
        existing["occurrence_count"] += 1
        existing["last_observed_at"] = observed_at
        existing["summary"] = summary
        existing["evidence_sha256"] = sorted(
            set(existing["evidence_sha256"]).union(evidence_sha256)
        )
        candidates.sort(key=lambda item: item["incident_id"])
        _atomic_write(self.path, value)
        return dict(existing)

def create_verified_lesson(
    *,
    lesson_id: str,
    scope: str,
    trigger_codes: tuple[str, ...],
    problem: str,
    root_cause: str,
    required_actions: tuple[str, ...],
    forbidden_actions: tuple[str, ...],
    verification_requirements: tuple[str, ...],
    evidence_sha256: tuple[str, ...],
    reviewed_by: str,
    reviewed_on: str,
) -> VerifiedLesson:
    payload: dict[str, Any] = {
        "schema_version": 1,
        "lesson_version": LESSON_VERSION,
        "lesson_id": lesson_id,
        "scope": scope,
        "trigger_codes": trigger_codes,
        "problem": problem,
        "root_cause": root_cause,
        "required_actions": required_actions,
        "forbidden_actions": forbidden_actions,
        "verification_requirements": verification_requirements,
        "evidence_sha256": evidence_sha256,
        "reviewed_by": reviewed_by,
        "reviewed_on": reviewed_on,
        "active": True,
    }
    return VerifiedLesson(**payload, lesson_sha256=_digest(payload))


def create_registry_snapshot(lessons: Iterable[VerifiedLesson]) -> LessonRegistrySnapshot:
    ordered = tuple(sorted(lessons, key=lambda item: item.lesson_id))
    digest_payload = {
        "schema_version": 1,
        "registry_version": REGISTRY_VERSION,
        "lessons": [asdict(lesson) for lesson in ordered],
    }
    return LessonRegistrySnapshot(
        schema_version=1,
        registry_version=REGISTRY_VERSION,
        lessons=ordered,
        registry_sha256=_digest(digest_payload),
    )


def parse_registry_snapshot(value: object) -> LessonRegistrySnapshot:
    if not isinstance(value, Mapping) or set(value) != {
        "schema_version",
        "registry_version",
        "lessons",
        "registry_sha256",
    }:
        raise VerifiedLessonError("Verified-lesson registry fields are invalid.")
    raw_lessons = value["lessons"]
    if not isinstance(raw_lessons, list):
        raise VerifiedLessonError("Verified-lesson registry lessons must be a JSON array.")
    lessons: list[VerifiedLesson] = []
    expected_fields = set(VerifiedLesson.__dataclass_fields__)
    for raw in raw_lessons:
        if not isinstance(raw, Mapping) or set(raw) != expected_fields:
            raise VerifiedLessonError("Verified-lesson fields are invalid.")
        converted = dict(raw)
        for name in (
            "trigger_codes",
            "required_actions",
            "forbidden_actions",
            "verification_requirements",
            "evidence_sha256",
        ):
            if not isinstance(converted[name], list):
                raise VerifiedLessonError(f"Verified-lesson {name} must be a JSON array.")
            converted[name] = tuple(converted[name])
        lessons.append(VerifiedLesson(**converted))
    return LessonRegistrySnapshot(
        schema_version=value["schema_version"],
        registry_version=value["registry_version"],
        lessons=tuple(lessons),
        registry_sha256=value["registry_sha256"],
    )


def _lesson_payload(lesson: VerifiedLesson) -> dict[str, Any]:
    payload = asdict(lesson)
    payload.pop("lesson_sha256")
    return payload


def _digest(value: Mapping[str, Any]) -> str:
    data = json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False)
    return hashlib.sha256(data.encode("utf-8")).hexdigest()


def _identifier(value: object, name: str) -> None:
    if not isinstance(value, str) or _IDENTIFIER.fullmatch(value) is None:
        raise VerifiedLessonError(f"Invalid {name}.")


def _identifiers(values: object, name: str, minimum: int, maximum: int) -> None:
    if not isinstance(values, tuple) or not minimum <= len(values) <= maximum:
        raise VerifiedLessonError(f"Invalid {name} count.")
    if len(values) != len(set(values)):
        raise VerifiedLessonError(f"{name.capitalize()} must be unique.")
    for value in values:
        _identifier(value, name)


def _texts(values: object, name: str, minimum: int, maximum: int, limit: int) -> None:
    if not isinstance(values, tuple) or not minimum <= len(values) <= maximum:
        raise VerifiedLessonError(f"Invalid {name} count.")
    for value in values:
        if not isinstance(value, str) or not value.strip() or len(value) > limit:
            raise VerifiedLessonError(f"Invalid {name} text.")


def _atomic_write(path: Path, value: Mapping[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, name = tempfile.mkstemp(dir=path.parent, prefix=f".{path.name}.", suffix=".tmp")
    temporary = Path(name)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as handle:
            json.dump(value, handle, indent=2, ensure_ascii=False)
            handle.write("\n")
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary, path)
    except OSError as exc:
        raise VerifiedLessonError(f"Cannot atomically write verified-lesson registry: {exc}") from exc
    finally:
        if temporary.exists():
            temporary.unlink()
