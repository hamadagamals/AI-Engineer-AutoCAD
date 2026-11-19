"""Versioned connected-load, diversity, and phase-allocation calculations."""

from __future__ import annotations

import hashlib
import json
import math
import re
from dataclasses import dataclass
from enum import Enum
from typing import Any

from agent.exceptions import LoadAggregationError
from core.electrical_drafting import canonical_model_sha256
from core.electrical_model import ElectricalProjectModel, JsonRecord, SourceReference


RULE_SET_VERSION = "m20.load_aggregation.v1"
_IDENTIFIER = re.compile(r"[a-z][a-z0-9_.:-]{0,127}")
_SHA256 = re.compile(r"[0-9a-f]{64}")


class LoadPhase(str, Enum):
    """Explicit allocation of one load across a three-phase source."""

    L1 = "l1"
    L2 = "l2"
    L3 = "l3"
    THREE_PHASE = "three_phase"


@dataclass(frozen=True, slots=True)
class LoadAllocation(JsonRecord):
    """Engineer-reviewed connected load, demand input, and phase assignment."""

    load_object_id: str
    connected_output_power_watts: float
    demand_factor: float
    phase: LoadPhase

    def __post_init__(self) -> None:
        _identifier(self.load_object_id, "load object ID")
        _number(self.connected_output_power_watts, "connected output power", minimum=0.000001)
        _number(self.demand_factor, "demand factor", minimum=0.000001, maximum=1.0)
        if not isinstance(self.phase, LoadPhase):
            raise LoadAggregationError("Load phase assignment is invalid.")


@dataclass(frozen=True, slots=True)
class DiversityGroup(JsonRecord):
    """Explicit diversity factor applied once to a reviewed set of loads."""

    group_id: str
    member_load_object_ids: tuple[str, ...]
    diversity_factor: float

    def __post_init__(self) -> None:
        _identifier(self.group_id, "diversity group ID")
        _identifiers(self.member_load_object_ids, "diversity group members", 1, 256)
        _number(self.diversity_factor, "diversity factor", minimum=0.000001, maximum=1.0)


@dataclass(frozen=True, slots=True)
class LoadAggregationRuleSet(JsonRecord):
    """Complete reviewed rule set for one reproducible aggregation."""

    rule_set_id: str
    rule_set_version: str
    allocations: tuple[LoadAllocation, ...]
    diversity_groups: tuple[DiversityGroup, ...]
    reviewed_by: str
    assumptions: tuple[str, ...]

    def __post_init__(self) -> None:
        _identifier(self.rule_set_id, "rule-set ID")
        if self.rule_set_version != RULE_SET_VERSION:
            raise LoadAggregationError("Unsupported load-aggregation rule-set version.")
        if not isinstance(self.allocations, tuple) or not 1 <= len(self.allocations) <= 256:
            raise LoadAggregationError("Rule set must contain 1 through 256 typed load allocations.")
        if any(not isinstance(item, LoadAllocation) for item in self.allocations):
            raise LoadAggregationError("Rule-set load allocations are invalid.")
        allocation_ids = tuple(item.load_object_id for item in self.allocations)
        if len(allocation_ids) != len(set(allocation_ids)):
            raise LoadAggregationError("Each load object must have exactly one allocation.")
        if not isinstance(self.diversity_groups, tuple) or not self.diversity_groups:
            raise LoadAggregationError("Rule set requires explicit typed diversity groups.")
        if any(not isinstance(item, DiversityGroup) for item in self.diversity_groups):
            raise LoadAggregationError("Rule-set diversity groups are invalid.")
        group_ids = tuple(item.group_id for item in self.diversity_groups)
        if len(group_ids) != len(set(group_ids)):
            raise LoadAggregationError("Diversity group IDs must be unique.")
        grouped = tuple(
            member for group in self.diversity_groups for member in group.member_load_object_ids
        )
        if len(grouped) != len(set(grouped)):
            raise LoadAggregationError("Each load object must belong to exactly one diversity group.")
        if set(grouped) != set(allocation_ids):
            raise LoadAggregationError(
                "Diversity groups must cover every allocated load object exactly once."
            )
        _text(self.reviewed_by, "rule-set reviewer", 128)
        if not isinstance(self.assumptions, tuple) or not 1 <= len(self.assumptions) <= 16:
            raise LoadAggregationError("Rule-set assumptions must contain 1 through 16 entries.")
        for assumption in self.assumptions:
            _text(assumption, "rule-set assumption", 512)


