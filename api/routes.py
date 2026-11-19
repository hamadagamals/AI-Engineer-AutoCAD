"""Authenticated localhost API routes backed by the task engine."""

from __future__ import annotations

import secrets
import logging
import hashlib
from collections import Counter
from typing import Annotated, Awaitable, Callable

from fastapi import APIRouter, Depends, Header, Query
from starlette.concurrency import run_in_threadpool

from agent.app import Application
from agent.copilot import (
    ContextBinding,
    ContextScope,
    ContextSummary,
    CopilotLanguage,
    create_egress_field,
)
from agent.exceptions import ApiConfigurationError, ManufacturerEvidenceError
from core.electrical_model import PrivacyClassification
from core.quantity_takeoff import (
    RouteLengthOverride,
    TakeoffPolicy,
    create_takeoff_report,
)
from core.quantity_export import create_reviewed_csv_export
from core.low_voltage_calculations import (
    LowVoltageCalculationInput,
    PhaseSystem,
    calculate_low_voltage,
)
from core.load_aggregation import (
    DiversityGroup,
    LoadAggregationRuleSet,
    LoadAllocation,
    LoadPhase,
    aggregate_loads,
)
from core.cable_ampacity import (
    CableAmpacityInput,
    DeratingFactorInput,
    calculate_derated_ampacity,
)
from core.short_circuit import (
    FaultArrangement,
    ShortCircuitInput,
    calculate_short_circuit,
)
from core.candidate_adequacy import (
    CandidateAdequacyRuleSet,
    evaluate_candidate_adequacy,
)
from core.protective_conductor import (
    DisconnectionBasis,
    ProtectiveConductorRuleInput,
    RuleApplicability,
    size_protective_conductor,
)
from core.panel_sld_extraction import extract_panel_sld_observations
from core.panel_schedule import generate_panel_schedule
from core.code_profile import parse_project_code_profile
from core.compliance_evidence import (
    evaluate_compliance,
    parse_clause_evidence,
    parse_compliance_rule,
    parse_engineering_fact,
)
from core.manufacturer_evidence import parse_manufacturer_evidence_bundle
from core.manufacturer_discovery import (
    compare_candidates,
    parse_discovery_request,
    parse_discovery_result,
    select_candidate_for_review,
)
from core.manufacturer_asset_collection import (
    approve_asset_collection,
    collect_approved_assets,
    create_asset_search_result,
    parse_asset_search_request,
    parse_asset_search_result,
)
from integrations.manufacturer_retrieval import (
    parse_quarantine_record,
    parse_retrieval_request,
)
from integrations.manufacturer_mapping import (
    ManufacturerReviewScope,
    parse_catalog_registration,
    parse_model_binding,
    parse_symbol_candidate,
    review_manufacturer_package,
)
from integrations.openai_manufacturer_search import (
    verify_official_candidate_draft,
    verify_saved_candidate_draft,
)
from agent.version import __version__
from api.models import (
    ApprovalDecisionRequest,
    ApprovalResponse,
    CommandListResponse,
    CommandResponse,
    CopilotMessageCreateRequest,
    CopilotAnswerCreateRequest,
    CopilotAnswerResponse,
    CopilotProviderStatusResponse,
    CopilotSessionCreateRequest,
    GeneralCopilotSessionCreateRequest,
    CopilotSessionCreatedResponse,
    CopilotSessionResponse,
    DeveloperReviewRequest,
    DeveloperRunCreateRequest,
    DeveloperRunListResponse,
    DeveloperRunResponse,
    DeveloperStatusResponse,
    EgressReviewCreateRequest,
    EgressReviewDecisionRequest,
    EgressReviewResponse,
    DraftingPlanCreateRequest,
    DraftingExecutionRequest,
    DraftingExecutionResponse,
    DraftingExecutionReviewResponse,
    DraftingPreviewCreateRequest,
    DraftingPreviewResponse,
    DraftingReviewDecisionRequest,
    DraftingRunCreatedResponse,
    DraftingRunResponse,
    FileDeleteRequest,
    FileReadRequest,
    FileWriteRequest,
    HealthResponse,
    ScanRequest,
    StatusResponse,
    TaskListResponse,
    TaskResponse,
    TaskRunRequest,
    TaskSubmitRequest,
    TakeoffCreateRequest,
    TakeoffCsvExportRequest,
    TakeoffCsvExportResponse,
    TakeoffResponse,
    LowVoltageCalculationCreateRequest,
    LowVoltageCalculationResponse,
    LoadAggregationCreateRequest,
    LoadAggregationResponse,
    CableAmpacityCreateRequest,
    CableAmpacityResponse,
    ShortCircuitCreateRequest,
    ShortCircuitResponse,
    CandidateAdequacyCreateRequest,
    CandidateAdequacyResponse,
    ProtectiveConductorSizingCreateRequest,
    ProtectiveConductorSizingResponse,
    PanelSldExtractionCreateRequest,
    PanelSldExtractionResponse,
    ComplianceEvaluationCreateRequest,
    ComplianceEvaluationResponse,
    ManufacturerReviewCreateRequest,
    ManufacturerReviewResponse,
    ManufacturerDiscoveryCreateRequest,
    ManufacturerManualDiscoveryCreateRequest,
    ManufacturerDiscoveryResponse,
    ManufacturerCandidateSelectionCreateRequest,
    ManufacturerCandidateSelectionResponse,
    ManufacturerAssetDiscoveryCreateRequest,
    ManufacturerAssetDiscoveryResponse,
    ManufacturerAssetCollectionCreateRequest,
    ManufacturerAssetCollectionResponse,
)


class BearerAuthenticator:
    """Constant-time Bearer token dependency that never logs the credential."""

    def __init__(self, token: str) -> None:
        self._token = validate_api_token(token)

    async def __call__(
        self,
        authorization: Annotated[str | None, Header()] = None,
    ) -> None:
        if authorization is None or not authorization.startswith("Bearer "):
            _raise_unauthorized()
        supplied = authorization[7:]
        if not supplied or not secrets.compare_digest(supplied, self._token):
            _raise_unauthorized()

    def matches_bytes(self, value: bytes) -> bool:
        """Validate one raw Authorization header without exposing token state."""
        if not value.startswith(b"Bearer "):
            return False
        try:
            supplied = value[7:].decode("ascii")
        except UnicodeDecodeError:
            return False
        return bool(supplied) and secrets.compare_digest(supplied, self._token)


def validate_api_token(token: str) -> str:
    """Require a strong, single-line API token supplied outside the repository."""
    if not isinstance(token, str) or len(token) < 32:
        raise ApiConfigurationError("API token must contain at least 32 characters.")
    if token != token.strip() or any(character.isspace() for character in token):
        raise ApiConfigurationError("API token must be a single token without whitespace.")
    if len(set(token)) < 12:
        raise ApiConfigurationError("API token does not contain enough character diversity.")
    return token


def public_router(application: Application) -> APIRouter:
    """Create the minimal unauthenticated health router."""
    router = APIRouter()

    @router.get("/health", response_model=HealthResponse)
    async def health() -> HealthResponse:
        return HealthResponse(status="ok", version=__version__)

    return router


