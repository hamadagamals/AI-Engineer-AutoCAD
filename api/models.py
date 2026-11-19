"""Strict request and response models for the protected local API."""

from __future__ import annotations

from typing import Any, Annotated

from pydantic import BaseModel, ConfigDict, Field

from agent.permissions import ApprovalRecord
from agent.copilot import CopilotAnswer, CopilotSession, DataEgressReview
from agent.developer_agent import DeveloperRun, ProposedAction
from agent.task_engine import TaskRecord
from agent.drafting_service import DraftingRun
from core.electrical_drafting import DraftingPreview
from core.electrical_drafting_validation import DraftingValidationResult

SafeIdentifier = Annotated[str, Field(min_length=1, max_length=128)]
SafePath = Annotated[str, Field(min_length=1, max_length=4_096)]


class StrictModel(BaseModel):
    """Forbid coercion and unrecognized API input fields."""

    model_config = ConfigDict(extra="forbid", strict=True)


class HealthResponse(StrictModel):
    status: str
    version: str


class StatusResponse(StrictModel):
    status: str
    version: str
    registered_commands: int
    task_counts: dict[str, int]


class CommandResponse(StrictModel):
    name: str
    description: str
    permission: str
    risk: str
    timeout_seconds: int


class CommandListResponse(StrictModel):
    commands: list[CommandResponse]


class TaskSubmitRequest(StrictModel):
    command_name: Annotated[
        str,
        Field(
            min_length=3,
            max_length=128,
            pattern=r"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$",
        ),
    ]
    payload: dict[str, Any] = Field(default_factory=dict)
    max_attempts: Annotated[int, Field(ge=1, le=3)] = 1


class TaskRunRequest(StrictModel):
    approval_id: SafeIdentifier | None = None


class TaskResponse(StrictModel):
    task_id: str
    command_name: str
    status: str
    attempts: int
    max_attempts: int
    created_at: str
    updated_at: str
    approval_id: str | None
    result: Any
    error: str | None

    @classmethod
    def from_record(cls, record: TaskRecord) -> TaskResponse:
        """Build a response without exposing the submitted payload."""
        return cls(
            task_id=record.task_id,
            command_name=record.command_name,
            status=record.status.value,
            attempts=record.attempts,
            max_attempts=record.max_attempts,
            created_at=record.created_at,
            updated_at=record.updated_at,
            approval_id=record.approval_id,
            result=record.result,
            error=record.error,
        )


class TaskListResponse(StrictModel):
    tasks: list[TaskResponse]


class ApprovalDecisionRequest(StrictModel):
    approved: bool


class ApprovalResponse(StrictModel):
    approval_id: str
    task_id: str
    command_name: str
    status: str
    requested_at: str
    expires_at: str
    decided_at: str | None
    consumed_at: str | None

    @classmethod
    def from_record(cls, record: ApprovalRecord) -> ApprovalResponse:
        """Build a public approval response."""
        return cls(**record.to_dict())


class FileReadRequest(StrictModel):
    path: SafePath


class FileWriteRequest(StrictModel):
    path: SafePath
    content: Annotated[str, Field(max_length=524_288)]
    create_parents: bool = False


class FileDeleteRequest(StrictModel):
    path: SafePath


class ScanRequest(StrictModel):
    path: SafePath


class DeveloperStatusResponse(StrictModel):
    configured: bool
    provider_name: str | None


class DeveloperRunCreateRequest(StrictModel):
    goal: Annotated[str, Field(min_length=1, max_length=16_384)]
    maximum_actions: Annotated[int, Field(ge=1, le=10)] = 5


class DeveloperReviewRequest(StrictModel):
    approved: bool


class ProposedActionResponse(StrictModel):
    action_id: str
    command_name: str
    payload: dict[str, Any]
    rationale: str
    risk: str
    status: str
    task_id: str | None
    approval_id: str | None

    @classmethod
    def from_record(cls, record: ProposedAction) -> ProposedActionResponse:
        """Expose the exact proposal required for informed local review."""
        return cls(**record.to_dict())


