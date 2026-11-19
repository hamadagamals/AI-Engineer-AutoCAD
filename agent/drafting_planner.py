"""Trusted orchestration from untrusted proposal JSON to durable drafting state."""

from __future__ import annotations

import hashlib
import json
import uuid
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta
from pathlib import Path
from typing import Any, Callable

from agent.drafting_service import DraftingLifecycleService, DraftingRun, DraftingRunCreated
from agent.exceptions import DraftingContractError
from core.electrical_drafting import (
    DraftingPreview,
    DraftingProposal,
    create_drafting_preview,
    drafting_proposal_from_dict,
)
from core.electrical_drafting_validation import (
    DraftingValidationResult,
    validate_drafting_proposal,
)
from integrations.autocad import (
    AutoCADManager,
    CircuitFragmentArtifact,
    CircuitPlacement,
    DraftingRecoveryEvidence,
)
from integrations.electrical_symbols import ElectricalSymbolCatalog


@dataclass(frozen=True, slots=True)
class DraftingPlanCreated:
    """Local result containing typed validation plus one-time lifecycle ownership."""

    proposal: DraftingProposal
    validation: DraftingValidationResult
    lifecycle: DraftingRunCreated


@dataclass(frozen=True, slots=True)
class DraftingPreviewCreated:
    """Transient exact preview plus its durable metadata-only lifecycle binding."""

    preview: DraftingPreview
    validation: DraftingValidationResult
    run: DraftingRun


@dataclass(frozen=True, slots=True)
class DraftingExecutionPlan:
    """Exact reviewed mapping from one preview to a supported scratch operation."""

    run_id: str
    proposal_sha256: str
    validation_sha256: str
    preview_id: str
    preview_sha256: str
    source_path: Path
    source_sha256: str
    symbol_id: str
    definition_sha256: str
    placements: tuple[CircuitPlacement, ...]
    wire_points: tuple[tuple[float, float, float], ...]
    execution_request_sha256: str

    def to_dict(self) -> dict[str, object]:
        return {
            **self._hash_payload(),
            "execution_request_sha256": self.execution_request_sha256,
            "operation": "autocad.create_electrical_circuit_fragment_scratch",
            "source_will_be_modified": False,
            "scratch_copy_will_be_modified": True,
            "handoff_requires_separate_approval": True,
        }

    def _hash_payload(self) -> dict[str, object]:
        return {
            "schema_version": 1,
            "run_id": self.run_id,
            "proposal_sha256": self.proposal_sha256,
            "validation_sha256": self.validation_sha256,
            "preview_id": self.preview_id,
            "preview_sha256": self.preview_sha256,
            "source_path": str(self.source_path),
            "source_sha256": self.source_sha256,
            "symbol_id": self.symbol_id,
            "definition_sha256": self.definition_sha256,
            "placements": [item.to_dict() for item in self.placements],
            "wire_points": [list(point) for point in self.wire_points],
        }


@dataclass(frozen=True, slots=True)
class DraftingExecutionPrepared:
    plan: DraftingExecutionPlan
    run: DraftingRun


@dataclass(frozen=True, slots=True)
class DraftingExecutionCompleted:
    plan: DraftingExecutionPlan
    artifact: CircuitFragmentArtifact
    recovery: DraftingRecoveryEvidence
    run: DraftingRun


