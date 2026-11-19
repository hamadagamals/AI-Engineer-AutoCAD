"""Tests for durable metadata-only drafting proposal lifecycle state."""

from __future__ import annotations

import hashlib
import logging
from datetime import UTC, datetime, timedelta
from pathlib import Path
from types import SimpleNamespace

import pytest

from agent.drafting_service import DraftingLifecycleService, DraftingRunStatus
from agent.drafting_planner import DraftingPlanCoordinator
from agent.exceptions import DraftingContractError, ResourceNotFoundError
from core.electrical_drafting import (
    ProposalInputIssue,
    ProposalIssueKind,
    ProposedDevice,
    ProposedCircuit,
    ProposedLayer,
    ProposedRoute,
    ProposedTerminal,
    create_drafting_preview,
    create_drafting_proposal,
    create_source_model_binding,
)
from core.electrical_drafting_validation import validate_drafting_proposal
from core.electrical_model import (
    Confidence,
    Drawing,
    DrawingLayer,
    ElectricalProjectModel,
    PrivacyClassification,
    Provenance,
    SourceKind,
    UnitSystem,
)
from integrations.electrical_symbols import ElectricalSymbolCatalog
from integrations.autocad import AutoCADManager


ROOT = Path(__file__).resolve().parents[1]
CATALOG = ROOT / "plugins" / "electrical_symbols" / "catalog.json"
EVIDENCE_SHA256 = hashlib.sha256(b"drafting service context").hexdigest()


def _model() -> ElectricalProjectModel:
    drawing_id = "autocad:123456781234123412341234567890ab"
    return ElectricalProjectModel(
        1,
        "project:test",
        drawing_id,
        "a" * 32,
        UnitSystem.MILLIMETER,
        Provenance(
            SourceKind.AUTOCAD_SELECTION,
            "2026-08-07T08:00:00Z",
            Confidence.OBSERVED,
            PrivacyClassification.PROJECT_CONFIDENTIAL,
            False,
            EVIDENCE_SHA256,
        ),
        (Drawing(drawing_id, "sample.dwg", UnitSystem.MILLIMETER, "Model", ("layer:power", "layer:wire"), 0),),
        (
            DrawingLayer("layer:power", "E-POWR-DEVC", 2, "CONTINUOUS", False, False, False),
            DrawingLayer("layer:wire", "E-WIRE", 3, "CONTINUOUS", False, False, False),
        ),
        (), (), (), (), (), (), (), (), (), (),
    )


def _proposal(*, blocked: bool = False, proposal_id: str = "b" * 32):
    model = _model()
    symbol = ElectricalSymbolCatalog.load(CATALOG).get("socket_single")
    catalog_sha256 = hashlib.sha256(CATALOG.read_bytes()).hexdigest()
    binding = create_source_model_binding(
        model,
        context_evidence_sha256=EVIDENCE_SHA256,
        symbol_catalog_sha256=catalog_sha256,
    )
    device = ProposedDevice(
        "device:socket-1",
        symbol.symbol_id,
        symbol.definition_sha256,
        symbol.block_name,
        "layer:power",
        (100.0, 200.0, 0.0),
        0.0,
        (1.0, 1.0, 1.0),
        (),
    )
    issues = (
        ProposalInputIssue(
            "issue:missing",
            ProposalIssueKind.MISSING,
            "devices.rating",
            "Rating requires engineer input.",
            (device.device_id,),
        ),
    ) if blocked else ()
    proposal = create_drafting_proposal(
        proposal_id=proposal_id,
        binding=binding,
        layers=(ProposedLayer("layer:power", "E-POWR-DEVC", 2, "CONTINUOUS", False),),
        devices=(device,),
        terminals=(),
        circuits=(),
        routes=(),
        input_issues=issues,
        created_at="2026-08-07T09:00:00Z",
        expires_at="2026-08-07T12:00:00Z",
    )
    validation = validate_drafting_proposal(proposal, model=model, symbol_catalog_path=CATALOG)
    return proposal, validation


def _state(path: Path) -> None:
    path.write_text('{"schema_version":1,"runs":[]}\n', encoding="utf-8")