def protected_router(
    application: Application, authenticate: BearerAuthenticator
) -> APIRouter:
    """Create all authenticated versioned routes."""
    router = APIRouter(prefix="/v1", dependencies=[Depends(authenticate)])

    @router.get("/status", response_model=StatusResponse)
    async def status() -> StatusResponse:
        tasks = _tasks(application).list_tasks(limit=500)
        counts = Counter(task.status.value for task in tasks)
        registry = _registry(application)
        return StatusResponse(
            status=application.status.value,
            version=__version__,
            registered_commands=len(registry.list_definitions()),
            task_counts=dict(counts),
        )

    @router.get("/commands", response_model=CommandListResponse)
    async def commands() -> CommandListResponse:
        definitions = _registry(application).list_definitions()
        return CommandListResponse(
            commands=[
                CommandResponse(
                    name=item.name,
                    description=item.description,
                    permission=item.permission,
                    risk=item.risk.name.lower(),
                    timeout_seconds=item.timeout_seconds,
                )
                for item in definitions
            ]
        )

    @router.post("/tasks", response_model=TaskResponse, status_code=201)
    async def submit_task(request: TaskSubmitRequest) -> TaskResponse:
        record = await run_in_threadpool(
            _tasks(application).submit,
            request.command_name,
            request.payload,
            max_attempts=request.max_attempts,
        )
        return TaskResponse.from_record(record)

    @router.get("/tasks", response_model=TaskListResponse)
    async def list_tasks(
        limit: Annotated[int, Query(strict=True, ge=1, le=500)] = 100,
    ) -> TaskListResponse:
        records = await run_in_threadpool(_tasks(application).list_tasks, limit=limit)
        return TaskListResponse(tasks=[TaskResponse.from_record(item) for item in records])

    @router.get("/tasks/{task_id}", response_model=TaskResponse)
    async def get_task(task_id: str) -> TaskResponse:
        record = await run_in_threadpool(_tasks(application).get, task_id)
        return TaskResponse.from_record(record)

    @router.post("/tasks/{task_id}/run", response_model=TaskResponse)
    async def run_task(task_id: str, request: TaskRunRequest) -> TaskResponse:
        record = await run_in_threadpool(
            _tasks(application).run, task_id, approval_id=request.approval_id
        )
        return TaskResponse.from_record(record)

    @router.post("/tasks/{task_id}/cancel", response_model=TaskResponse)
    async def cancel_task(task_id: str) -> TaskResponse:
        record = await run_in_threadpool(_tasks(application).cancel, task_id)
        return TaskResponse.from_record(record)

    @router.get("/approvals/{approval_id}", response_model=ApprovalResponse)
    async def get_approval(approval_id: str) -> ApprovalResponse:
        record = await run_in_threadpool(_approvals(application).get, approval_id)
        return ApprovalResponse.from_record(record)

    @router.post("/approvals/{approval_id}/decision", response_model=ApprovalResponse)
    async def decide_approval(
        approval_id: str, request: ApprovalDecisionRequest
    ) -> ApprovalResponse:
        record = await run_in_threadpool(
            _approvals(application).decide, approval_id, approved=request.approved
        )
        return ApprovalResponse.from_record(record)

    @router.post("/files/read", response_model=TaskResponse)
    async def read_file(request: FileReadRequest) -> TaskResponse:
        return await _submit_and_run(application, "file.read", request.model_dump())

    @router.post("/files/write", response_model=TaskResponse)
    async def write_file(request: FileWriteRequest) -> TaskResponse:
        return await _submit_and_run(application, "file.write", request.model_dump())

    @router.post("/files/delete", response_model=TaskResponse)
    async def delete_file(request: FileDeleteRequest) -> TaskResponse:
        return await _submit_and_run(application, "file.delete", request.model_dump())

    @router.post("/scans", response_model=TaskResponse)
    async def scan_project(request: ScanRequest) -> TaskResponse:
        return await _submit_and_run(application, "project.scan", request.model_dump())

    @router.post(
        "/copilot/sessions",
        response_model=CopilotSessionCreatedResponse,
        status_code=201,
    )
    async def create_copilot_session(
        request: CopilotSessionCreateRequest,
    ) -> CopilotSessionCreatedResponse:
        imported = await run_in_threadpool(
            _autocad(application).import_drawing_context,
            context_id=request.context_id,
            evidence_sha256=request.evidence_sha256,
            confirmed=True,
        )
        created = await run_in_threadpool(
            _copilot(application).create_session,
            project_id=imported.model.project_id,
            context=ContextBinding(
                imported.context.context_id,
                imported.evidence_sha256,
                ContextScope(imported.context.source_mode),
            ),
            context_summary=ContextSummary(
                entity_count=len(imported.context.entities),
                layer_count=len(imported.context.layers),
                device_count=len(imported.model.devices),
                route_count=len(imported.model.routes),
                unknown_count=len(imported.model.unknown_objects),
                warning_count=len(imported.model.warnings),
                truncated=imported.context.truncated,
                database_modified=imported.context.database_modified,
                block_reference_count=sum(
                    entity.entity_type == "INSERT" for entity in imported.context.entities
                ),
                route_primitive_count=sum(
                    entity.entity_type in {"LINE", "LWPOLYLINE"}
                    for entity in imported.context.entities
                ),
            ),
            language=CopilotLanguage(request.language),
            ttl_minutes=request.ttl_minutes,
        )
        return CopilotSessionCreatedResponse(
            session=created.session.to_dict(), owner_key=created.owner_key
        )

    @router.post(
        "/copilot/general-sessions",
        response_model=CopilotSessionCreatedResponse,
        status_code=201,
    )
    async def create_general_copilot_session(
        request: GeneralCopilotSessionCreateRequest,
    ) -> CopilotSessionCreatedResponse:
        """Create a bounded chat session with no drawing context or drawing authority."""
        evidence_sha256 = hashlib.sha256(
            b"ai-engineer-general-chat-context-v1"
        ).hexdigest()
        created = await run_in_threadpool(
            _copilot(application).create_session,
            project_id="autocad.general",
            context=ContextBinding(
                evidence_sha256[:32],
                evidence_sha256,
                ContextScope.GENERAL,
            ),
            context_summary=ContextSummary(
                entity_count=0,
                layer_count=0,
                device_count=0,
                route_count=0,
                unknown_count=0,
                warning_count=0,
                truncated=False,
                database_modified=0,
            ),
            language=CopilotLanguage(request.language),
            ttl_minutes=request.ttl_minutes,
        )
        return CopilotSessionCreatedResponse(
            session=created.session.to_dict(), owner_key=created.owner_key
        )

    @router.get(
        "/copilot/provider-status", response_model=CopilotProviderStatusResponse
    )
    async def copilot_provider_status() -> CopilotProviderStatusResponse:
        provider = _copilot(application).provider
        return CopilotProviderStatusResponse(
            configured=provider is not None,
            provider_name=provider.name if provider is not None else None,
        )

    @router.get(
        "/copilot/sessions/{session_id}", response_model=CopilotSessionResponse
    )
    async def get_copilot_session(
        session_id: str,
        owner_key: Annotated[
            str,
            Header(
                alias="X-AI-Engineer-Session-Key",
                min_length=32,
                max_length=128,
            ),
        ],
    ) -> CopilotSessionResponse:
        session = await run_in_threadpool(
            _copilot(application).get_session, session_id, owner_key
        )
        return CopilotSessionResponse.from_record(session)

    @router.post(
        "/copilot/sessions/{session_id}/messages",
        response_model=CopilotSessionResponse,
    )
    async def add_copilot_message(
        session_id: str,
        request: CopilotMessageCreateRequest,
        owner_key: Annotated[
            str,
            Header(
                alias="X-AI-Engineer-Session-Key",
                min_length=32,
                max_length=128,
            ),
        ],
    ) -> CopilotSessionResponse:
        session = await run_in_threadpool(
            _copilot(application).add_engineer_message,
            session_id,
            owner_key,
            language=CopilotLanguage(request.language),
            content=request.content,
        )
        return CopilotSessionResponse.from_record(session)

    @router.post(
        "/copilot/sessions/{session_id}/cancel",
        response_model=CopilotSessionResponse,
    )
    async def cancel_copilot_session(
        session_id: str,
        owner_key: Annotated[
            str,
            Header(
                alias="X-AI-Engineer-Session-Key",
                min_length=32,
                max_length=128,
            ),
        ],
    ) -> CopilotSessionResponse:
        session = await run_in_threadpool(
            _copilot(application).cancel_session, session_id, owner_key
        )
        return CopilotSessionResponse.from_record(session)

    @router.post(
        "/copilot/sessions/{session_id}/egress-reviews",
        response_model=EgressReviewResponse,
        status_code=201,
    )
    async def create_copilot_egress_review(
        session_id: str,
        request: EgressReviewCreateRequest,
        owner_key: Annotated[
            str,
            Header(
                alias="X-AI-Engineer-Session-Key",
                min_length=32,
                max_length=128,
            ),
        ],
    ) -> EgressReviewResponse:
        review = await run_in_threadpool(
            _copilot(application).create_review,
            session_id,
            owner_key,
            destination=request.destination,
            privacy=PrivacyClassification(request.privacy),
            fields=tuple(
                create_egress_field(field.field_name, field.value)
                for field in request.fields
            ),
            ttl_minutes=request.ttl_minutes,
        )
        return EgressReviewResponse.from_record(review)

    @router.get(
        "/copilot/sessions/{session_id}/egress-reviews/{review_id}",
        response_model=EgressReviewResponse,
    )
    async def get_copilot_egress_review(
        session_id: str,
        review_id: str,
        owner_key: Annotated[
            str,
            Header(
                alias="X-AI-Engineer-Session-Key",
                min_length=32,
                max_length=128,
            ),
        ],
    ) -> EgressReviewResponse:
        review = await run_in_threadpool(
            _copilot(application).get_review, session_id, owner_key, review_id
        )
        return EgressReviewResponse.from_record(review)

    @router.post(
        "/copilot/sessions/{session_id}/egress-reviews/{review_id}/decision",
        response_model=EgressReviewResponse,
    )
    async def decide_copilot_egress_review(
        session_id: str,
        review_id: str,
        request: EgressReviewDecisionRequest,
        owner_key: Annotated[
            str,
            Header(
                alias="X-AI-Engineer-Session-Key",
                min_length=32,
                max_length=128,
            ),
        ],
    ) -> EgressReviewResponse:
        review = await run_in_threadpool(
            _copilot(application).decide_review,
            session_id,
            owner_key,
            review_id,
            approved=request.approved,
        )
        return EgressReviewResponse.from_record(review)

    @router.post(
        "/copilot/sessions/{session_id}/answers",
        response_model=CopilotAnswerResponse,
        status_code=201,
    )
    async def request_copilot_answer(
        session_id: str,
        request: CopilotAnswerCreateRequest,
        owner_key: Annotated[
            str,
            Header(
                alias="X-AI-Engineer-Session-Key",
                min_length=32,
                max_length=128,
            ),
        ],
    ) -> CopilotAnswerResponse:
        if _copilot(application).provider is None:
            raise ApiConfigurationError("Copilot provider is not configured.")
        session, answer = await run_in_threadpool(
            _copilot(application).request_answer,
            session_id,
            owner_key,
            request.review_id,
        )
        return CopilotAnswerResponse.from_record(answer, session)

    @router.get(
        "/copilot/sessions/{session_id}/answers/{answer_id}",
        response_model=CopilotAnswerResponse,
    )
    async def get_copilot_answer(
        session_id: str,
        answer_id: str,
        owner_key: Annotated[
            str,
            Header(
                alias="X-AI-Engineer-Session-Key",
                min_length=32,
                max_length=128,
            ),
        ],
    ) -> CopilotAnswerResponse:
        answer = await run_in_threadpool(
            _copilot(application).get_answer, session_id, owner_key, answer_id
        )
        return CopilotAnswerResponse.from_record(answer)

    @router.post(
        "/takeoff/reports",
        response_model=TakeoffResponse,
        status_code=201,
    )
    async def create_quantity_takeoff(
        request: TakeoffCreateRequest,
    ) -> TakeoffResponse:
        imported = await run_in_threadpool(
            _autocad(application).import_drawing_context,
            context_id=request.context_id,
            evidence_sha256=request.context_evidence_sha256,
            confirmed=True,
        )
        policy = _takeoff_policy(request)
        report = await run_in_threadpool(
            create_takeoff_report,
            imported.model,
            context_evidence_sha256=request.context_evidence_sha256,
            policy=policy,
        )
        return TakeoffResponse(report=report.to_dict())

    @router.post(
        "/takeoff/exports/csv",
        response_model=TakeoffCsvExportResponse,
        status_code=201,
    )
    async def create_quantity_csv_export(
        request: TakeoffCsvExportRequest,
    ) -> TakeoffCsvExportResponse:
        imported = await run_in_threadpool(
            _autocad(application).import_drawing_context,
            context_id=request.context_id,
            evidence_sha256=request.context_evidence_sha256,
            confirmed=True,
        )
        report = await run_in_threadpool(
            create_takeoff_report,
            imported.model,
            context_evidence_sha256=request.context_evidence_sha256,
            policy=_takeoff_policy(request),
        )
        exported = await run_in_threadpool(
            create_reviewed_csv_export,
            report,
            reviewed_report_sha256=request.reviewed_report_sha256,
        )
        return TakeoffCsvExportResponse(export=exported.to_dict())

    @router.post(
        "/panels/extract-schedule",
        response_model=PanelSldExtractionResponse,
        status_code=201,
    )
    async def extract_existing_panel_sld(
        request: PanelSldExtractionCreateRequest,
    ) -> PanelSldExtractionResponse:
        imported = await run_in_threadpool(
            _autocad(application).import_drawing_context,
            context_id=request.context_id,
            evidence_sha256=request.context_evidence_sha256,
            confirmed=True,
        )
        extraction = await run_in_threadpool(
            extract_panel_sld_observations,
            imported.context,
            imported.model,
            context_evidence_sha256=request.context_evidence_sha256,
            reviewed_by=request.reviewed_by,
            assumptions=tuple(request.assumptions),
        )
        schedule = None
        if extraction.complete:
            if extraction.semantic_model is None or extraction.contract is None:
                raise ApiConfigurationError(
                    "Complete panel/SLD extraction omitted its semantic result."
                )
            schedule = await run_in_threadpool(
                generate_panel_schedule,
                extraction.semantic_model,
                extraction.contract,
            )
        logging.getLogger("ai_engineer.panels").info(
            "panel_sld_extraction_completed",
            extra={
                "context_id": request.context_id,
                "result_sha256": extraction.result_sha256,
                "complete": extraction.complete,
                "finding_count": len(extraction.findings),
                "provider_egress_performed": False,
                "drawing_mutation_performed": False,
            },
        )
        return PanelSldExtractionResponse(
            extraction=extraction.to_dict(),
            schedule=schedule.to_dict() if schedule is not None else None,
        )

    @router.post(
        "/compliance/evaluate",
        response_model=ComplianceEvaluationResponse,
        status_code=201,
    )
    async def create_compliance_evaluation(
        request: ComplianceEvaluationCreateRequest,
    ) -> ComplianceEvaluationResponse:
        profile = await run_in_threadpool(parse_project_code_profile, request.profile)
        parsed_clauses = []
        for item in request.clauses:
            parsed_clauses.append(await run_in_threadpool(parse_clause_evidence, item))
        clauses = tuple(parsed_clauses)
        parsed_rules = []
        for item in request.rules:
            parsed_rules.append(await run_in_threadpool(parse_compliance_rule, item))
        rules = tuple(parsed_rules)
        parsed_facts = []
        for item in request.facts:
            parsed_facts.append(await run_in_threadpool(parse_engineering_fact, item))
        facts = tuple(parsed_facts)
        result = await run_in_threadpool(
            evaluate_compliance,
            profile,
            evaluation_id=request.evaluation_id,
            clauses=clauses,
            rules=rules,
            facts=facts,
            evaluated_by=request.evaluated_by,
            evaluated_on=request.evaluated_on,
            assumptions=tuple(request.assumptions),
        )
        logging.getLogger("ai_engineer.compliance").info(
            "compliance_evaluation_completed",
            extra={
                "evaluation_id": request.evaluation_id,
                "profile_id": profile.profile_id,
                "profile_sha256": profile.profile_sha256,
                "result_sha256": result.result_sha256,
                "complete": result.complete,
                "all_requirements_satisfied": result.all_requirements_satisfied,
                "provider_retrieval_performed": False,
                "regulatory_approval_performed": False,
                "drawing_mutation_performed": False,
            },
        )
        return ComplianceEvaluationResponse(
            profile=profile.to_dict(),
            clauses=[item.to_dict() for item in clauses],
            result=result.to_dict(),
        )

    @router.post(
        "/manufacturers/review",
        response_model=ManufacturerReviewResponse,
        status_code=201,
    )
    async def create_manufacturer_review(
        request: ManufacturerReviewCreateRequest,
    ) -> ManufacturerReviewResponse:
        bundle = await run_in_threadpool(
            parse_manufacturer_evidence_bundle, request.bundle
        )
        retrieval = (
            await run_in_threadpool(parse_retrieval_request, request.retrieval_request)
            if request.retrieval_request is not None
            else None
        )
        quarantine = (
            await run_in_threadpool(parse_quarantine_record, request.quarantine)
            if request.quarantine is not None
            else None
        )
        binding = (
            await run_in_threadpool(parse_model_binding, request.model_binding)
            if request.model_binding is not None
            else None
        )
        candidate = (
            await run_in_threadpool(parse_symbol_candidate, request.symbol_candidate)
            if request.symbol_candidate is not None
            else None
        )
        registration = (
            await run_in_threadpool(
                parse_catalog_registration, request.catalog_registration
            )
            if request.catalog_registration is not None
            else None
        )
        result = await run_in_threadpool(
            review_manufacturer_package,
            bundle,
            review_id=request.review_id,
            scope=ManufacturerReviewScope(request.scope),
            retrieval_request=retrieval,
            quarantine=quarantine,
            model_binding=binding,
            symbol_candidate=candidate,
            catalog_registration=registration,
            require_model_binding=request.require_model_binding,
            reviewed_by=request.reviewed_by,
            reviewed_on=request.reviewed_on,
            assumptions=tuple(request.assumptions),
        )
        logging.getLogger("ai_engineer.manufacturers").info(
            "manufacturer_package_reviewed",
            extra={
                "review_id": request.review_id,
                "scope": request.scope,
                "bundle_sha256": bundle.bundle_sha256,
                "result_sha256": result.result_sha256,
                "complete": result.complete,
                "provider_egress_performed": False,
                "retrieval_performed_by_review": False,
                "catalog_registration_performed_by_review": False,
                "drawing_mutation_performed": False,
            },
        )
        return ManufacturerReviewResponse(
            bundle=bundle.to_dict(),
            retrieval_request=retrieval.to_dict() if retrieval else None,
            quarantine=quarantine.to_dict() if quarantine else None,
            model_binding=binding.to_dict() if binding else None,
            symbol_candidate=candidate.to_dict() if candidate else None,
            catalog_registration=registration.to_dict() if registration else None,
            result=result.to_dict(),
        )

    @router.post(
        "/manufacturers/discover",
        response_model=ManufacturerDiscoveryResponse,
        status_code=201,
    )
    async def discover_manufacturer_products(
        request: ManufacturerDiscoveryCreateRequest,
    ) -> ManufacturerDiscoveryResponse:
        provider = application.manufacturer_provider
        if provider is None:
            raise ApiConfigurationError("Manufacturer discovery provider is not configured.")
        discovery_request = await run_in_threadpool(
            parse_discovery_request, request.request
        )
        candidates = await run_in_threadpool(provider.discover, discovery_request)
        result = await run_in_threadpool(
            compare_candidates,
            discovery_request,
            candidates,
            discovery_id=request.discovery_id,
            provider_name=provider.name,
            reviewed_by=request.reviewed_by,
            reviewed_on=request.reviewed_on,
            assumptions=tuple(request.assumptions),
        )
        logging.getLogger("ai_engineer.manufacturers").info(
            "manufacturer_discovery_reviewed",
            extra={
                "discovery_id": request.discovery_id,
                "request_sha256": discovery_request.request_sha256,
                "result_sha256": result.result_sha256,
                "candidate_count": len(candidates),
                "provider_name": provider.name,
                "provider_egress_performed": True,
                "automatic_selection_performed": False,
                "automatic_download_performed": False,
                "catalog_registration_performed": False,
                "drawing_mutation_performed": False,
            },
        )
        return ManufacturerDiscoveryResponse(
            request=discovery_request.to_dict(), result=result.to_dict()
        )

    @router.post(
        "/manufacturers/discover/manual",
        response_model=ManufacturerDiscoveryResponse,
        status_code=201,
    )
    async def verify_manual_manufacturer_candidates(
        request: ManufacturerManualDiscoveryCreateRequest,
    ) -> ManufacturerDiscoveryResponse:
        discovery_request = await run_in_threadpool(
            parse_discovery_request, request.request
        )
        raw_candidates = request.candidate_draft.get("candidates")
        saved_mode = isinstance(raw_candidates, list) and any(
            isinstance(item, dict) and "evidence_path" in item for item in raw_candidates
        )
        if saved_mode:
            files = application.files
            if files is None:
                raise ApiConfigurationError("Workspace file service is not initialized.")

            def read_saved_html(path: str) -> bytes:
                content = files.read_bytes(path, maximum_bytes=2_097_152)
                try:
                    content.decode("utf-8")
                except UnicodeDecodeError as exc:
                    raise ManufacturerEvidenceError(
                        "Saved official HTML must be valid UTF-8."
                    ) from exc
                return content

            candidates = await run_in_threadpool(
                verify_saved_candidate_draft,
                request.candidate_draft,
                discovery_request,
                evidence_read=read_saved_html,
            )
            provider_name = "engineer-saved-official-evidence"
        else:
            candidates = await run_in_threadpool(
                verify_official_candidate_draft,
                request.candidate_draft,
                discovery_request,
            )
            provider_name = "manual-official-verification"
        result = await run_in_threadpool(
            compare_candidates,
            discovery_request,
            candidates,
            discovery_id=request.discovery_id,
            provider_name=provider_name,
            reviewed_by=request.reviewed_by,
            reviewed_on=request.reviewed_on,
            assumptions=tuple(request.assumptions),
            provider_egress_performed=False,
        )
        logging.getLogger("ai_engineer.manufacturers").info(
            "manual_manufacturer_candidates_verified",
            extra={
                "discovery_id": request.discovery_id,
                "request_sha256": discovery_request.request_sha256,
                "result_sha256": result.result_sha256,
                "candidate_count": len(candidates),
                "official_source_retrieval_performed": not saved_mode,
                "engineer_saved_evidence_read": saved_mode,
                "provider_egress_performed": False,
                "automatic_selection_performed": False,
                "automatic_download_performed": False,
                "catalog_registration_performed": False,
                "drawing_mutation_performed": False,
            },
        )
        return ManufacturerDiscoveryResponse(
            request=discovery_request.to_dict(), result=result.to_dict()
        )

    @router.post(
        "/manufacturers/discover/select",
        response_model=ManufacturerCandidateSelectionResponse,
        status_code=201,
    )
    async def select_manufacturer_candidate(
        request: ManufacturerCandidateSelectionCreateRequest,
    ) -> ManufacturerCandidateSelectionResponse:
        result = await run_in_threadpool(parse_discovery_result, request.result)
        selection = await run_in_threadpool(
            select_candidate_for_review,
            result,
            selection_id=request.selection_id,
            candidate_id=request.candidate_id,
            selected_by=request.selected_by,
            selected_on=request.selected_on,
            rationale=request.rationale,
        )
        logging.getLogger("ai_engineer.manufacturers").info(
            "manufacturer_candidate_selected_for_review",
            extra={
                "selection_id": selection.selection_id,
                "discovery_result_sha256": selection.discovery_result_sha256,
                "candidate_id": selection.candidate_id,
                "part_number": selection.part_number,
                "selection_sha256": selection.selection_sha256,
                "product_approval_performed": False,
                "automatic_download_performed": False,
                "catalog_registration_performed": False,
                "drawing_mutation_performed": False,
            },
        )
        return ManufacturerCandidateSelectionResponse(selection=selection.to_dict())

    @router.post(
        "/manufacturers/assets/discover",
        response_model=ManufacturerAssetDiscoveryResponse,
        status_code=201,
    )
    async def discover_manufacturer_assets(
        request: ManufacturerAssetDiscoveryCreateRequest,
    ) -> ManufacturerAssetDiscoveryResponse:
        provider = application.manufacturer_provider
        if provider is None:
            raise ApiConfigurationError("Manufacturer asset discovery provider is not configured.")
        search_request = await run_in_threadpool(parse_asset_search_request, request.request)
        candidates = await run_in_threadpool(provider.discover_assets, search_request)
        result = await run_in_threadpool(
            create_asset_search_result, search_request, candidates,
            result_id=request.result_id, provider_name=provider.name,
        )
        logging.getLogger("ai_engineer.manufacturers").info(
            "manufacturer_assets_discovered",
            extra={
                "result_id": result.result_id, "part_number": search_request.part_number,
                "candidate_count": len(candidates), "provider_egress_performed": True,
                "automatic_download_performed": False,
                "catalog_registration_performed": False, "drawing_mutation_performed": False,
            },
        )
        return ManufacturerAssetDiscoveryResponse(
            request=search_request.to_dict(), result=result.to_dict()
        )

    @router.post(
        "/manufacturers/assets/collect",
        response_model=ManufacturerAssetCollectionResponse,
        status_code=201,
    )
    async def collect_manufacturer_assets(
        request: ManufacturerAssetCollectionCreateRequest,
    ) -> ManufacturerAssetCollectionResponse:
        if request.download_approved is not True:
            raise ManufacturerEvidenceError("Manufacturer asset download requires explicit approval.")
        search_request = await run_in_threadpool(parse_asset_search_request, request.request)
        search_result = await run_in_threadpool(parse_asset_search_result, request.result)
        approval = await run_in_threadpool(
            approve_asset_collection, search_result, approval_id=request.approval_id,
            selected_candidate_ids=tuple(request.selected_candidate_ids),
            approved_by=request.approved_by, approved_on=request.approved_on,
            maximum_bytes_per_asset=request.maximum_bytes_per_asset,
        )
        collection = await run_in_threadpool(
            collect_approved_assets, search_request, search_result, approval,
            application.config.workspace_dir / "manufacturer-assets",
            retrieved_on=request.approved_on,
        )
        logging.getLogger("ai_engineer.manufacturers").info(
            "manufacturer_assets_collected",
            extra={
                "collection_id": collection.collection_id,
                "part_number": collection.part_number, "asset_count": len(collection.records),
                "retrieval_performed": True, "quarantine_write_performed": True,
                "catalog_registration_performed": False, "drawing_mutation_performed": False,
            },
        )
        return ManufacturerAssetCollectionResponse(
            approval=approval.to_dict(), collection=collection.to_dict()
        )

    @router.post(
        "/calculations/low-voltage",
        response_model=LowVoltageCalculationResponse,
        status_code=201,
    )
    async def create_low_voltage_calculation(
        request: LowVoltageCalculationCreateRequest,
    ) -> LowVoltageCalculationResponse:
        imported = await run_in_threadpool(
            _autocad(application).import_drawing_context,
            context_id=request.context_id,
            evidence_sha256=request.context_evidence_sha256,
            confirmed=True,
        )
        item = request.calculation
        calculation_input = LowVoltageCalculationInput(
            calculation_id=item.calculation_id,
            calculation_version=item.calculation_version,
            phase_system=PhaseSystem(item.phase_system),
            load_output_power_watts=item.load_output_power_watts,
            demand_factor=item.demand_factor,
            nominal_voltage_volts=item.nominal_voltage_volts,
            power_factor=item.power_factor,
            efficiency=item.efficiency,
            conductor_ac_resistance_ohm_per_km=(
                item.conductor_ac_resistance_ohm_per_km
            ),
            conductor_reactance_ohm_per_km=(
                item.conductor_reactance_ohm_per_km
            ),
            load_object_ids=tuple(item.load_object_ids),
            route_ids=tuple(item.route_ids),
            reviewed_by=item.reviewed_by,
            assumptions=tuple(item.assumptions),
        )
        result = await run_in_threadpool(
            calculate_low_voltage,
            imported.model,
            calculation_input,
            context_evidence_sha256=request.context_evidence_sha256,
        )
        logging.getLogger("ai_engineer.calculations").info(
            "low_voltage_calculation_completed",
            extra={
                "calculation_id": calculation_input.calculation_id,
                "context_id": request.context_id,
                "result_sha256": result.result_sha256,
                "drawing_mutation_performed": False,
                "compliance_evaluation_performed": False,
            },
        )
        return LowVoltageCalculationResponse(result=result.to_dict())

    @router.post(
        "/calculations/load-aggregation",
        response_model=LoadAggregationResponse,
        status_code=201,
    )
    async def create_load_aggregation(
        request: LoadAggregationCreateRequest,
    ) -> LoadAggregationResponse:
        imported = await run_in_threadpool(
            _autocad(application).import_drawing_context,
            context_id=request.context_id,
            evidence_sha256=request.context_evidence_sha256,
            confirmed=True,
        )
        item = request.rule_set
        rule_set = LoadAggregationRuleSet(
            rule_set_id=item.rule_set_id,
            rule_set_version=item.rule_set_version,
            allocations=tuple(
                LoadAllocation(
                    load_object_id=allocation.load_object_id,
                    connected_output_power_watts=allocation.connected_output_power_watts,
                    demand_factor=allocation.demand_factor,
                    phase=LoadPhase(allocation.phase),
                )
                for allocation in item.allocations
            ),
            diversity_groups=tuple(
                DiversityGroup(
                    group_id=group.group_id,
                    member_load_object_ids=tuple(group.member_load_object_ids),
                    diversity_factor=group.diversity_factor,
                )
                for group in item.diversity_groups
            ),
            reviewed_by=item.reviewed_by,
            assumptions=tuple(item.assumptions),
        )
        result = await run_in_threadpool(
            aggregate_loads,
            imported.model,
            rule_set,
            context_evidence_sha256=request.context_evidence_sha256,
        )
        logging.getLogger("ai_engineer.calculations").info(
            "load_aggregation_completed",
            extra={
                "rule_set_id": rule_set.rule_set_id,
                "context_id": request.context_id,
                "result_sha256": result.result_sha256,
                "drawing_mutation_performed": False,
                "compliance_evaluation_performed": False,
            },
        )
        return LoadAggregationResponse(result=result.to_dict())

    @router.post(
        "/calculations/cable-ampacity",
        response_model=CableAmpacityResponse,
        status_code=201,
    )
    async def create_cable_ampacity_calculation(
        request: CableAmpacityCreateRequest,
    ) -> CableAmpacityResponse:
        imported = await run_in_threadpool(
            _autocad(application).import_drawing_context,
            context_id=request.context_id,
            evidence_sha256=request.context_evidence_sha256,
            confirmed=True,
        )
        item = request.calculation
        calculation_input = CableAmpacityInput(
            calculation_id=item.calculation_id,
            calculation_version=item.calculation_version,
            source_route_id=item.source_route_id,
            cable_designation=item.cable_designation,
            base_ampacity_amperes=item.base_ampacity_amperes,
            base_ampacity_basis_reference=item.base_ampacity_basis_reference,
            derating_factors=tuple(
                DeratingFactorInput(
                    factor_id=factor.factor_id,
                    label=factor.label,
                    factor=factor.factor,
                    basis_reference=factor.basis_reference,
                )
                for factor in item.derating_factors
            ),
            reviewed_by=item.reviewed_by,
            assumptions=tuple(item.assumptions),
        )
        result = await run_in_threadpool(
            calculate_derated_ampacity,
            imported.model,
            calculation_input,
            context_evidence_sha256=request.context_evidence_sha256,
        )
        logging.getLogger("ai_engineer.calculations").info(
            "cable_ampacity_derating_completed",
            extra={
                "calculation_id": calculation_input.calculation_id,
                "context_id": request.context_id,
                "result_sha256": result.result_sha256,
                "cable_selection_performed": False,
                "protective_device_selection_performed": False,
                "drawing_mutation_performed": False,
                "compliance_evaluation_performed": False,
            },
        )
        return CableAmpacityResponse(result=result.to_dict())

    @router.post(
        "/calculations/short-circuit",
        response_model=ShortCircuitResponse,
        status_code=201,
    )
    async def create_short_circuit_calculation(
        request: ShortCircuitCreateRequest,
    ) -> ShortCircuitResponse:
        imported = await run_in_threadpool(
            _autocad(application).import_drawing_context,
            context_id=request.context_id,
            evidence_sha256=request.context_evidence_sha256,
            confirmed=True,
        )
        item = request.calculation
        calculation_input = ShortCircuitInput(
            calculation_id=item.calculation_id,
            calculation_version=item.calculation_version,
            source_route_id=item.source_route_id,
            fault_arrangement=FaultArrangement(item.fault_arrangement),
            nominal_voltage_volts=item.nominal_voltage_volts,
            voltage_multiplier=item.voltage_multiplier,
            source_resistance_ohms=item.source_resistance_ohms,
            source_reactance_ohms=item.source_reactance_ohms,
            path_resistance_ohms=item.path_resistance_ohms,
            path_reactance_ohms=item.path_reactance_ohms,
            impedance_basis_reference=item.impedance_basis_reference,
            reviewed_by=item.reviewed_by,
            assumptions=tuple(item.assumptions),
        )
        result = await run_in_threadpool(
            calculate_short_circuit,
            imported.model,
            calculation_input,
            context_evidence_sha256=request.context_evidence_sha256,
        )
        logging.getLogger("ai_engineer.calculations").info(
            "short_circuit_calculation_completed",
            extra={
                "calculation_id": calculation_input.calculation_id,
                "context_id": request.context_id,
                "result_sha256": result.result_sha256,
                "protective_device_adequacy_evaluated": False,
                "peak_current_calculated": False,
                "arc_flash_calculated": False,
                "drawing_mutation_performed": False,
                "compliance_evaluation_performed": False,
            },
        )
        return ShortCircuitResponse(result=result.to_dict())

    @router.post(
        "/calculations/candidate-adequacy",
        response_model=CandidateAdequacyResponse,
        status_code=201,
    )
    async def create_candidate_adequacy_review(
        request: CandidateAdequacyCreateRequest,
    ) -> CandidateAdequacyResponse:
        imported = await run_in_threadpool(
            _autocad(application).import_drawing_context,
            context_id=request.context_id,
            evidence_sha256=request.context_evidence_sha256,
            confirmed=True,
        )
        low_voltage_item = request.low_voltage
        low_voltage_input = LowVoltageCalculationInput(
            calculation_id=low_voltage_item.calculation_id,
            calculation_version=low_voltage_item.calculation_version,
            phase_system=PhaseSystem(low_voltage_item.phase_system),
            load_output_power_watts=low_voltage_item.load_output_power_watts,
            demand_factor=low_voltage_item.demand_factor,
            nominal_voltage_volts=low_voltage_item.nominal_voltage_volts,
            power_factor=low_voltage_item.power_factor,
            efficiency=low_voltage_item.efficiency,
            conductor_ac_resistance_ohm_per_km=(
                low_voltage_item.conductor_ac_resistance_ohm_per_km
            ),
            conductor_reactance_ohm_per_km=(
                low_voltage_item.conductor_reactance_ohm_per_km
            ),
            load_object_ids=tuple(low_voltage_item.load_object_ids),
            route_ids=tuple(low_voltage_item.route_ids),
            reviewed_by=low_voltage_item.reviewed_by,
            assumptions=tuple(low_voltage_item.assumptions),
        )
        ampacity_item = request.ampacity
        ampacity_input = CableAmpacityInput(
            calculation_id=ampacity_item.calculation_id,
            calculation_version=ampacity_item.calculation_version,
            source_route_id=ampacity_item.source_route_id,
            cable_designation=ampacity_item.cable_designation,
            base_ampacity_amperes=ampacity_item.base_ampacity_amperes,
            base_ampacity_basis_reference=ampacity_item.base_ampacity_basis_reference,
            derating_factors=tuple(
                DeratingFactorInput(
                    factor_id=factor.factor_id,
                    label=factor.label,
                    factor=factor.factor,
                    basis_reference=factor.basis_reference,
                )
                for factor in ampacity_item.derating_factors
            ),
            reviewed_by=ampacity_item.reviewed_by,
            assumptions=tuple(ampacity_item.assumptions),
        )
        short_circuit_item = request.short_circuit
        short_circuit_input = ShortCircuitInput(
            calculation_id=short_circuit_item.calculation_id,
            calculation_version=short_circuit_item.calculation_version,
            source_route_id=short_circuit_item.source_route_id,
            fault_arrangement=FaultArrangement(short_circuit_item.fault_arrangement),
            nominal_voltage_volts=short_circuit_item.nominal_voltage_volts,
            voltage_multiplier=short_circuit_item.voltage_multiplier,
            source_resistance_ohms=short_circuit_item.source_resistance_ohms,
            source_reactance_ohms=short_circuit_item.source_reactance_ohms,
            path_resistance_ohms=short_circuit_item.path_resistance_ohms,
            path_reactance_ohms=short_circuit_item.path_reactance_ohms,
            impedance_basis_reference=short_circuit_item.impedance_basis_reference,
            reviewed_by=short_circuit_item.reviewed_by,
            assumptions=tuple(short_circuit_item.assumptions),
        )
        rule_item = request.rule_set
        rule_set = CandidateAdequacyRuleSet(
            rule_set_id=rule_item.rule_set_id,
            rule_set_version=rule_item.rule_set_version,
            candidate_id=rule_item.candidate_id,
            protective_device_designation=rule_item.protective_device_designation,
            protective_device_rated_current_amperes=(
                rule_item.protective_device_rated_current_amperes
            ),
            protective_device_breaking_capacity_amperes=(
                rule_item.protective_device_breaking_capacity_amperes
            ),
            required_ampacity_multiplier=rule_item.required_ampacity_multiplier,
            maximum_voltage_drop_percent=rule_item.maximum_voltage_drop_percent,
            basis_reference=rule_item.basis_reference,
            reviewed_by=rule_item.reviewed_by,
            assumptions=tuple(rule_item.assumptions),
        )
        low_voltage_result = await run_in_threadpool(
            calculate_low_voltage,
            imported.model,
            low_voltage_input,
            context_evidence_sha256=request.context_evidence_sha256,
        )
        ampacity_result = await run_in_threadpool(
            calculate_derated_ampacity,
            imported.model,
            ampacity_input,
            context_evidence_sha256=request.context_evidence_sha256,
        )
        short_circuit_result = await run_in_threadpool(
            calculate_short_circuit,
            imported.model,
            short_circuit_input,
            context_evidence_sha256=request.context_evidence_sha256,
        )
        result = await run_in_threadpool(
            evaluate_candidate_adequacy,
            imported.model,
            low_voltage_result,
            ampacity_result,
            short_circuit_result,
            rule_set,
        )
        logging.getLogger("ai_engineer.calculations").info(
            "candidate_adequacy_review_completed",
            extra={
                "rule_set_id": rule_set.rule_set_id,
                "candidate_id": rule_set.candidate_id,
                "context_id": request.context_id,
                "result_sha256": result.result_sha256,
                "candidate_adequate_under_reviewed_rules": (
                    result.candidate_adequate_under_reviewed_rules
                ),
                "automatic_selection_performed": False,
                "compliance_evaluation_performed": False,
                "drawing_mutation_performed": False,
            },
        )
        return CandidateAdequacyResponse(result=result.to_dict())

    @router.post(
        "/calculations/protective-conductor",
        response_model=ProtectiveConductorSizingResponse,
        status_code=201,
    )
    async def create_protective_conductor_sizing(
        request: ProtectiveConductorSizingCreateRequest,
    ) -> ProtectiveConductorSizingResponse:
        imported = await run_in_threadpool(
            _autocad(application).import_drawing_context,
            context_id=request.context_id,
            evidence_sha256=request.context_evidence_sha256,
            confirmed=True,
        )
        item = request.rule_input
        rule_input = ProtectiveConductorRuleInput(
            calculation_id=item.calculation_id,
            calculation_version=item.calculation_version,
            source_route_id=item.source_route_id,
            applicability=RuleApplicability(item.applicability),
            applicability_basis_reference=item.applicability_basis_reference,
            disconnection_basis=DisconnectionBasis(item.disconnection_basis),
            disconnection_basis_reference=item.disconnection_basis_reference,
            disconnection_time_seconds=item.disconnection_time_seconds,
            conductor_factor_amperes_sqrt_second_per_square_millimeter=(
                item.conductor_factor_amperes_sqrt_second_per_square_millimeter
            ),
            rule_source_id=item.rule_source_id,
            rule_source_title=item.rule_source_title,
            rule_source_version=item.rule_source_version,
            rule_source_locator=item.rule_source_locator,
            rule_source_sha256=item.rule_source_sha256,
            reviewed_by=item.reviewed_by,
            assumptions=tuple(item.assumptions),
        )
        short_circuit_result = None
        if request.short_circuit is not None:
            short_item = request.short_circuit
            short_input = ShortCircuitInput(
                calculation_id=short_item.calculation_id,
                calculation_version=short_item.calculation_version,
                source_route_id=short_item.source_route_id,
                fault_arrangement=FaultArrangement(short_item.fault_arrangement),
                nominal_voltage_volts=short_item.nominal_voltage_volts,
                voltage_multiplier=short_item.voltage_multiplier,
                source_resistance_ohms=short_item.source_resistance_ohms,
                source_reactance_ohms=short_item.source_reactance_ohms,
                path_resistance_ohms=short_item.path_resistance_ohms,
                path_reactance_ohms=short_item.path_reactance_ohms,
                impedance_basis_reference=short_item.impedance_basis_reference,
                reviewed_by=short_item.reviewed_by,
                assumptions=tuple(short_item.assumptions),
            )
            short_circuit_result = await run_in_threadpool(
                calculate_short_circuit,
                imported.model,
                short_input,
                context_evidence_sha256=request.context_evidence_sha256,
            )
        result = await run_in_threadpool(
            size_protective_conductor,
            imported.model,
            rule_input,
            short_circuit_result,
            context_evidence_sha256=request.context_evidence_sha256,
        )
        logging.getLogger("ai_engineer.calculations").info(
            "protective_conductor_sizing_completed",
            extra={
                "calculation_id": rule_input.calculation_id,
                "context_id": request.context_id,
                "result_sha256": result.result_sha256,
                "outcome": result.outcome.value,
                "conductor_selection_performed": False,
                "compliance_evaluation_performed": False,
                "drawing_mutation_performed": False,
            },
        )
        return ProtectiveConductorSizingResponse(result=result.to_dict())

    @router.post(
        "/drafting/runs",
        response_model=DraftingRunCreatedResponse,
        status_code=201,
    )
    async def create_drafting_run(
        request: DraftingPlanCreateRequest,
    ) -> DraftingRunCreatedResponse:
        created = await run_in_threadpool(
            _drafting_planner(application).create,
            request.proposal,
            ttl_minutes=request.ttl_minutes,
        )
        return DraftingRunCreatedResponse(
            run=created.lifecycle.run.to_dict(),
            validation=created.validation.to_dict(),
            owner_key=created.lifecycle.owner_key,
        )

    @router.get("/drafting/runs/{run_id}", response_model=DraftingRunResponse)
    async def get_drafting_run(
        run_id: str,
        owner_key: Annotated[
            str,
            Header(alias="X-AI-Engineer-Drafting-Key", min_length=32, max_length=128),
        ],
    ) -> DraftingRunResponse:
        record = await run_in_threadpool(
            _drafting_lifecycle(application).get, run_id, owner_key
        )
        return DraftingRunResponse.from_record(record)

    @router.post(
        "/drafting/runs/{run_id}/preview",
        response_model=DraftingPreviewResponse,
    )
    async def create_drafting_preview(
        run_id: str,
        request: DraftingPreviewCreateRequest,
        owner_key: Annotated[
            str,
            Header(alias="X-AI-Engineer-Drafting-Key", min_length=32, max_length=128),
        ],
    ) -> DraftingPreviewResponse:
        created = await run_in_threadpool(
            _drafting_planner(application).create_preview,
            run_id,
            owner_key,
            request.proposal,
            ttl_minutes=request.ttl_minutes,
        )
        return DraftingPreviewResponse.from_preview(
            created.run,
            created.preview,
            created.validation,
        )

    @router.post(
        "/drafting/runs/{run_id}/preview-review",
        response_model=DraftingRunResponse,
    )
    async def request_drafting_preview_review(
        run_id: str,
        owner_key: Annotated[
            str,
            Header(alias="X-AI-Engineer-Drafting-Key", min_length=32, max_length=128),
        ],
    ) -> DraftingRunResponse:
        record = await run_in_threadpool(
            _drafting_lifecycle(application).request_review, run_id, owner_key
        )
        return DraftingRunResponse.from_record(record)

    @router.post(
        "/drafting/runs/{run_id}/preview-review/decision",
        response_model=DraftingRunResponse,
    )
    async def decide_drafting_preview_review(
        run_id: str,
        request: DraftingReviewDecisionRequest,
        owner_key: Annotated[
            str,
            Header(alias="X-AI-Engineer-Drafting-Key", min_length=32, max_length=128),
        ],
    ) -> DraftingRunResponse:
        record = await run_in_threadpool(
            _drafting_lifecycle(application).decide,
            run_id,
            owner_key,
            approved=request.approved,
        )
        return DraftingRunResponse.from_record(record)

    @router.post(
        "/drafting/runs/{run_id}/execution-review",
        response_model=DraftingExecutionReviewResponse,
    )
    async def request_drafting_execution_review(
        run_id: str,
        request: DraftingExecutionRequest,
        owner_key: Annotated[
            str,
            Header(alias="X-AI-Engineer-Drafting-Key", min_length=32, max_length=128),
        ],
    ) -> DraftingExecutionReviewResponse:
        prepared = await run_in_threadpool(
            _drafting_planner(application).prepare_execution,
            run_id,
            owner_key,
            request.proposal,
            source_path=request.source_path,
            source_sha256=request.source_sha256,
        )
        return DraftingExecutionReviewResponse(
            run=prepared.run.to_dict(),
            execution=prepared.plan.to_dict(),
        )

    @router.post(
        "/drafting/runs/{run_id}/execution-review/decision",
        response_model=DraftingRunResponse,
    )
    async def decide_drafting_execution_review(
        run_id: str,
        request: DraftingReviewDecisionRequest,
        owner_key: Annotated[
            str,
            Header(alias="X-AI-Engineer-Drafting-Key", min_length=32, max_length=128),
        ],
    ) -> DraftingRunResponse:
        record = await run_in_threadpool(
            _drafting_lifecycle(application).decide_execution,
            run_id,
            owner_key,
            approved=request.approved,
        )
        return DraftingRunResponse.from_record(record)

    @router.post(
        "/drafting/runs/{run_id}/execute",
        response_model=DraftingExecutionResponse,
    )
    async def execute_drafting_run(
        run_id: str,
        request: DraftingExecutionRequest,
        owner_key: Annotated[
            str,
            Header(alias="X-AI-Engineer-Drafting-Key", min_length=32, max_length=128),
        ],
    ) -> DraftingExecutionResponse:
        completed = await run_in_threadpool(
            _drafting_planner(application).execute,
            run_id,
            owner_key,
            request.proposal,
            source_path=request.source_path,
            source_sha256=request.source_sha256,
        )
        return DraftingExecutionResponse(
            run=completed.run.to_dict(),
            execution=completed.plan.to_dict(),
            artifact=completed.artifact.to_dict(),
            recovery=completed.recovery.to_dict(),
        )

    @router.post(
        "/drafting/runs/{run_id}/cancel",
        response_model=DraftingRunResponse,
    )
    async def cancel_drafting_run(
        run_id: str,
        owner_key: Annotated[
            str,
            Header(alias="X-AI-Engineer-Drafting-Key", min_length=32, max_length=128),
        ],
    ) -> DraftingRunResponse:
        record = await run_in_threadpool(
            _drafting_lifecycle(application).cancel, run_id, owner_key
        )
        return DraftingRunResponse.from_record(record)

    @router.get("/developer/status", response_model=DeveloperStatusResponse)
    async def developer_status() -> DeveloperStatusResponse:
        developer = application.developer_agent
        return DeveloperStatusResponse(
            configured=developer is not None,
            provider_name=developer.provider.name if developer else None,
        )

    @router.post(
        "/developer/runs", response_model=DeveloperRunResponse, status_code=201
    )
    async def create_developer_run(
        request: DeveloperRunCreateRequest,
    ) -> DeveloperRunResponse:
        record = await run_in_threadpool(
            _developer(application).create_run,
            request.goal,
            maximum_actions=request.maximum_actions,
        )
        return DeveloperRunResponse.from_record(record)

    @router.get("/developer/runs", response_model=DeveloperRunListResponse)
    async def list_developer_runs(
        limit: Annotated[int, Query(strict=True, ge=1, le=500)] = 100,
    ) -> DeveloperRunListResponse:
        records = await run_in_threadpool(
            _developer(application).list_runs, limit=limit
        )
        return DeveloperRunListResponse(
            runs=[DeveloperRunResponse.from_record(item) for item in records]
        )

    @router.get("/developer/runs/{run_id}", response_model=DeveloperRunResponse)
    async def get_developer_run(run_id: str) -> DeveloperRunResponse:
        record = await run_in_threadpool(_developer(application).get, run_id)
        return DeveloperRunResponse.from_record(record)

    @router.post(
        "/developer/runs/{run_id}/plan-review",
        response_model=DeveloperRunResponse,
    )
    async def review_developer_plan(
        run_id: str, request: DeveloperReviewRequest
    ) -> DeveloperRunResponse:
        record = await run_in_threadpool(
            _developer(application).review_plan, run_id, approved=request.approved
        )
        return DeveloperRunResponse.from_record(record)

    @router.post(
        "/developer/runs/{run_id}/advance", response_model=DeveloperRunResponse
    )
    async def advance_developer_run(run_id: str) -> DeveloperRunResponse:
        record = await run_in_threadpool(_developer(application).advance, run_id)
        return DeveloperRunResponse.from_record(record)

    @router.post(
        "/developer/runs/{run_id}/action-review",
        response_model=DeveloperRunResponse,
    )
    async def review_developer_action(
        run_id: str, request: DeveloperReviewRequest
    ) -> DeveloperRunResponse:
        record = await run_in_threadpool(
            _developer(application).review_action, run_id, approved=request.approved
        )
        return DeveloperRunResponse.from_record(record)

    @router.post(
        "/developer/runs/{run_id}/cancel", response_model=DeveloperRunResponse
    )
    async def cancel_developer_run(run_id: str) -> DeveloperRunResponse:
        record = await run_in_threadpool(_developer(application).cancel, run_id)
        return DeveloperRunResponse.from_record(record)

    return router


