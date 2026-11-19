"""Hash-bound, workspace-confined handoff of verified AutoCAD outputs."""

from __future__ import annotations

import hashlib
import json
import logging
import os
import re
import tempfile
import uuid
from dataclasses import asdict, dataclass
from datetime import UTC, datetime
from pathlib import Path
from typing import Callable

from agent.exceptions import (
    ConfirmationRequiredError,
    DrawingHandoffError,
)
from core.file_manager import FileManager


_RUN_ID_PATTERN = re.compile(r"[0-9a-f]{32}")
_SHA256_PATTERN = re.compile(r"[0-9a-f]{64}")
_SUPPORTED_SUFFIXES = frozenset({".dwg", ".dwt"})
_PRODUCER_OPERATIONS = frozenset(
    {
        "autocad.create_line_scratch",
        "autocad.create_wire_route_scratch",
        "autocad.place_electrical_symbol_scratch",
        "autocad.create_electrical_circuit_fragment_scratch",
        "autocad.create_panel_sld_fixture_scratch",
        "autocad.create_panel_sld_draft_scratch",
        "autocad.create_db3_ga2_right_group_review_scratch",
    }
)
_EVIDENCE_NAME = "drawing-output.json"
_EVIDENCE_KEYS = frozenset(
    {
        "schema_version",
        "run_id",
        "operation",
        "source",
        "source_sha256",
        "output",
        "output_sha256",
        "output_size",
        "suffix",
    }
)


@dataclass(frozen=True, slots=True)
class DrawingOutputEvidence:
    """Immutable evidence identifying one exact producer output."""

    schema_version: int
    run_id: str
    operation: str
    source: Path
    source_sha256: str
    output: Path
    output_sha256: str
    output_size: int
    suffix: str

    def to_dict(self) -> dict[str, object]:
        value = asdict(self)
        value["source"] = str(self.source)
        value["output"] = str(self.output)
        return value


@dataclass(frozen=True, slots=True)
class DrawingHandoffResult:
    """Describe one verified new-file or replacement handoff."""

    run_id: str
    operation: str
    destination: Path
    output_sha256: str
    output_size: int
    replaced: bool
    backup_path: Path | None

    def to_dict(self) -> dict[str, object]:
        value = asdict(self)
        value["destination"] = str(self.destination)
        value["backup_path"] = str(self.backup_path) if self.backup_path else None
        return value


def persist_drawing_output_evidence(
    files: FileManager,
    *,
    run_id: str,
    operation: str,
    source: Path,
    source_sha256: str,
    output: Path,
    output_sha256: str,
    maximum_bytes: int,
) -> DrawingOutputEvidence:
    """Atomically persist evidence for an exact output in its producer run directory."""
    _validate_run_id(run_id)
    _validate_digest(source_sha256, "source_sha256")
    _validate_digest(output_sha256, "output_sha256")
    if operation not in _PRODUCER_OPERATIONS:
        raise DrawingHandoffError("Drawing producer operation is invalid.")
    source_path = files.resolve_path(source)
    output_path = files.resolve_path(output)
    expected_run_dir = output_path.parent
    if expected_run_dir.name != run_id or expected_run_dir.parent.name != "runs":
        raise DrawingHandoffError("Drawing output is not inside its producer run directory.")
    suffix = output_path.suffix.casefold()
    if suffix not in _SUPPORTED_SUFFIXES or output_path.name != f"source{suffix}":
        raise DrawingHandoffError("Drawing output name or suffix is not handoff-safe.")
    size, current_digest = _bounded_file_identity(output_path, maximum_bytes)
    if current_digest != output_sha256:
        raise DrawingHandoffError("Drawing output changed before evidence was recorded.")
    if _bounded_file_identity(source_path, maximum_bytes)[1] != source_sha256:
        raise DrawingHandoffError("Drawing source changed before evidence was recorded.")
    evidence = DrawingOutputEvidence(
        1,
        run_id,
        operation,
        source_path,
        source_sha256,
        output_path,
        output_sha256,
        size,
        suffix,
    )
    files.write_text(
        expected_run_dir / _EVIDENCE_NAME,
        json.dumps(evidence.to_dict(), indent=2, sort_keys=True) + "\n",
    )
    return evidence