def _preview(proposal, *, preview_id: str = "c" * 32):
    return create_drafting_preview(
        preview_id=preview_id,
        proposal=proposal,
        created_at="2026-08-07T09:01:00Z",
        expires_at="2026-08-07T09:20:00Z",
    )


def _execution_proposal():
    model = _model()
    symbol = ElectricalSymbolCatalog.load(CATALOG).get("socket_single")
    binding = create_source_model_binding(
        model,
        context_evidence_sha256=EVIDENCE_SHA256,
        symbol_catalog_sha256=hashlib.sha256(CATALOG.read_bytes()).hexdigest(),
    )
    devices = (
        ProposedDevice(
            "device:source", symbol.symbol_id, symbol.definition_sha256,
            symbol.block_name, "layer:power", (0.0, 0.0, 0.0), 0.0,
            (1.0, 1.0, 1.0), (),
        ),
        ProposedDevice(
            "device:load", symbol.symbol_id, symbol.definition_sha256,
            symbol.block_name, "layer:power", (1000.0, 0.0, 0.0), 90.0,
            (1.0, 1.0, 1.0), (),
        ),
    )
    terminals = (
        ProposedTerminal("terminal:source", "device:source", "L", (0.0, 0.0, 0.0)),
        ProposedTerminal("terminal:load", "device:load", "L", (1000.0, 0.0, 0.0)),
    )
    route = ProposedRoute(
        "route:power", "circuit:power", "layer:wire", "terminal:source",
        "terminal:load", ((0.0, 0.0, 0.0), (500.0, 0.0, 0.0), (1000.0, 0.0, 0.0)),
    )
    proposal = create_drafting_proposal(
        proposal_id="d" * 32,
        binding=binding,
        layers=(
            ProposedLayer("layer:power", "E-POWR-DEVC", 2, "CONTINUOUS", False),
            ProposedLayer("layer:wire", "E-WIRE", 3, "CONTINUOUS", False),
        ),
        devices=devices,
        terminals=terminals,
        circuits=(
            ProposedCircuit(
                "circuit:power", "Power circuit",
                ("terminal:source", "terminal:load"), ("route:power",),
            ),
        ),
        routes=(route,),
        input_issues=(),
        created_at="2026-08-07T09:00:00Z",
        expires_at="2026-08-07T12:00:00Z",
    )
    return proposal, validate_drafting_proposal(
        proposal, model=model, symbol_catalog_path=CATALOG
    )


def test_lifecycle_survives_restart_and_persists_metadata_only(tmp_path: Path) -> None:
    path = tmp_path / "drafting-runs.json"
    _state(path)
    now = [datetime(2026, 8, 7, 9, 0, tzinfo=UTC)]
    ids = iter(("1" * 32, "2" * 32))
    service = DraftingLifecycleService(
        path,
        clock=lambda: now[0],
        id_factory=lambda: next(ids),
        secret_factory=lambda: "owner-secret-with-at-least-thirty-two-characters",
    )
    proposal, validation = _proposal()

    created = service.create(proposal, validation, ttl_minutes=30)
    assert created.run.status is DraftingRunStatus.VALIDATED
    raw = path.read_text(encoding="utf-8")
    assert created.owner_key not in raw
    assert "AIENG_E_SOCKET_SINGLE_V1" not in raw
    assert "100.0" not in raw

    restarted = DraftingLifecycleService(path, clock=lambda: now[0])
    restored = restarted.get(created.run.run_id, created.owner_key)
    assert restored == created.run
    with pytest.raises(ResourceNotFoundError):
        restarted.get(created.run.run_id, "wrong-owner-key-with-at-least-32-chars")