class DeveloperRunResponse(StrictModel):
    run_id: str
    provider_name: str
    goal: str
    status: str
    plan_summary: str | None
    plan_steps: list[str]
    action_count: int
    maximum_actions: int
    provider_calls: int
    maximum_provider_calls: int
    created_at: str
    updated_at: str
    pending_action: ProposedActionResponse | None
    last_task_id: str | None
    last_verification_accepted: bool | None
    last_verification_reason: str | None
    completion_summary: str | None
    error: str | None
    recent_history: list[dict[str, Any]]

    @classmethod
    def from_record(cls, record: DeveloperRun) -> DeveloperRunResponse:
        """Build a protected response including material human-review details."""
        return cls(
            run_id=record.run_id,
            provider_name=record.provider_name,
            goal=record.goal,
            status=record.status.value,
            plan_summary=record.plan_summary,
            plan_steps=list(record.plan_steps),
            action_count=record.action_count,
            maximum_actions=record.maximum_actions,
            provider_calls=record.provider_calls,
            maximum_provider_calls=record.maximum_provider_calls,
            created_at=record.created_at,
            updated_at=record.updated_at,
            pending_action=(
                ProposedActionResponse.from_record(record.pending_action)
                if record.pending_action
                else None
            ),
            last_task_id=record.last_task_id,
            last_verification_accepted=record.last_verification_accepted,
            last_verification_reason=record.last_verification_reason,
            completion_summary=record.completion_summary,
            error=record.error,
            recent_history=[dict(item) for item in record.recent_history],
        )


class DeveloperRunListResponse(StrictModel):
    runs: list[DeveloperRunResponse]


class CopilotSessionCreateRequest(StrictModel):
    context_id: Annotated[str, Field(pattern=r"^[0-9a-f]{32}$")]
    evidence_sha256: Annotated[str, Field(pattern=r"^[0-9a-f]{64}$")]
    language: Annotated[str, Field(pattern=r"^(ar|en)$")]
    ttl_minutes: Annotated[int, Field(ge=5, le=120)] = 60


class GeneralCopilotSessionCreateRequest(StrictModel):
    language: Annotated[str, Field(pattern=r"^(ar|en)$")]
    ttl_minutes: Annotated[int, Field(ge=5, le=120)] = 60


class CopilotMessageCreateRequest(StrictModel):
    language: Annotated[str, Field(pattern=r"^(ar|en)$")]
    content: Annotated[str, Field(min_length=1, max_length=16_384)]


class CopilotSessionResponse(StrictModel):
    session: dict[str, Any]

    @classmethod
    def from_record(cls, record: CopilotSession) -> CopilotSessionResponse:
        return cls(session=record.to_dict())


class CopilotSessionCreatedResponse(CopilotSessionResponse):
    owner_key: Annotated[str, Field(min_length=32, max_length=128)]


class EgressFieldRequest(StrictModel):
    field_name: Annotated[
        str,
        Field(pattern=r"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*){0,7}$"),
    ]
    value: Annotated[str, Field(min_length=1, max_length=65_536)]


class EgressReviewCreateRequest(StrictModel):
    destination: Annotated[
        str, Field(pattern=r"^[a-z][a-z0-9_.:-]{0,127}$")
    ]
    privacy: Annotated[
        str, Field(pattern=r"^(project_confidential|internal|public)$")
    ]
    fields: Annotated[list[EgressFieldRequest], Field(min_length=1, max_length=32)]
    ttl_minutes: Annotated[int, Field(ge=1, le=30)] = 10


class EgressReviewDecisionRequest(StrictModel):
    approved: bool


class EgressReviewResponse(StrictModel):
    review: dict[str, Any]

    @classmethod
    def from_record(cls, record: DataEgressReview) -> EgressReviewResponse:
        return cls(review=record.to_dict())


class CopilotProviderStatusResponse(StrictModel):
    configured: bool
    provider_name: str | None


class CopilotAnswerCreateRequest(StrictModel):
    review_id: Annotated[str, Field(pattern=r"^[0-9a-f]{32}$")]


class CopilotAnswerResponse(StrictModel):
    answer: dict[str, Any]
    session: dict[str, Any] | None = None

    @classmethod
    def from_record(
        cls, record: CopilotAnswer, session: CopilotSession | None = None
    ) -> CopilotAnswerResponse:
        return cls(
            answer=record.to_dict(),
            session=session.to_dict() if session is not None else None,
        )


class DraftingPlanCreateRequest(StrictModel):
    proposal: dict[str, Any]
    ttl_minutes: Annotated[int, Field(ge=5, le=120)] = 60