def handoff_verified_output(
    files: FileManager,
    backup_directory: Path,
    *,
    run_id: str,
    output_sha256: str,
    destination: str | Path,
    replace_existing: bool,
    expected_destination_sha256: str | None = None,
    confirmed: bool = False,
    maximum_bytes: int = 268_435_456,
    clock: Callable[[], datetime] | None = None,
    logger: logging.Logger | None = None,
) -> DrawingHandoffResult:
    """Copy one evidence-bound output to a safe destination with exact verification."""
    if not confirmed:
        raise ConfirmationRequiredError(
            "Verified drawing handoff requires explicit durable approval."
        )
    _validate_run_id(run_id)
    _validate_digest(output_sha256, "output_sha256")
    evidence = _load_evidence(files, run_id, maximum_bytes)
    if evidence.output_sha256 != output_sha256:
        raise DrawingHandoffError("Approved output digest does not match run evidence.")
    target = files.resolve_path(destination)
    if target.suffix.casefold() != evidence.suffix:
        raise DrawingHandoffError("Destination suffix must match the verified output.")
    if ".ai-engineer" in {part.casefold() for part in target.parts}:
        raise DrawingHandoffError("Destination cannot be inside internal run storage.")
    if target == evidence.source or target == evidence.output:
        raise DrawingHandoffError("Producer source and scratch output cannot be destinations.")
    if not target.parent.is_dir():
        raise DrawingHandoffError("Drawing handoff destination directory must already exist.")
    if files.resolve_path(target.parent) != target.parent.resolve(strict=True):
        raise DrawingHandoffError("Drawing destination directory changed during validation.")
    source_size, source_digest = _bounded_file_identity(evidence.output, maximum_bytes)
    if source_size != evidence.output_size or source_digest != evidence.output_sha256:
        raise DrawingHandoffError("Producer output no longer matches its evidence.")

    active_logger = logger or logging.getLogger("ai_engineer.drawing_handoff")
    resolved_backup = backup_directory.expanduser().resolve(strict=False)
    if any(
        _is_within(resolved_backup, root) or _is_within(root, resolved_backup)
        for root in files.workspace_roots
    ):
        raise DrawingHandoffError(
            "Drawing handoff backups must be outside configured workspaces."
        )
    backup_path: Path | None = None
    if replace_existing:
        if expected_destination_sha256 is None:
            raise DrawingHandoffError("Replacement requires the current destination digest.")
        _validate_digest(expected_destination_sha256, "expected_destination_sha256")
        backup_path = _replace_existing(
            target,
            evidence,
            expected_destination_sha256,
            resolved_backup,
            maximum_bytes,
            clock or (lambda: datetime.now(UTC)),
        )
    else:
        if expected_destination_sha256 is not None:
            raise DrawingHandoffError("New-file handoff does not accept a destination digest.")
        _create_new(target, evidence, maximum_bytes)

    active_logger.info(
        "autocad_drawing_handed_off",
        extra={
            "run_id": run_id,
            "operation": evidence.operation,
            "destination": str(target),
            "replaced": replace_existing,
            "output_sha256": evidence.output_sha256,
            "backup_path": str(backup_path) if backup_path else None,
        },
    )
    return DrawingHandoffResult(
        run_id,
        evidence.operation,
        target,
        evidence.output_sha256,
        evidence.output_size,
        replace_existing,
        backup_path,
    )