def test_review_is_durable_bound_and_replay_safe(tmp_path: Path) -> None:
    path = tmp_path / "drafting-runs.json"
    _state(path)
    now = [datetime(2026, 8, 7, 9, 0, tzinfo=UTC)]
    ids = iter(("3" * 32, "4" * 32))
    service = DraftingLifecycleService(
        path,
        clock=lambda: now[0],
        id_factory=lambda: next(ids),
        secret_factory=lambda: "another-owner-secret-with-thirty-two-chars",
    )
    proposal, validation = _proposal()
    created = service.create(proposal, validation)
    previewed = service.bind_preview(
        created.run.run_id, created.owner_key, _preview(proposal)
    )
    assert previewed.status is DraftingRunStatus.PREVIEW_READY
    assert previewed.preview_id == "c" * 32
    pending = service.request_review(created.run.run_id, created.owner_key)
    assert pending.status is DraftingRunStatus.REVIEW_PENDING
    assert pending.review_id == "4" * 32

    now[0] += timedelta(minutes=1)
    approved = service.decide(created.run.run_id, created.owner_key, approved=True)
    assert approved.status is DraftingRunStatus.APPROVED
    assert [item.event for item in approved.audit] == [
        "created", "preview_created", "review_requested", "approved"
    ]
    restarted = DraftingLifecycleService(path, clock=lambda: now[0])
    assert restarted.get(created.run.run_id, created.owner_key).status is DraftingRunStatus.APPROVED
    with pytest.raises(DraftingContractError, match="pending"):
        restarted.decide(created.run.run_id, created.owner_key, approved=True)


def test_preview_expiry_replay_and_mismatch_fail_closed(tmp_path: Path) -> None:
    path = tmp_path / "drafting-runs.json"
    _state(path)
    now = [datetime(2026, 8, 7, 9, 0, tzinfo=UTC)]
    service = DraftingLifecycleService(
        path,
        clock=lambda: now[0],
        id_factory=lambda: "f" * 32,
        secret_factory=lambda: "expiry-preview-owner-secret-at-least-32-chars",
    )
    proposal, validation = _proposal()
    created = service.create(proposal, validation, ttl_minutes=30)
    mismatched, _ = _proposal(proposal_id="1" * 32)
    with pytest.raises(DraftingContractError, match="different run"):
        service.bind_preview(
            created.run.run_id,
            created.owner_key,
            _preview(mismatched, preview_id="1" * 32),
        )
    service.bind_preview(created.run.run_id, created.owner_key, _preview(proposal))
    with pytest.raises(DraftingContractError, match="valid active"):
        service.bind_preview(
            created.run.run_id,
            created.owner_key,
            _preview(proposal, preview_id="2" * 32),
        )

    now[0] = datetime(2026, 8, 7, 9, 21, tzinfo=UTC)
    with pytest.raises(DraftingContractError, match="preview-bound"):
        service.request_review(created.run.run_id, created.owner_key)
    expired = service.get(created.run.run_id, created.owner_key)
    assert expired.status is DraftingRunStatus.EXPIRED
    assert expired.preview_sha256 is not None
    assert expired.audit[-1].event == "expired"


def test_blocked_cancelled_and_expired_runs_cannot_request_review(tmp_path: Path) -> None:
    path = tmp_path / "drafting-runs.json"
    _state(path)
    now = [datetime(2026, 8, 7, 9, 0, tzinfo=UTC)]
    ids = iter(("5" * 32, "6" * 32))
    service = DraftingLifecycleService(
        path,
        clock=lambda: now[0],
        id_factory=lambda: next(ids),
        secret_factory=lambda: "third-owner-secret-with-at-least-32-chars",
    )
    blocked_proposal, blocked_validation = _proposal(blocked=True)
    blocked = service.create(blocked_proposal, blocked_validation)
    assert blocked.run.status is DraftingRunStatus.BLOCKED
    with pytest.raises(DraftingContractError, match="preview-bound"):
        service.request_review(blocked.run.run_id, blocked.owner_key)
    cancelled = service.cancel(blocked.run.run_id, blocked.owner_key)
    assert cancelled.status is DraftingRunStatus.CANCELLED

    proposal, validation = _proposal()
    active = service.create(proposal, validation, ttl_minutes=5)
    now[0] += timedelta(minutes=6)
    expired = service.get(active.run.run_id, active.owner_key)
    assert expired.status is DraftingRunStatus.EXPIRED
    with pytest.raises(DraftingContractError, match="preview-bound"):
        service.request_review(active.run.run_id, active.owner_key)