class RouteLengthOverrideRequest(StrictModel):
    route_id: SafeIdentifier
    final_total_meters: Annotated[float, Field(ge=0.0, le=1_000_000_000.0)]
    reason: Annotated[str, Field(min_length=1, max_length=512)]
    reviewed_by: Annotated[str, Field(min_length=1, max_length=128)]


class TakeoffPolicyRequest(StrictModel):
    calculation_version: Annotated[str, Field(pattern=r"^m19\.takeoff\.v1$")] = (
        "m19.takeoff.v1"
    )
    vertical_allowance_per_route_meters: Annotated[
        float, Field(ge=0.0, le=1_000_000_000.0)
    ] = 0.0
    termination_allowance_per_end_meters: Annotated[
        float, Field(ge=0.0, le=1_000_000_000.0)
    ] = 0.0
    waste_percent: Annotated[float, Field(ge=0.0, le=100.0)] = 0.0
    route_overrides: Annotated[
        list[RouteLengthOverrideRequest], Field(max_length=4096)
    ] = Field(default_factory=list)


class TakeoffCreateRequest(StrictModel):
    context_id: Annotated[str, Field(pattern=r"^[0-9a-f]{32}$")]
    context_evidence_sha256: Annotated[str, Field(pattern=r"^[0-9a-f]{64}$")]
    policy: TakeoffPolicyRequest = Field(default_factory=TakeoffPolicyRequest)


class TakeoffResponse(StrictModel):
    report: dict[str, Any]


class TakeoffCsvExportRequest(TakeoffCreateRequest):
    reviewed_report_sha256: Annotated[str, Field(pattern=r"^[0-9a-f]{64}$")]


class TakeoffCsvExportResponse(StrictModel):
    export: dict[str, Any]


class PanelSldExtractionCreateRequest(StrictModel):
    context_id: Annotated[str, Field(pattern=r"^[0-9a-f]{32}$")]
    context_evidence_sha256: Annotated[str, Field(pattern=r"^[0-9a-f]{64}$")]
    reviewed_by: Annotated[str, Field(min_length=1, max_length=128)]
    assumptions: Annotated[
        list[Annotated[str, Field(min_length=1, max_length=512)]],
        Field(min_length=1, max_length=16),
    ]


class PanelSldExtractionResponse(StrictModel):
    extraction: dict[str, Any]
    schedule: dict[str, Any] | None


class ComplianceEvaluationCreateRequest(StrictModel):
    evaluation_id: Annotated[str, Field(pattern=r"^[a-z][a-z0-9_.:-]{0,127}$")]
    profile: dict[str, Any]
    clauses: Annotated[list[dict[str, Any]], Field(max_length=256)]
    rules: Annotated[list[dict[str, Any]], Field(min_length=1, max_length=256)]
    facts: Annotated[list[dict[str, Any]], Field(max_length=512)]
    evaluated_by: Annotated[str, Field(min_length=1, max_length=128)]
    evaluated_on: Annotated[str, Field(pattern=r"^\d{4}-\d{2}-\d{2}$")]
    assumptions: Annotated[
        list[Annotated[str, Field(min_length=1, max_length=512)]],
        Field(min_length=1, max_length=16),
    ]


class ComplianceEvaluationResponse(StrictModel):
    profile: dict[str, Any]
    clauses: list[dict[str, Any]]
    result: dict[str, Any]


class ManufacturerReviewCreateRequest(StrictModel):
    review_id: Annotated[str, Field(pattern=r"^[a-z][a-z0-9_.:-]{0,127}$")]
    scope: Annotated[
        str,
        Field(pattern=r"^(evidence|quarantined_artifact|symbol_candidate|catalog_registration)$"),
    ]
    bundle: dict[str, Any]
    retrieval_request: dict[str, Any] | None = None
    quarantine: dict[str, Any] | None = None
    model_binding: dict[str, Any] | None = None
    symbol_candidate: dict[str, Any] | None = None
    catalog_registration: dict[str, Any] | None = None
    require_model_binding: bool = False
    reviewed_by: Annotated[str, Field(min_length=1, max_length=128)]
    reviewed_on: Annotated[str, Field(pattern=r"^\d{4}-\d{2}-\d{2}$")]
    assumptions: Annotated[
        list[Annotated[str, Field(min_length=1, max_length=512)]],
        Field(min_length=1, max_length=16),
    ]