def _load_evidence(
    files: FileManager, run_id: str, maximum_bytes: int
) -> DrawingOutputEvidence:
    matches: list[Path] = []
    for root in files.workspace_roots:
        candidate = files.resolve_path(
            root / ".ai-engineer" / "autocad" / "runs" / run_id / _EVIDENCE_NAME
        )
        if candidate.is_file():
            matches.append(candidate)
    if len(matches) != 1:
        raise DrawingHandoffError("Exactly one drawing-output evidence record is required.")
    try:
        raw = json.loads(files.read_text(matches[0]))
    except (OSError, ValueError, TypeError) as exc:
        raise DrawingHandoffError("Drawing-output evidence is not valid JSON.") from exc
    if not isinstance(raw, dict) or set(raw) != _EVIDENCE_KEYS:
        raise DrawingHandoffError("Drawing-output evidence has an invalid schema.")
    if raw["schema_version"] != 1 or raw["run_id"] != run_id:
        raise DrawingHandoffError("Drawing-output evidence identity is invalid.")
    if raw["operation"] not in _PRODUCER_OPERATIONS:
        raise DrawingHandoffError("Drawing-output evidence operation is invalid.")
    if not isinstance(raw["output_size"], int) or isinstance(raw["output_size"], bool):
        raise DrawingHandoffError("Drawing-output evidence size is invalid.")
    for key in ("source", "output", "source_sha256", "output_sha256", "suffix"):
        if not isinstance(raw[key], str):
            raise DrawingHandoffError(f"Drawing-output evidence {key} is invalid.")
    _validate_digest(raw["source_sha256"], "source_sha256")
    _validate_digest(raw["output_sha256"], "output_sha256")
    source = files.resolve_path(raw["source"])
    output = files.resolve_path(raw["output"])
    run_directory = matches[0].parent.resolve(strict=True)
    suffix = raw["suffix"].casefold()
    if (
        output.parent.resolve(strict=True) != run_directory
        or output.name != f"source{suffix}"
        or suffix not in _SUPPORTED_SUFFIXES
        or output.suffix.casefold() != suffix
    ):
        raise DrawingHandoffError("Drawing-output evidence path is invalid.")
    evidence = DrawingOutputEvidence(
        1,
        run_id,
        raw["operation"],
        source,
        raw["source_sha256"],
        output,
        raw["output_sha256"],
        raw["output_size"],
        suffix,
    )
    size, digest = _bounded_file_identity(output, maximum_bytes)
    if size != evidence.output_size or digest != evidence.output_sha256:
        raise DrawingHandoffError("Drawing output does not match persisted evidence.")
    if _bounded_file_identity(source, maximum_bytes)[1] != evidence.source_sha256:
        raise DrawingHandoffError("Drawing producer source no longer matches evidence.")
    return evidence


def _create_new(target: Path, evidence: DrawingOutputEvidence, maximum_bytes: int) -> None:
    if target.exists():
        raise DrawingHandoffError("New-file handoff refuses an existing destination.")
    staged = _stage_copy(evidence.output, target, maximum_bytes)
    try:
        os.link(staged, target)
        size, digest = _bounded_file_identity(target, maximum_bytes)
        if size != evidence.output_size or digest != evidence.output_sha256:
            target.unlink(missing_ok=True)
            raise DrawingHandoffError("New drawing post-write verification failed.")
    except FileExistsError as exc:
        raise DrawingHandoffError("Destination was created concurrently; no overwrite occurred.") from exc
    except OSError as exc:
        raise DrawingHandoffError(f"Cannot atomically create drawing destination: {exc}") from exc
    finally:
        staged.unlink(missing_ok=True)


