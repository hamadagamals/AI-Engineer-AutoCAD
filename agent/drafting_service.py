"""Durable metadata-only lifecycle for validated electrical drafting proposals."""

from __future__ import annotations

import hashlib
import logging
import secrets
import threading
import uuid
from collections import deque
from dataclasses import dataclass, replace
from datetime import UTC, datetime, timedelta
from enum import Enum
from pathlib import Path
from typing import Any, Callable

from agent.exceptions import DraftingContractError, ResourceNotFoundError
from agent.state import StateStore
from core.electrical_drafting import DraftingPreview, DraftingProposal, JsonContract
from core.electrical_drafting_validation import DraftingValidationResult


class DraftingRunStatus(str, Enum):
    """Durable states for one reviewed preview and bounded execution."""

    VALIDATED = "validated"
    BLOCKED = "blocked"
    PREVIEW_READY = "preview_ready"
    REVIEW_PENDING = "review_pending"
    EXECUTION_REVIEW_PENDING = "execution_review_pending"
    APPROVED = "approved"
    EXECUTING = "executing"
    EXECUTED = "executed"
    RECOVERY_REQUIRED = "recovery_required"
    REJECTED = "rejected"
    CANCELLED = "cancelled"
    EXPIRED = "expired"


@dataclass(frozen=True, slots=True)
class DraftingAuditEvent(JsonContract):
    """Metadata-only lifecycle event; it contains no drawing or proposal values."""

    sequence: int
    event: str
    status: DraftingRunStatus
    recorded_at: str

    def __post_init__(self) -> None:
        if not isinstance(self.sequence, int) or isinstance(self.sequence, bool) or self.sequence < 1:
            raise DraftingContractError("Drafting audit sequence is invalid.")
        if not isinstance(self.event, str) or self.event not in {
            "created", "preview_created", "review_requested", "approved", "rejected",
            "execution_review_requested", "execution_started", "execution_completed",
            "execution_failed", "cancelled", "expired"
        }:
            raise DraftingContractError("Drafting audit event is invalid.")
        if not isinstance(self.status, DraftingRunStatus):
            raise DraftingContractError("Drafting audit status is invalid.")
        _parse_timestamp(self.recorded_at, "drafting audit timestamp")