@dataclass(frozen=True, slots=True)
class DiversityGroupResult(JsonRecord):
    group_id: str
    connected_output_power_watts: float
    demand_before_diversity_watts: float
    diversity_factor: float
    demand_after_diversity_watts: float

    def __post_init__(self) -> None:
        _identifier(self.group_id, "diversity result group ID")
        _number(self.connected_output_power_watts, "group connected power", minimum=0.0)
        _number(self.demand_before_diversity_watts, "group demand before diversity", minimum=0.0)
        _number(self.diversity_factor, "group diversity factor", minimum=0.000001, maximum=1.0)
        _number(self.demand_after_diversity_watts, "group demand after diversity", minimum=0.0)


@dataclass(frozen=True, slots=True)
class PhaseLoadResult(JsonRecord):
    phase: LoadPhase
    connected_output_power_watts: float
    demand_output_power_watts: float
    deviation_from_average_watts: float
    deviation_from_average_percent: float

    def __post_init__(self) -> None:
        if self.phase not in (LoadPhase.L1, LoadPhase.L2, LoadPhase.L3):
            raise LoadAggregationError("Phase result must identify L1, L2, or L3.")
        for value, name in (
            (self.connected_output_power_watts, "phase connected power"),
            (self.demand_output_power_watts, "phase demand power"),
            (self.deviation_from_average_watts, "phase demand deviation"),
            (self.deviation_from_average_percent, "phase demand deviation percent"),
        ):
            _number(value, name, minimum=0.0)


@dataclass(frozen=True, slots=True)
class LoadAggregationResult(JsonRecord):
    """Self-hashed aggregation output with exact source and rule-set traceability."""

    schema_version: int
    project_id: str
    document_id: str
    context_id: str
    context_evidence_sha256: str
    model_sha256: str
    rule_set: LoadAggregationRuleSet
    load_sources: tuple[SourceReference, ...]
    connected_output_power_watts: float
    demand_before_diversity_watts: float
    demand_after_diversity_watts: float
    diversity_group_results: tuple[DiversityGroupResult, ...]
    phase_results: tuple[PhaseLoadResult, ...]
    phase_average_demand_watts: float
    phase_maximum_spread_watts: float
    phase_maximum_deviation_percent: float
    formulas: tuple[str, ...]
    warnings: tuple[str, ...]
    compliance_evaluation_performed: bool
    drawing_mutation_performed: bool
    result_sha256: str

    def __post_init__(self) -> None:
        if self.schema_version != 1:
            raise LoadAggregationError("Load-aggregation result schema must be 1.")
        _identifier(self.project_id, "result project ID")
        _identifier(self.document_id, "result document ID")
        if re.fullmatch(r"[0-9a-f]{32}", self.context_id) is None:
            raise LoadAggregationError("Load-aggregation context ID is invalid.")
        for value, name in (
            (self.context_evidence_sha256, "context evidence hash"),
            (self.model_sha256, "model hash"),
            (self.result_sha256, "result hash"),
        ):
            if _SHA256.fullmatch(value) is None:
                raise LoadAggregationError(f"Load-aggregation {name} must be SHA-256.")
        if not isinstance(self.rule_set, LoadAggregationRuleSet):
            raise LoadAggregationError("Load-aggregation result rule set is invalid.")
        if len(self.load_sources) != len(self.rule_set.allocations) or any(
            not isinstance(item, SourceReference) for item in self.load_sources
        ):
            raise LoadAggregationError("Load-aggregation source links are incomplete.")
        for value, name in (
            (self.connected_output_power_watts, "connected power"),
            (self.demand_before_diversity_watts, "demand before diversity"),
            (self.demand_after_diversity_watts, "demand after diversity"),
            (self.phase_average_demand_watts, "phase average demand"),
            (self.phase_maximum_spread_watts, "phase maximum spread"),
            (self.phase_maximum_deviation_percent, "phase maximum deviation"),
        ):
            _number(value, name, minimum=0.0)
        if len(self.phase_results) != 3 or tuple(item.phase for item in self.phase_results) != (
            LoadPhase.L1, LoadPhase.L2, LoadPhase.L3
        ):
            raise LoadAggregationError("Load-aggregation result requires ordered L1/L2/L3 results.")
        if not self.diversity_group_results or any(
            not isinstance(item, DiversityGroupResult) for item in self.diversity_group_results
        ):
            raise LoadAggregationError("Load-aggregation group results are invalid.")
        if len(self.formulas) != 3 or any(not isinstance(item, str) for item in self.formulas):
            raise LoadAggregationError("Load-aggregation formulas are incomplete.")
        if len(self.warnings) < 2 or any(not isinstance(item, str) for item in self.warnings):
            raise LoadAggregationError("Load-aggregation audit details are incomplete.")
        for formula in self.formulas:
            _text(formula, "load-aggregation formula", 512)
        for warning in self.warnings:
            _text(warning, "load-aggregation warning", 512)
        if self.compliance_evaluation_performed is not False:
            raise LoadAggregationError("Load aggregation cannot claim compliance evaluation.")
        if self.drawing_mutation_performed is not False:
            raise LoadAggregationError("Load aggregation cannot claim drawing mutation.")
        value = self.to_dict()
        del value["result_sha256"]
        if self.result_sha256 != _canonical_sha256(value):
            raise LoadAggregationError("Load-aggregation result SHA-256 does not match its content.")