def _replace_existing(
    target: Path,
    evidence: DrawingOutputEvidence,
    expected_digest: str,
    backup_directory: Path,
    maximum_bytes: int,
    clock: Callable[[], datetime],
) -> Path:
    if not target.is_file():
        raise DrawingHandoffError("Replacement destination must be an existing regular file.")
    if _bounded_file_identity(target, maximum_bytes)[1] != expected_digest:
        raise DrawingHandoffError("Destination changed since replacement approval.")
    backup_root = backup_directory
    backup_root.mkdir(parents=True, exist_ok=True)
    stamp = clock().astimezone(UTC).strftime("%Y%m%dT%H%M%S.%fZ")
    backup = backup_root / f"{stamp}-{uuid.uuid4().hex}-{target.name}"
    staged_backup = _stage_copy(target, backup, maximum_bytes)
    os.replace(staged_backup, backup)
    if _bounded_file_identity(backup, maximum_bytes)[1] != expected_digest:
        raise DrawingHandoffError("Replacement backup verification failed.")
    staged = _stage_copy(evidence.output, target, maximum_bytes)
    try:
        if _bounded_file_identity(target, maximum_bytes)[1] != expected_digest:
            raise DrawingHandoffError("Destination changed immediately before replacement.")
        os.replace(staged, target)
        size, digest = _bounded_file_identity(target, maximum_bytes)
        if size != evidence.output_size or digest != evidence.output_sha256:
            raise DrawingHandoffError("Replacement verification failed; restoring backup.")
    except Exception as exc:
        staged.unlink(missing_ok=True)
        restore = _stage_copy(backup, target, maximum_bytes)
        os.replace(restore, target)
        if _bounded_file_identity(target, maximum_bytes)[1] != expected_digest:
            raise DrawingHandoffError("Replacement failed and automatic rollback verification failed.") from exc
        if isinstance(exc, DrawingHandoffError):
            raise
        raise DrawingHandoffError(f"Drawing replacement failed and was rolled back: {exc}") from exc
    return backup


def _stage_copy(source: Path, target: Path, maximum_bytes: int) -> Path:
    descriptor, name = tempfile.mkstemp(
        dir=target.parent, prefix=f".{target.name}.", suffix=".handoff"
    )
    staged = Path(name)
    try:
        with os.fdopen(descriptor, "wb") as writer:
            with source.open("rb") as reader:
                copied = 0
                while chunk := reader.read(1024 * 1024):
                    copied += len(chunk)
                    if copied > maximum_bytes:
                        raise DrawingHandoffError(
                            "Drawing exceeds the handoff byte limit."
                        )
                    writer.write(chunk)
                writer.flush()
                os.fsync(writer.fileno())
        return staged
    except Exception:
        staged.unlink(missing_ok=True)
        raise


def _bounded_file_identity(path: Path, maximum_bytes: int) -> tuple[int, str]:
    try:
        if not path.is_file():
            raise DrawingHandoffError(f"Drawing is not an existing regular file: {path}")
        size = path.stat().st_size
        if size <= 0 or size > maximum_bytes:
            raise DrawingHandoffError("Drawing is empty or exceeds the handoff byte limit.")
        digest = hashlib.sha256()
        read = 0
        with path.open("rb") as handle:
            while chunk := handle.read(1024 * 1024):
                read += len(chunk)
                if read > maximum_bytes:
                    raise DrawingHandoffError("Drawing changed beyond the handoff byte limit.")
                digest.update(chunk)
        if read != size:
            raise DrawingHandoffError("Drawing changed while its digest was calculated.")
        return size, digest.hexdigest()
    except DrawingHandoffError:
        raise
    except OSError as exc:
        raise DrawingHandoffError(f"Cannot inspect drawing bytes: {exc}") from exc


def _validate_run_id(value: str) -> None:
    if not isinstance(value, str) or _RUN_ID_PATTERN.fullmatch(value) is None:
        raise DrawingHandoffError("run_id must be 32 lowercase hexadecimal characters.")


def _validate_digest(value: str, name: str) -> None:
    if not isinstance(value, str) or _SHA256_PATTERN.fullmatch(value) is None:
        raise DrawingHandoffError(f"{name} must be a lowercase SHA-256 digest.")


def _is_within(candidate: Path, root: Path) -> bool:
    try:
        candidate.relative_to(root)
        return True
    except ValueError:
        return False