class DraftingPlanCoordinator:
    """Re-import exact local evidence before trusting any provider-produced proposal."""

    def __init__(
        self,
        autocad: AutoCADManager,
        lifecycle: DraftingLifecycleService,
        *,
        clock: Callable[[], datetime] | None = None,
        id_factory: Callable[[], str] | None = None,
    ) -> None:
        if not isinstance(autocad, AutoCADManager):
            raise DraftingContractError("Drafting planner requires AutoCAD manager.")
        if not isinstance(lifecycle, DraftingLifecycleService):
            raise DraftingContractError("Drafting planner requires lifecycle service.")
        self.autocad = autocad
        self.lifecycle = lifecycle
        self._clock = clock or (lambda: datetime.now(UTC))
        self._id_factory = id_factory or (lambda: uuid.uuid4().hex)

    def create(self, raw_proposal: dict[str, Any], *, ttl_minutes: int = 60) -> DraftingPlanCreated:
        """Parse, re-import, validate, and persist hashes without drawing mutation."""
        proposal, validation = self._parse_and_validate(raw_proposal)
        lifecycle = self.lifecycle.create(
            proposal,
            validation,
            ttl_minutes=ttl_minutes,
        )
        return DraftingPlanCreated(proposal, validation, lifecycle)

    def create_preview(
        self,
        run_id: str,
        owner_key: str,
        raw_proposal: dict[str, Any],
        *,
        ttl_minutes: int = 30,
    ) -> DraftingPreviewCreated:
        """Revalidate and return one transient preview bound durably by ID and SHA-256."""
        if not isinstance(ttl_minutes, int) or isinstance(ttl_minutes, bool) or not 5 <= ttl_minutes <= 120:
            raise DraftingContractError("Drafting preview TTL must be 5 through 120 minutes.")
        current = self.lifecycle.get(run_id, owner_key)
        proposal, validation = self._parse_and_validate(raw_proposal)
        if not validation.preview_allowed:
            raise DraftingContractError("Drafting validation does not allow preview.")
        if (
            proposal.proposal_id != current.proposal_id
            or proposal.proposal_sha256 != current.proposal_sha256
            or validation.validation_sha256 != current.validation_sha256
            or proposal.binding.model_sha256 != current.model_sha256
            or proposal.binding.symbol_catalog_sha256 != current.symbol_catalog_sha256
        ):
            raise DraftingContractError("Drafting preview input differs from the durable run.")
        now = self._now()
        run_expires = _parse_timestamp(current.expires_at, "drafting run expires_at")
        proposal_expires = _parse_timestamp(proposal.expires_at, "proposal expires_at")
        expires = min(now + timedelta(minutes=ttl_minutes), run_expires, proposal_expires)
        if now >= expires:
            raise DraftingContractError("Drafting preview has no active lifetime.")
        preview = create_drafting_preview(
            preview_id=self._id_factory(),
            proposal=proposal,
            created_at=_timestamp(now),
            expires_at=_timestamp(expires),
        )
        bound = self.lifecycle.bind_preview(run_id, owner_key, preview)
        return DraftingPreviewCreated(preview, validation, bound)

    def prepare_execution(
        self,
        run_id: str,
        owner_key: str,
        raw_proposal: dict[str, Any],
        *,
        source_path: str,
        source_sha256: str,
    ) -> DraftingExecutionPrepared:
        """Create a separate exact execution review without touching AutoCAD."""
        current = self.lifecycle.get(run_id, owner_key)
        plan = self._execution_plan(
            current,
            raw_proposal,
            source_path=source_path,
            source_sha256=source_sha256,
        )
        reviewed = self.lifecycle.request_execution_review(
            run_id,
            owner_key,
            execution_request_sha256=plan.execution_request_sha256,
        )
        return DraftingExecutionPrepared(plan, reviewed)

    def execute(
        self,
        run_id: str,
        owner_key: str,
        raw_proposal: dict[str, Any],
        *,
        source_path: str,
        source_sha256: str,
    ) -> DraftingExecutionCompleted:
        """Consume one approval and create only a verified scratch drawing."""
        current = self.lifecycle.get(run_id, owner_key)
        plan = self._execution_plan(
            current,
            raw_proposal,
            source_path=source_path,
            source_sha256=source_sha256,
        )
        started = self.lifecycle.begin_execution(
            run_id,
            owner_key,
            execution_request_sha256=plan.execution_request_sha256,
        )
        if started.execution_id is None:
            raise DraftingContractError("Drafting execution identity was not persisted.")
        try:
            artifact = self.autocad.create_electrical_circuit_fragment_scratch(
                plan.source_path,
                symbol_id=plan.symbol_id,
                definition_sha256=plan.definition_sha256,
                placements=plan.placements,
                wire_points=plan.wire_points,
                expected_source_sha256=plan.source_sha256,
                confirmed=True,
            )
            recovery = self.autocad.record_drafting_recovery_evidence(
                artifact,
                drafting_run_id=run_id,
                execution_id=started.execution_id,
                execution_request_sha256=plan.execution_request_sha256,
                preview_sha256=plan.preview_sha256,
                clock=self._clock,
            )
            completed = self.lifecycle.complete_execution(
                run_id,
                owner_key,
                execution_id=started.execution_id,
                autocad_run_id=artifact.run_id,
                source_sha256=artifact.source_sha256,
                output_sha256=artifact.output_sha256,
                recovery_evidence_sha256=recovery.evidence_sha256,
            )
            return DraftingExecutionCompleted(plan, artifact, recovery, completed)
        except Exception:
            self.lifecycle.fail_execution(
                run_id,
                owner_key,
                execution_id=started.execution_id,
            )
            raise

    def _execution_plan(
        self,
        current: DraftingRun,
        raw_proposal: dict[str, Any],
        *,
        source_path: str,
        source_sha256: str,
    ) -> DraftingExecutionPlan:
        proposal, validation, imported = self._parse_validate_and_import(raw_proposal)
        if not validation.preview_allowed:
            raise DraftingContractError("Drafting validation does not allow execution.")
        if (
            proposal.proposal_id != current.proposal_id
            or proposal.proposal_sha256 != current.proposal_sha256
            or validation.validation_sha256 != current.validation_sha256
            or current.preview_id is None
            or current.preview_sha256 is None
        ):
            raise DraftingContractError(
                "Drafting execution input differs from the reviewed preview."
            )
        resolved_source = self.autocad.verify_drawing_source(
            source_path, expected_sha256=source_sha256
        )
        if imported.context.document_name.casefold() != resolved_source.name.casefold():
            raise DraftingContractError(
                "Drafting source filename differs from the reviewed AutoCAD context."
            )
        if not 2 <= len(proposal.devices) <= 8:
            raise DraftingContractError(
                "Supported execution requires two through eight devices."
            )
        if len(proposal.routes) != 1 or len(proposal.circuits) != 1:
            raise DraftingContractError(
                "Supported execution requires exactly one circuit and one route."
            )
        first = proposal.devices[0]
        if any(
            device.symbol_id != first.symbol_id
            or device.symbol_definition_sha256 != first.symbol_definition_sha256
            or device.block_name != first.block_name
            or device.attributes
            or not (device.scale[0] == device.scale[1] == device.scale[2])
            for device in proposal.devices
        ):
            raise DraftingContractError(
                "Supported execution requires one symbol type, uniform scale, and no attributes."
            )
        catalog_symbol = ElectricalSymbolCatalog.load(
            self.autocad.symbol_catalog_path
        ).get(first.symbol_id)
        layer_by_id = {layer.layer_id: layer for layer in proposal.layers}
        if any(
            layer_by_id[device.layer_id].name.casefold()
            != catalog_symbol.insertion_layer.casefold()
            for device in proposal.devices
        ):
            raise DraftingContractError(
                "Device layers differ from the approved symbol insertion layer."
            )
        route = proposal.routes[0]
        route_layer = layer_by_id[route.layer_id]
        if (
            route_layer.name.casefold() != "e-wire"
            or route_layer.color_index != 3
            or route_layer.linetype.casefold() != "continuous"
        ):
            raise DraftingContractError(
                "Supported execution requires the exact E-WIRE layer policy."
            )
        circuit = proposal.circuits[0]
        if circuit.route_ids != (route.route_id,) or route.circuit_id != circuit.circuit_id:
            raise DraftingContractError("Circuit and route identities are inconsistent.")
        terminals = {terminal.terminal_id: terminal for terminal in proposal.terminals}
        if set(circuit.terminal_ids) != set(terminals):
            raise DraftingContractError(
                "Supported execution requires all proposed terminals in one circuit."
            )
        if {item.device_id for item in terminals.values()} != {
            item.device_id for item in proposal.devices
        }:
            raise DraftingContractError(
                "Supported execution requires one connected terminal per device."
            )
        placements = tuple(
            CircuitPlacement(
                device.insertion_point,
                device.rotation_degrees,
                device.scale[0],
            )
            for device in proposal.devices
        )
        payload = {
            "schema_version": 1,
            "run_id": current.run_id,
            "proposal_sha256": proposal.proposal_sha256,
            "validation_sha256": validation.validation_sha256,
            "preview_id": current.preview_id,
            "preview_sha256": current.preview_sha256,
            "source_path": str(resolved_source),
            "source_sha256": source_sha256,
            "symbol_id": first.symbol_id,
            "definition_sha256": first.symbol_definition_sha256,
            "placements": [item.to_dict() for item in placements],
            "wire_points": [list(point) for point in route.points],
        }
        request_sha256 = hashlib.sha256(
            json.dumps(
                payload,
                ensure_ascii=True,
                separators=(",", ":"),
                sort_keys=True,
            ).encode("utf-8")
        ).hexdigest()
        return DraftingExecutionPlan(
            current.run_id,
            proposal.proposal_sha256,
            validation.validation_sha256,
            current.preview_id,
            current.preview_sha256,
            resolved_source,
            source_sha256,
            first.symbol_id,
            first.symbol_definition_sha256,
            placements,
            route.points,
            request_sha256,
        )

    def _parse_and_validate(
        self, raw_proposal: dict[str, Any]
    ) -> tuple[DraftingProposal, DraftingValidationResult]:
        proposal, validation, _imported = self._parse_validate_and_import(raw_proposal)
        return proposal, validation

    def _parse_validate_and_import(
        self, raw_proposal: dict[str, Any]
    ) -> tuple[DraftingProposal, DraftingValidationResult, Any]:
        proposal = drafting_proposal_from_dict(raw_proposal)
        imported = self.autocad.import_drawing_context(
            context_id=proposal.binding.context_id,
            evidence_sha256=proposal.binding.context_evidence_sha256,
            confirmed=True,
        )
        if imported.read_only is not True or imported.provider_egress_performed is not False:
            raise DraftingContractError(
                "Drafting planning requires read-only local context import."
            )
        validation = validate_drafting_proposal(
            proposal,
            model=imported.model,
            symbol_catalog_path=self.autocad.symbol_catalog_path,
        )
        return proposal, validation, imported

    def _now(self) -> datetime:
        value = self._clock()
        if not isinstance(value, datetime) or value.tzinfo is None:
            raise DraftingContractError("Drafting preview clock must be timezone-aware.")
        return value.astimezone(UTC)


def _timestamp(value: datetime) -> str:
    return value.astimezone(UTC).isoformat().replace("+00:00", "Z")


def _parse_timestamp(value: str, name: str) -> datetime:
    if not isinstance(value, str) or not value.endswith("Z"):
        raise DraftingContractError(f"{name} must be a UTC timestamp.")
    try:
        return datetime.fromisoformat(value[:-1] + "+00:00")
    except ValueError as exc:
        raise DraftingContractError(f"{name} must be a valid UTC timestamp.") from exc