class ManufacturerReviewResponse(StrictModel):
    bundle: dict[str, Any]
    retrieval_request: dict[str, Any] | None
    quarantine: dict[str, Any] | None
    model_binding: dict[str, Any] | None
    symbol_candidate: dict[str, Any] | None
    catalog_registration: dict[str, Any] | None
    result: dict[str, Any]


class ManufacturerDiscoveryCreateRequest(StrictModel):
    request: dict[str, Any]
    discovery_id: Annotated[str, Field(pattern=r"^[a-z][a-z0-9_.:-]{0,127}$")]
    reviewed_by: Annotated[str, Field(min_length=1, max_length=128)]
    reviewed_on: Annotated[str, Field(pattern=r"^\d{4}-\d{2}-\d{2}$")]
    assumptions: Annotated[
        list[Annotated[str, Field(min_length=1, max_length=512)]],
        Field(min_length=1, max_length=16),
    ]


class ManufacturerManualDiscoveryCreateRequest(ManufacturerDiscoveryCreateRequest):
    candidate_draft: dict[str, Any]


class ManufacturerDiscoveryResponse(StrictModel):
    request: dict[str, Any]
    result: dict[str, Any]


class ManufacturerCandidateSelectionCreateRequest(StrictModel):
    result: dict[str, Any]
    selection_id: Annotated[str, Field(pattern=r"^[a-z][a-z0-9_.:-]{0,127}$")]
    candidate_id: Annotated[str, Field(pattern=r"^[a-z][a-z0-9_.:-]{0,127}$")]
    selected_by: Annotated[str, Field(min_length=1, max_length=128)]
    selected_on: Annotated[str, Field(pattern=r"^\d{4}-\d{2}-\d{2}$")]
    rationale: Annotated[str, Field(min_length=1, max_length=512)]


class ManufacturerCandidateSelectionResponse(StrictModel):
    selection: dict[str, Any]


class ManufacturerAssetDiscoveryCreateRequest(StrictModel):
    request: dict[str, Any]
    result_id: Annotated[str, Field(pattern=r"^[a-z][a-z0-9_.:-]{0,127}$")]


class ManufacturerAssetDiscoveryResponse(StrictModel):
    request: dict[str, Any]
    result: dict[str, Any]


class ManufacturerAssetCollectionCreateRequest(StrictModel):
    request: dict[str, Any]
    result: dict[str, Any]
    approval_id: Annotated[str, Field(pattern=r"^[a-z][a-z0-9_.:-]{0,127}$")]
    selected_candidate_ids: Annotated[
        list[Annotated[str, Field(pattern=r"^[a-z][a-z0-9_.:-]{0,127}$")]],
        Field(min_length=1, max_length=16),
    ]
    approved_by: Annotated[str, Field(min_length=1, max_length=128)]
    approved_on: Annotated[str, Field(pattern=r"^\d{4}-\d{2}-\d{2}$")]
    maximum_bytes_per_asset: Annotated[int, Field(ge=1024, le=100_000_000)] = 50_000_000
    download_approved: bool


class ManufacturerAssetCollectionResponse(StrictModel):
    approval: dict[str, Any]
    collection: dict[str, Any]


class LowVoltageCalculationInputRequest(StrictModel):
    calculation_id: Annotated[
        str, Field(pattern=r"^[a-z][a-z0-9_.:-]{0,127}$")
    ]
    calculation_version: Annotated[
        str, Field(pattern=r"^m20\.lv_current_voltage_drop\.v1$")
    ] = "m20.lv_current_voltage_drop.v1"
    phase_system: Annotated[
        str, Field(pattern=r"^(single_phase|balanced_three_phase)$")
    ]
    load_output_power_watts: Annotated[float, Field(gt=0.0, le=1_000_000_000.0)]
    demand_factor: Annotated[float, Field(gt=0.0, le=1.0)]
    nominal_voltage_volts: Annotated[float, Field(gt=0.0, le=1_000_000_000.0)]
    power_factor: Annotated[float, Field(gt=0.0, le=1.0)]
    efficiency: Annotated[float, Field(gt=0.0, le=1.0)]
    conductor_ac_resistance_ohm_per_km: Annotated[
        float, Field(ge=0.0, le=1_000_000_000.0)
    ]
    conductor_reactance_ohm_per_km: Annotated[
        float, Field(ge=0.0, le=1_000_000_000.0)
    ]
    load_object_ids: Annotated[list[SafeIdentifier], Field(min_length=1, max_length=256)]
    route_ids: Annotated[list[SafeIdentifier], Field(min_length=1, max_length=256)]
    reviewed_by: Annotated[str, Field(min_length=1, max_length=128)]
    assumptions: Annotated[
        list[Annotated[str, Field(min_length=1, max_length=512)]],
        Field(min_length=1, max_length=16),
    ]