def test_planner_parses_then_reimports_exact_local_context_before_persisting(
    tmp_path: Path,
) -> None:
    path = tmp_path / "drafting-runs.json"
    _state(path)
    service = DraftingLifecycleService(
        path,
        clock=lambda: datetime(2026, 8, 7, 9, 0, tzinfo=UTC),
        id_factory=lambda: "7" * 32,
        secret_factory=lambda: "planner-owner-secret-with-at-least-32-chars",
    )
    proposal, _validation = _proposal()
    calls: list[dict[str, object]] = []
    autocad = object.__new__(AutoCADManager)
    autocad.symbol_catalog_path = CATALOG

    def import_context(**values):
        calls.append(values)
        return SimpleNamespace(
            model=_model(),
            read_only=True,
            provider_egress_performed=False,
        )

    autocad.import_drawing_context = import_context
    coordinator = DraftingPlanCoordinator(autocad, service)

    created = coordinator.create(proposal.to_dict(), ttl_minutes=30)

    assert created.validation.is_valid is True
    assert created.lifecycle.run.status is DraftingRunStatus.VALIDATED
    assert calls == [
        {
            "context_id": proposal.binding.context_id,
            "evidence_sha256": proposal.binding.context_evidence_sha256,
            "confirmed": True,
        }
    ]


def test_planner_creates_transient_preview_and_persists_only_its_hash(
    tmp_path: Path,
) -> None:
    path = tmp_path / "drafting-runs.json"
    _state(path)
    now = datetime(2026, 8, 7, 9, 0, tzinfo=UTC)
    service = DraftingLifecycleService(
        path,
        clock=lambda: now,
        id_factory=lambda: "a" * 32,
        secret_factory=lambda: "preview-owner-secret-with-at-least-32-chars",
    )
    proposal, _validation = _proposal()
    autocad = object.__new__(AutoCADManager)
    autocad.symbol_catalog_path = CATALOG
    autocad.import_drawing_context = lambda **values: SimpleNamespace(
        model=_model(), read_only=True, provider_egress_performed=False
    )
    coordinator = DraftingPlanCoordinator(
        autocad,
        service,
        clock=lambda: now,
        id_factory=lambda: "d" * 32,
    )
    created = coordinator.create(proposal.to_dict(), ttl_minutes=30)

    previewed = coordinator.create_preview(
        created.lifecycle.run.run_id,
        created.lifecycle.owner_key,
        proposal.to_dict(),
        ttl_minutes=10,
    )

    assert previewed.run.status is DraftingRunStatus.PREVIEW_READY
    assert previewed.run.preview_id == previewed.preview.preview_id == "d" * 32
    assert previewed.run.preview_sha256 == previewed.preview.preview_sha256
    assert previewed.preview.drawing_mutation_performed is False
    assert previewed.preview.execution_approved is False
    stored = path.read_text(encoding="utf-8")
    assert previewed.preview.preview_sha256 in stored
    assert "AIENG_E_SOCKET_SINGLE_V1" not in stored
    assert "100.0" not in stored


def test_legacy_validated_run_without_preview_fields_still_loads(tmp_path: Path) -> None:
    path = tmp_path / "drafting-runs.json"
    _state(path)
    now = datetime(2026, 8, 7, 9, 0, tzinfo=UTC)
    service = DraftingLifecycleService(
        path,
        clock=lambda: now,
        id_factory=lambda: "e" * 32,
        secret_factory=lambda: "legacy-owner-secret-with-at-least-32-chars",
    )
    proposal, validation = _proposal()
    created = service.create(proposal, validation)
    document = __import__("json").loads(path.read_text(encoding="utf-8"))
    for name in ("preview_id", "preview_sha256", "preview_expires_at"):
        del document["runs"][0]["run"][name]
    path.write_text(__import__("json").dumps(document), encoding="utf-8")

    restored = DraftingLifecycleService(path, clock=lambda: now).get(
        created.run.run_id, created.owner_key
    )

    assert restored.status is DraftingRunStatus.VALIDATED
    assert restored.preview_id is None


def test_owner_operations_are_rate_limited_per_run(tmp_path: Path) -> None:
    path = tmp_path / "drafting-runs.json"
    _state(path)
    now = datetime(2026, 8, 7, 9, 0, tzinfo=UTC)
    service = DraftingLifecycleService(
        path,
        clock=lambda: now,
        id_factory=lambda: "8" * 32,
        secret_factory=lambda: "rate-limited-owner-secret-with-32-chars",
        maximum_operations_per_minute=5,
    )
    proposal, validation = _proposal()
    created = service.create(proposal, validation)

    for _ in range(5):
        assert service.get(created.run.run_id, created.owner_key).run_id == created.run.run_id
    with pytest.raises(DraftingContractError, match="rate limit"):
        service.get(created.run.run_id, created.owner_key)