async def _submit_and_run(
    application: Application, command_name: str, payload: dict[str, object]
) -> TaskResponse:
    engine = _tasks(application)
    task = await run_in_threadpool(engine.submit, command_name, payload)
    result = await run_in_threadpool(engine.run, task.task_id)
    return TaskResponse.from_record(result)


def _tasks(application: Application):
    if application.task_engine is None:
        raise ApiConfigurationError("Application task engine is not initialized.")
    return application.task_engine


def _registry(application: Application):
    if application.command_registry is None:
        raise ApiConfigurationError("Application command registry is not initialized.")
    return application.command_registry


def _approvals(application: Application):
    if application.approvals is None:
        raise ApiConfigurationError("Application approval manager is not initialized.")
    return application.approvals


def _developer(application: Application):
    if application.developer_agent is None:
        raise ApiConfigurationError("Developer provider is not configured.")
    return application.developer_agent


def _copilot(application: Application):
    if application.copilot_sessions is None:
        raise ApiConfigurationError("Copilot session service is not initialized.")
    return application.copilot_sessions


def _autocad(application: Application):
    if application.autocad_manager is None:
        raise ApiConfigurationError("AutoCAD context service is not initialized.")
    return application.autocad_manager


def _takeoff_policy(request: TakeoffCreateRequest) -> TakeoffPolicy:
    return TakeoffPolicy(
        calculation_version=request.policy.calculation_version,
        vertical_allowance_per_route_meters=(
            request.policy.vertical_allowance_per_route_meters
        ),
        termination_allowance_per_end_meters=(
            request.policy.termination_allowance_per_end_meters
        ),
        waste_percent=request.policy.waste_percent,
        route_overrides=tuple(
            RouteLengthOverride(
                route_id=item.route_id,
                final_total_meters=item.final_total_meters,
                reason=item.reason,
                reviewed_by=item.reviewed_by,
            )
            for item in request.policy.route_overrides
        ),
    )


def _drafting_lifecycle(application: Application):
    if application.drafting_lifecycle is None:
        raise ApiConfigurationError("Drafting lifecycle service is not initialized.")
    return application.drafting_lifecycle


def _drafting_planner(application: Application):
    if application.drafting_planner is None:
        raise ApiConfigurationError("Drafting planning service is not initialized.")
    return application.drafting_planner


def _raise_unauthorized() -> None:
    from fastapi import HTTPException

    raise HTTPException(
        status_code=401,
        detail="Authentication required.",
        headers={"WWW-Authenticate": "Bearer"},
    )