@dataclass(frozen=True, slots=True)
class DraftingRun(JsonContract):
    """Public durable hash binding for one proposal review lifecycle."""

    schema_version: int
    run_id: str
    proposal_id: str
    proposal_sha256: str
    validation_sha256: str
    model_sha256: str
    symbol_catalog_sha256: str
    status: DraftingRunStatus
    created_at: str
    updated_at: str
    expires_at: str
    preview_id: str | None
    preview_sha256: str | None
    preview_expires_at: str | None
    review_id: str | None
    decided_at: str | None
    audit: tuple[DraftingAuditEvent, ...]
    drawing_mutation_performed: bool = False
    execution_request_sha256: str | None = None
    execution_id: str | None = None
    execution_started_at: str | None = None
    execution_completed_at: str | None = None
    autocad_run_id: str | None = None
    source_sha256: str | None = None
    output_sha256: str | None = None
    recovery_evidence_sha256: str | None = None

    def __post_init__(self) -> None:
        if self.schema_version != 1:
            raise DraftingContractError("Drafting run schema_version must be 1.")
        _hex(self.run_id, 32, "drafting run ID")
        _hex(self.proposal_id, 32, "drafting proposal ID")
        for value, name in (
            (self.proposal_sha256, "proposal SHA-256"),
            (self.validation_sha256, "validation SHA-256"),
            (self.model_sha256, "model SHA-256"),
            (self.symbol_catalog_sha256, "catalog SHA-256"),
        ):
            _hex(value, 64, name)
        if not isinstance(self.status, DraftingRunStatus):
            raise DraftingContractError("Drafting run status is invalid.")
        created = _parse_timestamp(self.created_at, "drafting created_at")
        updated = _parse_timestamp(self.updated_at, "drafting updated_at")
        expires = _parse_timestamp(self.expires_at, "drafting expires_at")
        if not created <= updated or (
            self.status is not DraftingRunStatus.EXPIRED and updated >= expires
        ):
            raise DraftingContractError("Drafting run timestamps are invalid.")
        if self.review_id is not None:
            _hex(self.review_id, 32, "drafting review ID")
        preview_values = (self.preview_id, self.preview_sha256, self.preview_expires_at)
        if any(value is not None for value in preview_values) != all(
            value is not None for value in preview_values
        ):
            raise DraftingContractError("Drafting preview binding is incomplete.")
        preview_statuses = {
            DraftingRunStatus.PREVIEW_READY,
            DraftingRunStatus.REVIEW_PENDING,
            DraftingRunStatus.EXECUTION_REVIEW_PENDING,
            DraftingRunStatus.APPROVED,
            DraftingRunStatus.EXECUTING,
            DraftingRunStatus.EXECUTED,
            DraftingRunStatus.RECOVERY_REQUIRED,
            DraftingRunStatus.REJECTED,
        }
        has_preview = all(value is not None for value in preview_values)
        if self.status in preview_statuses and not has_preview:
            raise DraftingContractError("Drafting run status requires a preview binding.")
        if has_preview:
            _hex(self.preview_id, 32, "drafting preview ID")
            _hex(self.preview_sha256, 64, "drafting preview SHA-256")
            preview_expires = _parse_timestamp(
                self.preview_expires_at, "drafting preview expires_at"
            )
            if not created < preview_expires <= expires:
                raise DraftingContractError("Drafting preview lifetime is invalid.")
        if self.status in {DraftingRunStatus.VALIDATED, DraftingRunStatus.BLOCKED} and has_preview:
            raise DraftingContractError("Drafting run status cannot retain a preview binding.")
        if self.status in {
            DraftingRunStatus.REVIEW_PENDING,
            DraftingRunStatus.EXECUTION_REVIEW_PENDING,
            DraftingRunStatus.APPROVED,
            DraftingRunStatus.EXECUTING,
            DraftingRunStatus.EXECUTED,
            DraftingRunStatus.RECOVERY_REQUIRED,
            DraftingRunStatus.REJECTED,
        }:
            if self.review_id is None:
                raise DraftingContractError("Reviewed drafting run requires review ID.")
        decision_required_statuses = {
            DraftingRunStatus.APPROVED,
            DraftingRunStatus.EXECUTING,
            DraftingRunStatus.EXECUTED,
            DraftingRunStatus.RECOVERY_REQUIRED,
            DraftingRunStatus.REJECTED,
        }
        if self.status in decision_required_statuses:
            decided = _parse_timestamp(self.decided_at, "drafting decided_at")
            if not created <= decided <= updated:
                raise DraftingContractError("Drafting decision timestamp is invalid.")
        elif self.decided_at is not None and self.status is not DraftingRunStatus.EXPIRED:
            raise DraftingContractError("Undecided drafting run cannot have decided_at.")
        elif self.decided_at is not None:
            decided = _parse_timestamp(self.decided_at, "drafting decided_at")
            if not created <= decided <= updated:
                raise DraftingContractError("Drafting decision timestamp is invalid.")
        if not isinstance(self.audit, tuple) or not self.audit or any(
            not isinstance(item, DraftingAuditEvent) for item in self.audit
        ):
            raise DraftingContractError("Drafting audit must be a non-empty typed tuple.")
        if tuple(item.sequence for item in self.audit) != tuple(range(1, len(self.audit) + 1)):
            raise DraftingContractError("Drafting audit sequence is not contiguous.")
        if self.audit[-1].status is not self.status:
            raise DraftingContractError("Drafting audit does not match current status.")
        if not isinstance(self.drawing_mutation_performed, bool):
            raise DraftingContractError("Drawing mutation flag must be boolean.")
        if self.execution_request_sha256 is not None:
            _hex(self.execution_request_sha256, 64, "execution request SHA-256")
        execution_statuses = {
            DraftingRunStatus.EXECUTION_REVIEW_PENDING,
            DraftingRunStatus.EXECUTING,
            DraftingRunStatus.EXECUTED,
            DraftingRunStatus.RECOVERY_REQUIRED,
        }
        if self.status in execution_statuses and self.execution_request_sha256 is None:
            raise DraftingContractError("Execution state requires an exact request binding.")
        if self.execution_id is not None:
            _hex(self.execution_id, 32, "drafting execution ID")
        if self.status in {
            DraftingRunStatus.EXECUTING,
            DraftingRunStatus.EXECUTED,
            DraftingRunStatus.RECOVERY_REQUIRED,
        }:
            if self.execution_id is None or self.execution_started_at is None:
                raise DraftingContractError("Started execution requires identity and timestamp.")
            started = _parse_timestamp(self.execution_started_at, "execution started_at")
            if not created <= started <= updated:
                raise DraftingContractError("Execution start timestamp is invalid.")
        elif self.execution_id is not None or self.execution_started_at is not None:
            raise DraftingContractError("Unstarted execution cannot retain start evidence.")
        completed_values = (
            self.execution_completed_at,
            self.autocad_run_id,
            self.source_sha256,
            self.output_sha256,
            self.recovery_evidence_sha256,
        )
        if self.status is DraftingRunStatus.EXECUTED:
            if any(value is None for value in completed_values):
                raise DraftingContractError("Executed run requires complete recovery evidence.")
            completed = _parse_timestamp(
                self.execution_completed_at, "execution completed_at"
            )
            if completed > updated:
                raise DraftingContractError("Execution completion timestamp is invalid.")
            _hex(self.autocad_run_id, 32, "AutoCAD run ID")
            _hex(self.source_sha256, 64, "execution source SHA-256")
            _hex(self.output_sha256, 64, "execution output SHA-256")
            _hex(
                self.recovery_evidence_sha256,
                64,
                "recovery evidence SHA-256",
            )
            if self.drawing_mutation_performed is not True:
                raise DraftingContractError("Executed run must report verified scratch mutation.")
        elif any(value is not None for value in completed_values):
            raise DraftingContractError("Incomplete execution cannot retain completion evidence.")
        if self.drawing_mutation_performed and self.status not in {
            DraftingRunStatus.EXECUTED,
            DraftingRunStatus.RECOVERY_REQUIRED,
        }:
            raise DraftingContractError("Drawing mutation is invalid for this drafting state.")