class LowVoltageCalculationCreateRequest(StrictModel):
    context_id: Annotated[str, Field(pattern=r"^[0-9a-f]{32}$")]
    context_evidence_sha256: Annotated[str, Field(pattern=r"^[0-9a-f]{64}$")]
    calculation: LowVoltageCalculationInputRequest


class LowVoltageCalculationResponse(StrictModel):
    result: dict[str, Any]


class LoadAllocationRequest(StrictModel):
    load_object_id: Annotated[str, Field(pattern=r"^[a-z][a-z0-9_.:-]{0,127}$")]
    connected_output_power_watts: Annotated[float, Field(gt=0.0, le=1_000_000_000.0)]
    demand_factor: Annotated[float, Field(gt=0.0, le=1.0)]
    phase: Annotated[str, Field(pattern=r"^(l1|l2|l3|three_phase)$")]


class DiversityGroupRequest(StrictModel):
    group_id: Annotated[str, Field(pattern=r"^[a-z][a-z0-9_.:-]{0,127}$")]
    member_load_object_ids: Annotated[
        list[SafeIdentifier], Field(min_length=1, max_length=256)
    ]
    diversity_factor: Annotated[float, Field(gt=0.0, le=1.0)]


class LoadAggregationRuleSetRequest(StrictModel):
    rule_set_id: Annotated[str, Field(pattern=r"^[a-z][a-z0-9_.:-]{0,127}$")]
    rule_set_version: Annotated[
        str, Field(pattern=r"^m20\.load_aggregation\.v1$")
    ] = "m20.load_aggregation.v1"
    allocations: Annotated[list[LoadAllocationRequest], Field(min_length=1, max_length=256)]
    diversity_groups: Annotated[
        list[DiversityGroupRequest], Field(min_length=1, max_length=256)
    ]
    reviewed_by: Annotated[str, Field(min_length=1, max_length=128)]
    assumptions: Annotated[
        list[Annotated[str, Field(min_length=1, max_length=512)]],
        Field(min_length=1, max_length=16),
    ]


class LoadAggregationCreateRequest(StrictModel):
    context_id: Annotated[str, Field(pattern=r"^[0-9a-f]{32}$")]
    context_evidence_sha256: Annotated[str, Field(pattern=r"^[0-9a-f]{64}$")]
    rule_set: LoadAggregationRuleSetRequest


class LoadAggregationResponse(StrictModel):
    result: dict[str, Any]


class DeratingFactorRequest(StrictModel):
    factor_id: Annotated[str, Field(pattern=r"^[a-z][a-z0-9_.:-]{0,127}$")]
    label: Annotated[str, Field(min_length=1, max_length=128)]
    factor: Annotated[float, Field(gt=0.0, le=1.0)]
    basis_reference: Annotated[str, Field(min_length=1, max_length=512)]


class CableAmpacityInputRequest(StrictModel):
    calculation_id: Annotated[str, Field(pattern=r"^[a-z][a-z0-9_.:-]{0,127}$")]
    calculation_version: Annotated[
        str, Field(pattern=r"^m20\.cable_ampacity_derating\.v1$")
    ] = "m20.cable_ampacity_derating.v1"
    source_route_id: Annotated[str, Field(pattern=r"^[a-z][a-z0-9_.:-]{0,127}$")]
    cable_designation: Annotated[str, Field(min_length=1, max_length=256)]
    base_ampacity_amperes: Annotated[float, Field(gt=0.0, le=1_000_000_000.0)]
    base_ampacity_basis_reference: Annotated[str, Field(min_length=1, max_length=512)]
    derating_factors: Annotated[
        list[DeratingFactorRequest], Field(min_length=1, max_length=16)
    ]
    reviewed_by: Annotated[str, Field(min_length=1, max_length=128)]
    assumptions: Annotated[
        list[Annotated[str, Field(min_length=1, max_length=512)]],
        Field(min_length=1, max_length=16),
    ]