def aggregate_loads(
    model: ElectricalProjectModel,
    rule_set: LoadAggregationRuleSet,
    *,
    context_evidence_sha256: str,
) -> LoadAggregationResult:
    """Apply explicit demand, diversity, and phase-allocation inputs without code limits."""
    if not isinstance(model, ElectricalProjectModel) or not isinstance(
        rule_set, LoadAggregationRuleSet
    ):
        raise LoadAggregationError("Load aggregation requires a typed model and rule set.")
    if _SHA256.fullmatch(context_evidence_sha256) is None:
        raise LoadAggregationError("Context evidence hash must be SHA-256.")
    if model.provenance.evidence_sha256 != context_evidence_sha256:
        raise LoadAggregationError("Model provenance does not match context evidence.")
    sources = {
        **{item.device_id: item.source for item in model.devices},
        **{item.load_id: item.source for item in model.loads if item.source is not None},
    }
    allocation_ids = tuple(item.load_object_id for item in rule_set.allocations)
    missing = sorted(set(allocation_ids) - sources.keys())
    if missing:
        raise LoadAggregationError(
            f"Rule set references an unknown or unsourced load object: {missing[0]}."
        )

    allocations = {item.load_object_id: item for item in rule_set.allocations}
    connected_total = _normalized(math.fsum(item.connected_output_power_watts for item in rule_set.allocations))
    before_total = _normalized(math.fsum(
        item.connected_output_power_watts * item.demand_factor for item in rule_set.allocations
    ))
    group_results: list[DiversityGroupResult] = []
    final_by_load: dict[str, float] = {}
    for group in rule_set.diversity_groups:
        members = [allocations[item] for item in group.member_load_object_ids]
        connected = _normalized(math.fsum(item.connected_output_power_watts for item in members))
        before = _normalized(math.fsum(
            item.connected_output_power_watts * item.demand_factor for item in members
        ))
        after = _normalized(before * group.diversity_factor)
        group_results.append(DiversityGroupResult(
            group.group_id, connected, before, group.diversity_factor, after
        ))
        for member in members:
            final_by_load[member.load_object_id] = _normalized(
                member.connected_output_power_watts * member.demand_factor * group.diversity_factor
            )
    after_total = _normalized(math.fsum(final_by_load.values()))

    phase_connected = {phase: 0.0 for phase in (LoadPhase.L1, LoadPhase.L2, LoadPhase.L3)}
    phase_demand = dict(phase_connected)
    for allocation in rule_set.allocations:
        if allocation.phase is LoadPhase.THREE_PHASE:
            targets = (LoadPhase.L1, LoadPhase.L2, LoadPhase.L3)
            divisor = 3.0
        else:
            targets = (allocation.phase,)
            divisor = 1.0
        for phase in targets:
            phase_connected[phase] += allocation.connected_output_power_watts / divisor
            phase_demand[phase] += final_by_load[allocation.load_object_id] / divisor
    average = _normalized(after_total / 3.0)
    phase_results = tuple(
        PhaseLoadResult(
            phase,
            _normalized(phase_connected[phase]),
            _normalized(phase_demand[phase]),
            _normalized(abs(phase_demand[phase] - average)),
            _normalized(100.0 * abs(phase_demand[phase] - average) / average),
        )
        for phase in (LoadPhase.L1, LoadPhase.L2, LoadPhase.L3)
    )
    values = [phase_demand[phase] for phase in (LoadPhase.L1, LoadPhase.L2, LoadPhase.L3)]
    payload: dict[str, Any] = {
        "schema_version": 1,
        "project_id": model.project_id,
        "document_id": model.document_id,
        "context_id": model.context_id,
        "context_evidence_sha256": context_evidence_sha256,
        "model_sha256": canonical_model_sha256(model),
        "rule_set": rule_set,
        "load_sources": tuple(sources[item] for item in allocation_ids),
        "connected_output_power_watts": connected_total,
        "demand_before_diversity_watts": before_total,
        "demand_after_diversity_watts": after_total,
        "diversity_group_results": tuple(group_results),
        "phase_results": phase_results,
        "phase_average_demand_watts": average,
        "phase_maximum_spread_watts": _normalized(max(values) - min(values)),
        "phase_maximum_deviation_percent": _normalized(max(
            item.deviation_from_average_percent for item in phase_results
        )),
        "formulas": (
            "demand_before_diversity=sum(connected_output_power*demand_factor)",
            "demand_after_diversity=sum(group_demand_before_diversity*diversity_factor)",
            "phase_deviation_percent=abs(phase_demand-average_phase_demand)/average_phase_demand*100",
        ),
        "warnings": (
            "Demand and diversity factors are reviewed project inputs, not inferred code requirements.",
            "Phase imbalance is a connected power allocation metric; no current, voltage, harmonic, neutral, or standards-compliance limit is evaluated.",
        ),
        "compliance_evaluation_performed": False,
        "drawing_mutation_performed": False,
    }
    return LoadAggregationResult(**payload, result_sha256=_canonical_sha256(payload))