@dataclass(frozen=True, slots=True)
class DraftingRunCreated:
    run: DraftingRun
    owner_key: str


@dataclass(frozen=True, slots=True)
class _StoredRun:
    run: DraftingRun
    owner_key_sha256: str


class DraftingLifecycleService:
    """Atomically persist proposal hashes, ownership, review, cancellation, and expiry."""

    def __init__(
        self,
        path: Path,
        *,
        clock: Callable[[], datetime] | None = None,
        id_factory: Callable[[], str] | None = None,
        secret_factory: Callable[[], str] | None = None,
        logger: logging.Logger | None = None,
        maximum_runs: int = 1000,
        maximum_operations_per_minute: int = 60,
    ) -> None:
        if not isinstance(path, Path):
            raise DraftingContractError("Drafting lifecycle path must be a Path.")
        if not 1 <= maximum_runs <= 10_000:
            raise DraftingContractError("Drafting maximum_runs is invalid.")
        if not 5 <= maximum_operations_per_minute <= 600:
            raise DraftingContractError("Drafting operation rate limit is invalid.")
        self._store = StateStore(path)
        self._clock = clock or (lambda: datetime.now(UTC))
        self._id_factory = id_factory or (lambda: uuid.uuid4().hex)
        self._secret_factory = secret_factory or (lambda: secrets.token_urlsafe(32))
        self._logger = logger or logging.getLogger("ai_engineer.drafting")
        self.maximum_runs = maximum_runs
        self.maximum_operations_per_minute = maximum_operations_per_minute
        self._lock = threading.RLock()
        self._operations: dict[str, deque[datetime]] = {}

    def create(
        self,
        proposal: DraftingProposal,
        validation: DraftingValidationResult,
        *,
        ttl_minutes: int = 60,
    ) -> DraftingRunCreated:
        if not isinstance(proposal, DraftingProposal) or not isinstance(validation, DraftingValidationResult):
            raise DraftingContractError("Drafting lifecycle requires typed proposal and validation.")
        if not 5 <= ttl_minutes <= 120:
            raise DraftingContractError("Drafting lifecycle TTL must be 5 through 120 minutes.")
        if (
            validation.proposal_id != proposal.proposal_id
            or validation.proposal_sha256 != proposal.proposal_sha256
            or validation.model_sha256 != proposal.binding.model_sha256
            or validation.symbol_catalog_sha256 != proposal.binding.symbol_catalog_sha256
        ):
            raise DraftingContractError("Drafting validation is bound to a different proposal.")
        with self._lock:
            records = self._load()
            if len(records) >= self.maximum_runs:
                raise DraftingContractError("Drafting lifecycle run limit reached.")
            now = self._now()
            proposal_created = _parse_timestamp(proposal.created_at, "proposal created_at")
            proposal_expires = _parse_timestamp(proposal.expires_at, "proposal expires_at")
            if not proposal_created <= now < proposal_expires:
                raise DraftingContractError(
                    "Drafting proposal is not active at lifecycle creation time."
                )
            run_id = self._new_id({item.run.run_id for item in records}, "run")
            owner_key = self._secret_factory()
            if not isinstance(owner_key, str) or not 32 <= len(owner_key) <= 128:
                raise DraftingContractError("Generated drafting owner key is invalid.")
            status = DraftingRunStatus.VALIDATED if validation.is_valid else DraftingRunStatus.BLOCKED
            timestamp = _timestamp(now)
            run = DraftingRun(
                1, run_id, proposal.proposal_id, proposal.proposal_sha256,
                validation.validation_sha256, proposal.binding.model_sha256,
                proposal.binding.symbol_catalog_sha256, status, timestamp, timestamp,
                _timestamp(min(now + timedelta(minutes=ttl_minutes), proposal_expires)),
                None, None, None, None, None,
                (DraftingAuditEvent(1, "created", status, timestamp),), False,
            )
            records.append(_StoredRun(run, _digest(owner_key)))
            self._save(records)
            self._log("drafting_run_created", run)
            return DraftingRunCreated(run, owner_key)

    def get(self, run_id: str, owner_key: str) -> DraftingRun:
        with self._lock:
            records = self._load()
            index, stored = self._owned(records, run_id, owner_key)
            self._record_operation(run_id)
            updated = self._expire(stored)
            if updated != stored:
                records[index] = updated
                self._save(records)
            return updated.run

    def request_review(self, run_id: str, owner_key: str) -> DraftingRun:
        with self._lock:
            records = self._load()
            index, stored = self._owned(records, run_id, owner_key)
            self._record_operation(run_id)
            stored = self._expire(stored)
            if stored.run.status is not DraftingRunStatus.PREVIEW_READY:
                raise DraftingContractError(
                    "Only an active preview-bound drafting run may request review."
                )
            review_id = self._new_id(
                {item.run.review_id for item in records if item.run.review_id}, "review"
            )
            updated = self._transition(stored, DraftingRunStatus.REVIEW_PENDING, "review_requested", review_id=review_id)
            records[index] = updated
            self._save(records)
            self._log("drafting_review_requested", updated.run)
            return updated.run

    def request_execution_review(
        self,
        run_id: str,
        owner_key: str,
        *,
        execution_request_sha256: str,
    ) -> DraftingRun:
        """Bind the exact source and geometry execution request before approval."""
        _hex(execution_request_sha256, 64, "execution request SHA-256")
        with self._lock:
            records = self._load()
            index, stored = self._owned(records, run_id, owner_key)
            self._record_operation(run_id)
            stored = self._expire(stored)
            if stored.run.status is not DraftingRunStatus.PREVIEW_READY:
                raise DraftingContractError(
                    "Only an active preview may request execution review."
                )
            review_id = self._new_id(
                {item.run.review_id for item in records if item.run.review_id},
                "execution review",
            )
            updated = self._transition(
                stored,
                DraftingRunStatus.EXECUTION_REVIEW_PENDING,
                "execution_review_requested",
                review_id=review_id,
                execution_request_sha256=execution_request_sha256,
            )
            records[index] = updated
            self._save(records)
            self._log("drafting_execution_review_requested", updated.run)
            return updated.run

    def bind_preview(
        self, run_id: str, owner_key: str, preview: DraftingPreview
    ) -> DraftingRun:
        """Bind one exact transient preview hash before any review can be requested."""
        if not isinstance(preview, DraftingPreview):
            raise DraftingContractError("Drafting lifecycle requires a typed preview.")
        with self._lock:
            records = self._load()
            index, stored = self._owned(records, run_id, owner_key)
            self._record_operation(run_id)
            stored = self._expire(stored)
            run = stored.run
            if run.status is not DraftingRunStatus.VALIDATED:
                raise DraftingContractError(
                    "Only a valid active drafting run may bind a preview."
                )
            if (
                preview.proposal.proposal_id != run.proposal_id
                or preview.proposal.proposal_sha256 != run.proposal_sha256
                or preview.binding.model_sha256 != run.model_sha256
                or preview.binding.symbol_catalog_sha256 != run.symbol_catalog_sha256
            ):
                raise DraftingContractError("Drafting preview is bound to a different run.")
            preview_expires = _parse_timestamp(
                preview.expires_at, "drafting preview expires_at"
            )
            if self._now() >= preview_expires or preview_expires > _parse_timestamp(
                run.expires_at, "drafting expires_at"
            ):
                raise DraftingContractError("Drafting preview is outside the run lifetime.")
            timestamp = _timestamp(self._now())
            audit = run.audit + (
                DraftingAuditEvent(
                    len(run.audit) + 1,
                    "preview_created",
                    DraftingRunStatus.PREVIEW_READY,
                    timestamp,
                ),
            )
            updated = _StoredRun(
                replace(
                    run,
                    status=DraftingRunStatus.PREVIEW_READY,
                    updated_at=timestamp,
                    preview_id=preview.preview_id,
                    preview_sha256=preview.preview_sha256,
                    preview_expires_at=preview.expires_at,
                    audit=audit,
                ),
                stored.owner_key_sha256,
            )
            records[index] = updated
            self._save(records)
            self._log("drafting_preview_created", updated.run)
            return updated.run

    def decide(self, run_id: str, owner_key: str, *, approved: bool) -> DraftingRun:
        """Decide only the legacy read-only preview review path."""
        return self._decide(
            run_id,
            owner_key,
            approved=approved,
            expected_status=DraftingRunStatus.REVIEW_PENDING,
        )

    def decide_execution(
        self, run_id: str, owner_key: str, *, approved: bool
    ) -> DraftingRun:
        """Approve or reject only an exact execution-bound review."""
        return self._decide(
            run_id,
            owner_key,
            approved=approved,
            expected_status=DraftingRunStatus.EXECUTION_REVIEW_PENDING,
        )

    def _decide(
        self,
        run_id: str,
        owner_key: str,
        *,
        approved: bool,
        expected_status: DraftingRunStatus,
    ) -> DraftingRun:
        if not isinstance(approved, bool):
            raise DraftingContractError("Drafting review decision must be boolean.")
        with self._lock:
            records = self._load()
            index, stored = self._owned(records, run_id, owner_key)
            self._record_operation(run_id)
            stored = self._expire(stored)
            if stored.run.status is not expected_status:
                raise DraftingContractError("Only a pending drafting review may be decided.")
            status = DraftingRunStatus.APPROVED if approved else DraftingRunStatus.REJECTED
            event = "approved" if approved else "rejected"
            updated = self._transition(stored, status, event, decided=True)
            records[index] = updated
            self._save(records)
            self._log(f"drafting_review_{event}", updated.run)
            return updated.run

    def begin_execution(
        self,
        run_id: str,
        owner_key: str,
        *,
        execution_request_sha256: str,
    ) -> DraftingRun:
        """Atomically consume one exact approved execution request before AutoCAD."""
        _hex(execution_request_sha256, 64, "execution request SHA-256")
        with self._lock:
            records = self._load()
            index, stored = self._owned(records, run_id, owner_key)
            self._record_operation(run_id)
            stored = self._expire(stored)
            if stored.run.status is not DraftingRunStatus.APPROVED:
                raise DraftingContractError("Only an approved drafting run may execute.")
            if stored.run.execution_request_sha256 != execution_request_sha256:
                raise DraftingContractError(
                    "Execution request differs from the approved binding."
                )
            execution_id = self._new_id(
                {item.run.execution_id for item in records if item.run.execution_id},
                "execution",
            )
            updated = self._transition(
                stored,
                DraftingRunStatus.EXECUTING,
                "execution_started",
                execution_id=execution_id,
                execution_started=True,
            )
            records[index] = updated
            self._save(records)
            self._log("drafting_execution_started", updated.run)
            return updated.run

    def complete_execution(
        self,
        run_id: str,
        owner_key: str,
        *,
        execution_id: str,
        autocad_run_id: str,
        source_sha256: str,
        output_sha256: str,
        recovery_evidence_sha256: str,
    ) -> DraftingRun:
        """Persist verified scratch output and recovery identities atomically."""
        _hex(execution_id, 32, "drafting execution ID")
        _hex(autocad_run_id, 32, "AutoCAD run ID")
        _hex(source_sha256, 64, "execution source SHA-256")
        _hex(output_sha256, 64, "execution output SHA-256")
        _hex(recovery_evidence_sha256, 64, "recovery evidence SHA-256")
        with self._lock:
            records = self._load()
            index, stored = self._owned(records, run_id, owner_key)
            self._record_operation(run_id)
            if (
                stored.run.status is not DraftingRunStatus.EXECUTING
                or stored.run.execution_id != execution_id
            ):
                raise DraftingContractError("Drafting execution is not active.")
            updated = self._transition(
                stored,
                DraftingRunStatus.EXECUTED,
                "execution_completed",
                completed=True,
                autocad_run_id=autocad_run_id,
                source_sha256=source_sha256,
                output_sha256=output_sha256,
                recovery_evidence_sha256=recovery_evidence_sha256,
                drawing_mutation_performed=True,
            )
            records[index] = updated
            self._save(records)
            self._log("drafting_execution_completed", updated.run)
            return updated.run

    def fail_execution(
        self,
        run_id: str,
        owner_key: str,
        *,
        execution_id: str,
    ) -> DraftingRun:
        """Conservatively stop replay after an execution attempt fails."""
        _hex(execution_id, 32, "drafting execution ID")
        with self._lock:
            records = self._load()
            index, stored = self._owned(records, run_id, owner_key)
            self._record_operation(run_id)
            if (
                stored.run.status is not DraftingRunStatus.EXECUTING
                or stored.run.execution_id != execution_id
            ):
                raise DraftingContractError("Drafting execution is not active.")
            updated = self._transition(
                stored,
                DraftingRunStatus.RECOVERY_REQUIRED,
                "execution_failed",
            )
            records[index] = updated
            self._save(records)
            self._log("drafting_execution_failed", updated.run)
            return updated.run

    def cancel(self, run_id: str, owner_key: str) -> DraftingRun:
        with self._lock:
            records = self._load()
            index, stored = self._owned(records, run_id, owner_key)
            self._record_operation(run_id)
            stored = self._expire(stored)
            if stored.run.status not in {
                DraftingRunStatus.VALIDATED,
                DraftingRunStatus.BLOCKED,
                DraftingRunStatus.PREVIEW_READY,
                DraftingRunStatus.REVIEW_PENDING,
                DraftingRunStatus.EXECUTION_REVIEW_PENDING,
            }:
                raise DraftingContractError("Drafting run cannot be cancelled in its current state.")
            updated = self._transition(stored, DraftingRunStatus.CANCELLED, "cancelled")
            records[index] = updated
            self._save(records)
            self._log("drafting_run_cancelled", updated.run)
            return updated.run

    def _transition(
        self, stored: _StoredRun, status: DraftingRunStatus, event: str,
        *,
        review_id: str | None = None,
        decided: bool = False,
        execution_request_sha256: str | None = None,
        execution_id: str | None = None,
        execution_started: bool = False,
        completed: bool = False,
        autocad_run_id: str | None = None,
        source_sha256: str | None = None,
        output_sha256: str | None = None,
        recovery_evidence_sha256: str | None = None,
        drawing_mutation_performed: bool | None = None,
    ) -> _StoredRun:
        now = self._now()
        timestamp = _timestamp(now)
        run = stored.run
        audit = run.audit + (DraftingAuditEvent(len(run.audit) + 1, event, status, timestamp),)
        return _StoredRun(
            replace(
                run, status=status, updated_at=timestamp,
                review_id=review_id if review_id is not None else run.review_id,
                decided_at=timestamp if decided else run.decided_at,
                execution_request_sha256=(
                    execution_request_sha256
                    if execution_request_sha256 is not None
                    else run.execution_request_sha256
                ),
                execution_id=execution_id if execution_id is not None else run.execution_id,
                execution_started_at=(
                    timestamp if execution_started else run.execution_started_at
                ),
                execution_completed_at=(
                    timestamp if completed else run.execution_completed_at
                ),
                autocad_run_id=autocad_run_id or run.autocad_run_id,
                source_sha256=source_sha256 or run.source_sha256,
                output_sha256=output_sha256 or run.output_sha256,
                recovery_evidence_sha256=(
                    recovery_evidence_sha256 or run.recovery_evidence_sha256
                ),
                drawing_mutation_performed=(
                    run.drawing_mutation_performed
                    if drawing_mutation_performed is None
                    else drawing_mutation_performed
                ),
                audit=audit,
            ),
            stored.owner_key_sha256,
        )

    def _expire(self, stored: _StoredRun) -> _StoredRun:
        active_statuses = {
            DraftingRunStatus.VALIDATED,
            DraftingRunStatus.BLOCKED,
            DraftingRunStatus.PREVIEW_READY,
            DraftingRunStatus.REVIEW_PENDING,
            DraftingRunStatus.EXECUTION_REVIEW_PENDING,
            DraftingRunStatus.APPROVED,
        }
        deadline = _parse_timestamp(stored.run.expires_at, "expires_at")
        if stored.run.preview_expires_at is not None:
            deadline = min(
                deadline,
                _parse_timestamp(
                    stored.run.preview_expires_at, "drafting preview expires_at"
                ),
            )
        if stored.run.status in active_statuses and self._now() >= deadline:
            return self._transition(stored, DraftingRunStatus.EXPIRED, "expired")
        return stored

    def _owned(self, records: list[_StoredRun], run_id: str, owner_key: str) -> tuple[int, _StoredRun]:
        if not isinstance(run_id, str) or not isinstance(owner_key, str):
            raise ResourceNotFoundError("Drafting run was not found.")
        for index, stored in enumerate(records):
            if stored.run.run_id == run_id and secrets.compare_digest(stored.owner_key_sha256, _digest(owner_key)):
                return index, stored
        raise ResourceNotFoundError("Drafting run was not found.")

    def _record_operation(self, run_id: str) -> None:
        now = self._now()
        cutoff = now - timedelta(minutes=1)
        operations = self._operations.setdefault(run_id, deque())
        while operations and operations[0] <= cutoff:
            operations.popleft()
        if len(operations) >= self.maximum_operations_per_minute:
            raise DraftingContractError("Drafting lifecycle operation rate limit reached.")
        operations.append(now)

    def _load(self) -> list[_StoredRun]:
        document = self._store.load()
        if set(document) != {"schema_version", "runs"} or document["schema_version"] != 1 or not isinstance(document["runs"], list):
            raise DraftingContractError("Drafting lifecycle state is invalid.")
        return [_stored_from_dict(item) for item in document["runs"]]

    def _save(self, records: list[_StoredRun]) -> None:
        self._store.save({"schema_version": 1, "runs": [_stored_to_dict(item) for item in records]})

    def _new_id(self, used: set[str | None], name: str) -> str:
        value = self._id_factory()
        _hex(value, 32, f"generated drafting {name} ID")
        if value in used:
            raise DraftingContractError(f"Generated drafting {name} ID is not unique.")
        return value

    def _now(self) -> datetime:
        value = self._clock()
        if not isinstance(value, datetime) or value.tzinfo is None:
            raise DraftingContractError("Drafting clock must return timezone-aware values.")
        return value.astimezone(UTC)

    def _log(self, event: str, run: DraftingRun) -> None:
        self._logger.info(event, extra={
            "run_id": run.run_id, "proposal_id": run.proposal_id,
            "proposal_sha256": run.proposal_sha256, "validation_sha256": run.validation_sha256,
            "status": run.status.value, "review_id": run.review_id,
            "preview_id": run.preview_id, "preview_sha256": run.preview_sha256,
            "execution_request_sha256": run.execution_request_sha256,
            "execution_id": run.execution_id,
            "autocad_run_id": run.autocad_run_id,
            "source_sha256": run.source_sha256,
            "output_sha256": run.output_sha256,
            "recovery_evidence_sha256": run.recovery_evidence_sha256,
            "drawing_mutation_performed": run.drawing_mutation_performed,
        })