class CableAmpacityCreateRequest(StrictModel):
    context_id: Annotated[str, Field(pattern=r"^[0-9a-f]{32}$")]
    context_evidence_sha256: Annotated[str, Field(pattern=r"^[0-9a-f]{64}$")]
    calculation: CableAmpacityInputRequest


class CableAmpacityResponse(StrictModel):
    result: dict[str, Any]


class ShortCircuitInputRequest(StrictModel):
    calculation_id: Annotated[str, Field(pattern=r"^[a-z][a-z0-9_.:-]{0,127}$")]
    calculation_version: Annotated[
        str, Field(pattern=r"^m20\.short_circuit_source_equivalent\.v1$")
    ] = "m20.short_circuit_source_equivalent.v1"
    source_route_id: Annotated[str, Field(pattern=r"^[a-z][a-z0-9_.:-]{0,127}$")]
    fault_arrangement: Annotated[
        str, Field(pattern=r"^(single_phase_loop|balanced_three_phase)$")
    ]
    nominal_voltage_volts: Annotated[float, Field(gt=0.0, le=1_000_000_000.0)]
    voltage_multiplier: Annotated[float, Field(gt=0.0, le=10.0)]
    source_resistance_ohms: Annotated[float, Field(ge=0.0, le=1_000_000_000.0)]
    source_reactance_ohms: Annotated[float, Field(ge=0.0, le=1_000_000_000.0)]
    path_resistance_ohms: Annotated[float, Field(ge=0.0, le=1_000_000_000.0)]
    path_reactance_ohms: Annotated[float, Field(ge=0.0, le=1_000_000_000.0)]
    impedance_basis_reference: Annotated[str, Field(min_length=1, max_length=512)]
    reviewed_by: Annotated[str, Field(min_length=1, max_length=128)]
    assumptions: Annotated[
        list[Annotated[str, Field(min_length=1, max_length=512)]],
        Field(min_length=1, max_length=16),
    ]


class ShortCircuitCreateRequest(StrictModel):
    context_id: Annotated[str, Field(pattern=r"^[0-9a-f]{32}$")]
    context_evidence_sha256: Annotated[str, Field(pattern=r"^[0-9a-f]{64}$")]
    calculation: ShortCircuitInputRequest


class ShortCircuitResponse(StrictModel):
    result: dict[str, Any]


class CandidateAdequacyRuleSetRequest(StrictModel):
    rule_set_id: Annotated[str, Field(pattern=r"^[a-z][a-z0-9_.:-]{0,127}$")]
    rule_set_version: Annotated[
        str, Field(pattern=r"^m20\.candidate_adequacy\.v1$")
    ] = "m20.candidate_adequacy.v1"
    candidate_id: Annotated[str, Field(pattern=r"^[a-z][a-z0-9_.:-]{0,127}$")]
    protective_device_designation: Annotated[str, Field(min_length=1, max_length=256)]
    protective_device_rated_current_amperes: Annotated[
        float, Field(gt=0.0, le=1_000_000_000.0)
    ]
    protective_device_breaking_capacity_amperes: Annotated[
        float, Field(gt=0.0, le=1_000_000_000.0)
    ]
    required_ampacity_multiplier: Annotated[float, Field(ge=1.0, le=10.0)]
    maximum_voltage_drop_percent: Annotated[float, Field(gt=0.0, le=100.0)]
    basis_reference: Annotated[str, Field(min_length=1, max_length=512)]
    reviewed_by: Annotated[str, Field(min_length=1, max_length=128)]
    assumptions: Annotated[
        list[Annotated[str, Field(min_length=1, max_length=512)]],
        Field(min_length=1, max_length=16),
    ]


class CandidateAdequacyCreateRequest(StrictModel):
    context_id: Annotated[str, Field(pattern=r"^[0-9a-f]{32}$")]
    context_evidence_sha256: Annotated[str, Field(pattern=r"^[0-9a-f]{64}$")]
    low_voltage: LowVoltageCalculationInputRequest
    ampacity: CableAmpacityInputRequest
    short_circuit: ShortCircuitInputRequest
    rule_set: CandidateAdequacyRuleSetRequest