def _canonical_sha256(value: object) -> str:
    def convert(item: object) -> object:
        if isinstance(item, Enum):
            return item.value
        if isinstance(item, JsonRecord):
            return item.to_dict()
        if isinstance(item, tuple):
            return [convert(child) for child in item]
        if isinstance(item, dict):
            return {str(key): convert(child) for key, child in item.items()}
        return item
    encoded = json.dumps(convert(value), ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest()


def _normalized(value: float) -> float:
    normalized = round(float(value), 12)
    return 0.0 if normalized == 0.0 else normalized


def _identifier(value: str, name: str) -> None:
    if not isinstance(value, str) or _IDENTIFIER.fullmatch(value) is None:
        raise LoadAggregationError(f"{name} must be a bounded lowercase identifier.")


def _identifiers(values: tuple[str, ...], name: str, minimum: int, maximum: int) -> None:
    if not isinstance(values, tuple) or not minimum <= len(values) <= maximum:
        raise LoadAggregationError(f"{name} must contain {minimum} through {maximum} identifiers.")
    for value in values:
        _identifier(value, name)
    if len(values) != len(set(values)):
        raise LoadAggregationError(f"{name} must be unique.")


def _text(value: str, name: str, maximum: int) -> None:
    if not isinstance(value, str) or not value or len(value.encode("utf-8")) > maximum:
        raise LoadAggregationError(f"{name} must be non-empty and at most {maximum} bytes.")
    if any(ord(character) < 32 and character != "\t" for character in value):
        raise LoadAggregationError(f"{name} contains unsupported control characters.")


def _number(value: float, name: str, *, minimum: float, maximum: float = 1_000_000_000.0) -> None:
    if (
        not isinstance(value, (int, float))
        or isinstance(value, bool)
        or not math.isfinite(value)
        or not minimum <= float(value) <= maximum
    ):
        raise LoadAggregationError(f"{name} is outside its supported numeric range.")