def test_lifecycle_logs_contain_metadata_but_no_owner_or_proposal_values(
    tmp_path: Path,
) -> None:
    path = tmp_path / "drafting-runs.json"
    _state(path)
    records: list[logging.LogRecord] = []

    class Capture(logging.Handler):
        def emit(self, record: logging.LogRecord) -> None:
            records.append(record)

    logger = logging.Logger("drafting-redaction-test")
    logger.addHandler(Capture())
    owner_key = "redacted-owner-secret-with-at-least-32-chars"
    service = DraftingLifecycleService(
        path,
        clock=lambda: datetime(2026, 8, 7, 9, 0, tzinfo=UTC),
        id_factory=lambda: "9" * 32,
        secret_factory=lambda: owner_key,
        logger=logger,
    )
    proposal, validation = _proposal()

    created = service.create(proposal, validation)

    serialized = repr([record.__dict__ for record in records])
    assert records[0].msg == "drafting_run_created"
    assert records[0].proposal_sha256 == proposal.proposal_sha256
    assert owner_key not in serialized
    assert "AIENG_E_SOCKET_SINGLE_V1" not in serialized
    assert "100.0" not in serialized
    assert created.run.drawing_mutation_performed is False


def test_execution_review_is_exact_one_use_and_persists_recovery_hashes(
    tmp_path: Path,
) -> None:
    path = tmp_path / "drafting-runs.json"
    _state(path)
    now = [datetime(2026, 8, 7, 9, 0, tzinfo=UTC)]
    ids = iter(("1" * 32, "2" * 32, "3" * 32))
    service = DraftingLifecycleService(
        path,
        clock=lambda: now[0],
        id_factory=lambda: next(ids),
        secret_factory=lambda: "execution-owner-secret-with-at-least-32-chars",
    )
    proposal, validation = _execution_proposal()
    created = service.create(proposal, validation)
    service.bind_preview(created.run.run_id, created.owner_key, _preview(proposal))
    request_sha256 = "4" * 64

    pending = service.request_execution_review(
        created.run.run_id,
        created.owner_key,
        execution_request_sha256=request_sha256,
    )
    assert pending.status is DraftingRunStatus.EXECUTION_REVIEW_PENDING
    assert pending.execution_request_sha256 == request_sha256
    with pytest.raises(DraftingContractError, match="pending"):
        service.decide(created.run.run_id, created.owner_key, approved=True)

    approved = service.decide_execution(
        created.run.run_id, created.owner_key, approved=True
    )
    assert approved.status is DraftingRunStatus.APPROVED
    started = service.begin_execution(
        created.run.run_id,
        created.owner_key,
        execution_request_sha256=request_sha256,
    )
    assert started.status is DraftingRunStatus.EXECUTING
    assert started.execution_id == "3" * 32
    with pytest.raises(DraftingContractError, match="approved"):
        service.begin_execution(
            created.run.run_id,
            created.owner_key,
            execution_request_sha256=request_sha256,
        )

    completed = service.complete_execution(
        created.run.run_id,
        created.owner_key,
        execution_id="3" * 32,
        autocad_run_id="5" * 32,
        source_sha256="6" * 64,
        output_sha256="7" * 64,
        recovery_evidence_sha256="8" * 64,
    )
    assert completed.status is DraftingRunStatus.EXECUTED
    assert completed.drawing_mutation_performed is True
    assert completed.output_sha256 == "7" * 64
    assert [item.event for item in completed.audit][-4:] == [
        "execution_review_requested",
        "approved",
        "execution_started",
        "execution_completed",
    ]
    restored = DraftingLifecycleService(path, clock=lambda: now[0]).get(
        created.run.run_id, created.owner_key
    )
    assert restored == completed