class CandidateAdequacyResponse(StrictModel):
    result: dict[str, Any]


class ProtectiveConductorRuleInputRequest(StrictModel):
    calculation_id: Annotated[str, Field(pattern=r"^[a-z][a-z0-9_.:-]{0,127}$")]
    calculation_version: Annotated[
        str, Field(pattern=r"^m20\.protective_conductor_adiabatic\.v1$")
    ] = "m20.protective_conductor_adiabatic.v1"
    source_route_id: Annotated[str, Field(pattern=r"^[a-z][a-z0-9_.:-]{0,127}$")]
    applicability: Annotated[
        str, Field(pattern=r"^(applicable|not_applicable|unknown)$")
    ]
    applicability_basis_reference: Annotated[str, Field(min_length=1, max_length=512)]
    disconnection_basis: Annotated[
        str,
        Field(
            pattern=(
                r"^(reviewed_protective_device_time|"
                r"reviewed_upstream_clearing_time|unknown)$"
            )
        ),
    ]
    disconnection_basis_reference: Annotated[str, Field(min_length=1, max_length=512)]
    disconnection_time_seconds: Annotated[
        float, Field(gt=0.0, le=1_000_000_000.0)
    ] | None = None
    conductor_factor_amperes_sqrt_second_per_square_millimeter: Annotated[
        float, Field(gt=0.0, le=1_000_000_000.0)
    ] | None = None
    rule_source_id: Annotated[str, Field(pattern=r"^[a-z][a-z0-9_.:-]{0,127}$")]
    rule_source_title: Annotated[str, Field(min_length=1, max_length=256)]
    rule_source_version: Annotated[str, Field(min_length=1, max_length=128)]
    rule_source_locator: Annotated[str, Field(min_length=1, max_length=512)]
    rule_source_sha256: Annotated[str, Field(pattern=r"^[0-9a-f]{64}$")]
    reviewed_by: Annotated[str, Field(min_length=1, max_length=128)]
    assumptions: Annotated[
        list[Annotated[str, Field(min_length=1, max_length=512)]],
        Field(min_length=1, max_length=16),
    ]


class ProtectiveConductorSizingCreateRequest(StrictModel):
    context_id: Annotated[str, Field(pattern=r"^[0-9a-f]{32}$")]
    context_evidence_sha256: Annotated[str, Field(pattern=r"^[0-9a-f]{64}$")]
    rule_input: ProtectiveConductorRuleInputRequest
    short_circuit: ShortCircuitInputRequest | None = None


class ProtectiveConductorSizingResponse(StrictModel):
    result: dict[str, Any]


class DraftingPreviewCreateRequest(StrictModel):
    proposal: dict[str, Any]
    ttl_minutes: Annotated[int, Field(ge=5, le=120)] = 30


class DraftingReviewDecisionRequest(StrictModel):
    approved: bool


class DraftingExecutionRequest(StrictModel):
    proposal: dict[str, Any]
    source_path: SafePath
    source_sha256: Annotated[str, Field(pattern=r"^[0-9a-f]{64}$")]


class DraftingRunResponse(StrictModel):
    run: dict[str, Any]
    validation: dict[str, Any] | None = None

    @classmethod
    def from_record(
        cls,
        record: DraftingRun,
        validation: DraftingValidationResult | None = None,
    ) -> DraftingRunResponse:
        return cls(
            run=record.to_dict(),
            validation=validation.to_dict() if validation is not None else None,
        )


class DraftingRunCreatedResponse(DraftingRunResponse):
    owner_key: Annotated[str, Field(min_length=32, max_length=128)]


class DraftingPreviewResponse(DraftingRunResponse):
    preview: dict[str, Any]

    @classmethod
    def from_preview(
        cls,
        record: DraftingRun,
        preview: DraftingPreview,
        validation: DraftingValidationResult,
    ) -> DraftingPreviewResponse:
        return cls(
            run=record.to_dict(),
            validation=validation.to_dict(),
            preview=preview.to_dict(),
        )


class DraftingExecutionReviewResponse(DraftingRunResponse):
    execution: dict[str, Any]


class DraftingExecutionResponse(DraftingExecutionReviewResponse):
    artifact: dict[str, Any]
    recovery: dict[str, Any]