def _stored_to_dict(stored: _StoredRun) -> dict[str, Any]:
    return {"run": stored.run.to_dict(), "owner_key_sha256": stored.owner_key_sha256}


def _stored_from_dict(value: object) -> _StoredRun:
    if not isinstance(value, dict) or set(value) != {"run", "owner_key_sha256"}:
        raise DraftingContractError("Stored drafting run is invalid.")
    raw = value["run"]
    if not isinstance(raw, dict):
        raise DraftingContractError("Stored drafting run record is invalid.")
    try:
        audit = tuple(
            DraftingAuditEvent(item["sequence"], item["event"], DraftingRunStatus(item["status"]), item["recorded_at"])
            for item in raw["audit"]
        )
        legacy_fields = {
            "schema_version", "run_id", "proposal_id", "proposal_sha256",
            "validation_sha256", "model_sha256", "symbol_catalog_sha256", "status",
            "created_at", "updated_at", "expires_at", "review_id", "decided_at",
            "audit", "drawing_mutation_performed",
        }
        current_fields = legacy_fields | {
            "preview_id", "preview_sha256", "preview_expires_at"
        }
        execution_fields = current_fields | {
            "execution_request_sha256", "execution_id", "execution_started_at",
            "execution_completed_at", "autocad_run_id", "source_sha256",
            "output_sha256", "recovery_evidence_sha256",
        }
        legacy_execution_fields = legacy_fields | (
            execution_fields - current_fields
        )
        if frozenset(raw) not in {
            frozenset(legacy_fields),
            frozenset(current_fields),
            frozenset(execution_fields),
            frozenset(legacy_execution_fields),
        }:
            raise DraftingContractError("Stored drafting run fields are invalid.")
        run = DraftingRun(
            raw["schema_version"], raw["run_id"], raw["proposal_id"], raw["proposal_sha256"],
            raw["validation_sha256"], raw["model_sha256"], raw["symbol_catalog_sha256"],
            DraftingRunStatus(raw["status"]), raw["created_at"], raw["updated_at"],
            raw["expires_at"], raw.get("preview_id"), raw.get("preview_sha256"),
            raw.get("preview_expires_at"), raw["review_id"], raw["decided_at"], audit,
            raw["drawing_mutation_performed"],
            raw.get("execution_request_sha256"), raw.get("execution_id"),
            raw.get("execution_started_at"), raw.get("execution_completed_at"),
            raw.get("autocad_run_id"), raw.get("source_sha256"),
            raw.get("output_sha256"), raw.get("recovery_evidence_sha256"),
        )
        owner_hash = value["owner_key_sha256"]
        _hex(owner_hash, 64, "stored owner key SHA-256")
    except (KeyError, TypeError, ValueError) as exc:
        raise DraftingContractError("Stored drafting run fields are invalid.") from exc
    return _StoredRun(run, owner_hash)


def _digest(value: str) -> str:
    return hashlib.sha256(value.encode("utf-8")).hexdigest()


def _hex(value: object, length: int, name: str) -> None:
    if not isinstance(value, str) or len(value) != length or any(character not in "0123456789abcdef" for character in value):
        raise DraftingContractError(f"{name} is invalid.")


def _timestamp(value: datetime) -> str:
    return value.astimezone(UTC).isoformat().replace("+00:00", "Z")


def _parse_timestamp(value: object, name: str) -> datetime:
    if not isinstance(value, str) or not value.endswith("Z") or len(value) > 40:
        raise DraftingContractError(f"{name} must be a UTC timestamp.")
    try:
        return datetime.fromisoformat(value[:-1] + "+00:00")
    except ValueError as exc:
        raise DraftingContractError(f"{name} must be a valid UTC timestamp.") from exc