def test_execution_failure_stops_replay_and_requires_recovery(tmp_path: Path) -> None:
    path = tmp_path / "drafting-runs.json"
    _state(path)
    ids = iter(("a" * 32, "b" * 32, "c" * 32))
    service = DraftingLifecycleService(
        path,
        clock=lambda: datetime(2026, 8, 7, 9, 0, tzinfo=UTC),
        id_factory=lambda: next(ids),
        secret_factory=lambda: "failed-execution-owner-secret-at-least-32-chars",
    )
    proposal, validation = _execution_proposal()
    created = service.create(proposal, validation)
    service.bind_preview(created.run.run_id, created.owner_key, _preview(proposal))
    service.request_execution_review(
        created.run.run_id,
        created.owner_key,
        execution_request_sha256="d" * 64,
    )
    service.decide_execution(created.run.run_id, created.owner_key, approved=True)
    started = service.begin_execution(
        created.run.run_id,
        created.owner_key,
        execution_request_sha256="d" * 64,
    )

    failed = service.fail_execution(
        created.run.run_id,
        created.owner_key,
        execution_id=started.execution_id,
    )

    assert failed.status is DraftingRunStatus.RECOVERY_REQUIRED
    assert failed.drawing_mutation_performed is False
    with pytest.raises(DraftingContractError, match="approved"):
        service.begin_execution(
            created.run.run_id,
            created.owner_key,
            execution_request_sha256="d" * 64,
        )


def test_coordinator_executes_only_exact_approved_scratch_plan(tmp_path: Path) -> None:
    path = tmp_path / "drafting-runs.json"
    _state(path)
    source = tmp_path / "sample.dwg"
    source.write_bytes(b"reviewed source")
    source_sha256 = hashlib.sha256(source.read_bytes()).hexdigest()
    now = datetime(2026, 8, 7, 9, 0, tzinfo=UTC)
    ids = iter(("1" * 32, "2" * 32, "3" * 32))
    lifecycle = DraftingLifecycleService(
        path,
        clock=lambda: now,
        id_factory=lambda: next(ids),
        secret_factory=lambda: "coordinator-execution-owner-secret-32-chars",
    )
    autocad = object.__new__(AutoCADManager)
    autocad.symbol_catalog_path = CATALOG
    autocad.import_drawing_context = lambda **values: SimpleNamespace(
        model=_model(),
        context=SimpleNamespace(document_name="sample.dwg"),
        read_only=True,
        provider_egress_performed=False,
    )
    autocad.verify_drawing_source = lambda value, expected_sha256: source
    calls: list[dict[str, object]] = []

    def create_fragment(value, **kwargs):
        calls.append({"source": value, **kwargs})
        return SimpleNamespace(
            run_id="4" * 32,
            source=source,
            output_drawing=tmp_path / "output.dwg",
            source_sha256=source_sha256,
            output_sha256="5" * 64,
        )

    autocad.create_electrical_circuit_fragment_scratch = create_fragment
    autocad.record_drafting_recovery_evidence = lambda artifact, **kwargs: SimpleNamespace(
        evidence_sha256="6" * 64
    )
    coordinator = DraftingPlanCoordinator(
        autocad, lifecycle, clock=lambda: now, id_factory=lambda: "7" * 32
    )
    proposal, _validation = _execution_proposal()
    created = coordinator.create(proposal.to_dict())
    coordinator.create_preview(
        created.lifecycle.run.run_id,
        created.lifecycle.owner_key,
        proposal.to_dict(),
    )
    prepared = coordinator.prepare_execution(
        created.lifecycle.run.run_id,
        created.lifecycle.owner_key,
        proposal.to_dict(),
        source_path=str(source),
        source_sha256=source_sha256,
    )
    lifecycle.decide_execution(
        created.lifecycle.run.run_id,
        created.lifecycle.owner_key,
        approved=True,
    )

    completed = coordinator.execute(
        created.lifecycle.run.run_id,
        created.lifecycle.owner_key,
        proposal.to_dict(),
        source_path=str(source),
        source_sha256=source_sha256,
    )

    assert completed.run.status is DraftingRunStatus.EXECUTED
    assert completed.plan.execution_request_sha256 == prepared.plan.execution_request_sha256
    assert calls[0]["expected_source_sha256"] == source_sha256
    assert calls[0]["confirmed"] is True
    with pytest.raises(DraftingContractError, match="approved"):
        coordinator.execute(
            created.lifecycle.run.run_id,
            created.lifecycle.owner_key,
            proposal.to_dict(),
            source_path=str(source),
            source_sha256=source_sha256,
        )
