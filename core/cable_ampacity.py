"""Reviewed cable ampacity inputs and deterministic derating chains."""

from __future__ import annotations

import hashlib
import json
import math
import re
from dataclasses import dataclass
from enum import Enum
from typing import Any

from agent.exceptions import CableAmpacityError
from core.electrical_drafting import canonical_model_sha256
from core.electrical_model import ElectricalProjectModel, JsonRecord, SourceReference


CALCULATION_VERSION = "m20.cable_ampacity_derating.v1"
_IDENTIFIER = re.compile(r"[a-z][a-z0-9_.:-]{0,127}")
_SHA256 = re.compile(r"[0-9a-f]{64}")


@dataclass(frozen=True, slots=True)
class DeratingFactorInput(JsonRecord):
    """One ordered, reviewed factor and its human-auditable basis."""

    factor_id: str
    label: str
    factor: float
    basis_reference: str

    def __post_init__(self) -> None:
        _identifier(self.factor_id, "derating factor ID")
        _text(self.label, "derating factor label", 128)
        _number(self.factor, "derating factor", minimum=0.000001, maximum=1.0)
        _text(self.basis_reference, "derating factor basis reference", 512)


@dataclass(frozen=True, slots=True)
class CableAmpacityInput(JsonRecord):
    """Exact engineer-reviewed basis for one route-linked derating calculation."""

    calculation_id: str
    calculation_version: str
    source_route_id: str
    cable_designation: str
    base_ampacity_amperes: float
    base_ampacity_basis_reference: str
    derating_factors: tuple[DeratingFactorInput, ...]
    reviewed_by: str
    assumptions: tuple[str, ...]

    def __post_init__(self) -> None:
        _identifier(self.calculation_id, "ampacity calculation ID")
        if self.calculation_version != CALCULATION_VERSION:
            raise CableAmpacityError("Unsupported cable ampacity calculation version.")
        _identifier(self.source_route_id, "ampacity source route ID")
        _text(self.cable_designation, "cable designation", 256)
        _number(self.base_ampacity_amperes, "base ampacity", minimum=0.000001)
        _text(self.base_ampacity_basis_reference, "base ampacity basis reference", 512)
        if not isinstance(self.derating_factors, tuple) or not 1 <= len(
            self.derating_factors
        ) <= 16:
            raise CableAmpacityError("Ampacity input requires 1 through 16 derating factors.")
        if any(not isinstance(item, DeratingFactorInput) for item in self.derating_factors):
            raise CableAmpacityError("Ampacity derating factors are invalid.")
        factor_ids = tuple(item.factor_id for item in self.derating_factors)
        if len(factor_ids) != len(set(factor_ids)):
            raise CableAmpacityError("Ampacity derating factor IDs must be unique.")
        _text(self.reviewed_by, "ampacity reviewer", 128)
        if not isinstance(self.assumptions, tuple) or not 1 <= len(self.assumptions) <= 16:
            raise CableAmpacityError("Ampacity assumptions must contain 1 through 16 entries.")
        for assumption in self.assumptions:
            _text(assumption, "ampacity assumption", 512)


@dataclass(frozen=True, slots=True)
class DeratingStageResult(JsonRecord):
    """One reproducible multiplication stage in the reviewed chain."""

    sequence: int
    factor_id: str
    label: str
    factor: float
    basis_reference: str
    input_ampacity_amperes: float
    output_ampacity_amperes: float

    def __post_init__(self) -> None:
        if not isinstance(self.sequence, int) or isinstance(self.sequence, bool) or self.sequence < 1:
            raise CableAmpacityError("Derating stage sequence is invalid.")
        _identifier(self.factor_id, "derating stage factor ID")
        _text(self.label, "derating stage label", 128)
        _number(self.factor, "derating stage factor", minimum=0.000001, maximum=1.0)
        _text(self.basis_reference, "derating stage basis reference", 512)
        _number(self.input_ampacity_amperes, "derating stage input ampacity", minimum=0.000001)
        _number(self.output_ampacity_amperes, "derating stage output ampacity", minimum=0.000001)


@dataclass(frozen=True, slots=True)
class CableAmpacityResult(JsonRecord):
    """Self-hashed route-linked ampacity result without selection or compliance claims."""

    schema_version: int
    project_id: str
    document_id: str
    context_id: str
    context_evidence_sha256: str
    model_sha256: str
    input: CableAmpacityInput
    route_source: SourceReference
    combined_derating_factor: float
    derated_ampacity_amperes: float
    stages: tuple[DeratingStageResult, ...]
    formula: str
    warnings: tuple[str, ...]
    cable_selection_performed: bool
    protective_device_selection_performed: bool
    compliance_evaluation_performed: bool
    drawing_mutation_performed: bool
    result_sha256: str

    def __post_init__(self) -> None:
        if self.schema_version != 1:
            raise CableAmpacityError("Cable ampacity result schema must be 1.")
        _identifier(self.project_id, "ampacity result project ID")
        _identifier(self.document_id, "ampacity result document ID")
        if re.fullmatch(r"[0-9a-f]{32}", self.context_id) is None:
            raise CableAmpacityError("Cable ampacity result context ID is invalid.")
        for value, name in (
            (self.context_evidence_sha256, "context evidence hash"),
            (self.model_sha256, "model hash"),
            (self.result_sha256, "result hash"),
        ):
            if _SHA256.fullmatch(value) is None:
                raise CableAmpacityError(f"Cable ampacity {name} must be SHA-256.")
        if not isinstance(self.input, CableAmpacityInput):
            raise CableAmpacityError("Cable ampacity result input is invalid.")
        if not isinstance(self.route_source, SourceReference):
            raise CableAmpacityError("Cable ampacity route source is invalid.")
        _number(self.combined_derating_factor, "combined derating factor", minimum=0.000001, maximum=1.0)
        _number(self.derated_ampacity_amperes, "derated ampacity", minimum=0.000001)
        if len(self.stages) != len(self.input.derating_factors) or any(
            not isinstance(item, DeratingStageResult) for item in self.stages
        ):
            raise CableAmpacityError("Cable ampacity derating stages are incomplete.")
        if tuple(item.sequence for item in self.stages) != tuple(range(1, len(self.stages) + 1)):
            raise CableAmpacityError("Cable ampacity derating stages are out of order.")
        _text(self.formula, "cable ampacity formula", 512)
        if not isinstance(self.warnings, tuple) or len(self.warnings) < 2:
            raise CableAmpacityError("Cable ampacity warnings are incomplete.")
        for warning in self.warnings:
            _text(warning, "cable ampacity warning", 512)
        if any((
            self.cable_selection_performed,
            self.protective_device_selection_performed,
            self.compliance_evaluation_performed,
            self.drawing_mutation_performed,
        )):
            raise CableAmpacityError("Cable ampacity input derating cannot claim selection, compliance, or mutation.")
        value = self.to_dict()
        del value["result_sha256"]
        if self.result_sha256 != _canonical_sha256(value):
            raise CableAmpacityError("Cable ampacity result SHA-256 does not match its content.")


def calculate_derated_ampacity(
    model: ElectricalProjectModel,
    calculation_input: CableAmpacityInput,
    *,
    context_evidence_sha256: str,
) -> CableAmpacityResult:
    """Apply an ordered reviewed factor chain to a route-linked base ampacity."""
    if not isinstance(model, ElectricalProjectModel) or not isinstance(
        calculation_input, CableAmpacityInput
    ):
        raise CableAmpacityError("Cable ampacity calculation requires a typed model and input.")
    if _SHA256.fullmatch(context_evidence_sha256) is None:
        raise CableAmpacityError("Context evidence hash must be SHA-256.")
    if model.provenance.evidence_sha256 != context_evidence_sha256:
        raise CableAmpacityError("Model provenance does not match context evidence.")
    routes = {item.route_id: item for item in model.routes}
    try:
        route = routes[calculation_input.source_route_id]
    except KeyError as exc:
        raise CableAmpacityError(
            f"Ampacity input references an unknown source route: {calculation_input.source_route_id}."
        ) from exc

    stages: list[DeratingStageResult] = []
    current = calculation_input.base_ampacity_amperes
    combined = 1.0
    for sequence, factor in enumerate(calculation_input.derating_factors, start=1):
        output = _normalized(current * factor.factor)
        stages.append(DeratingStageResult(
            sequence=sequence,
            factor_id=factor.factor_id,
            label=factor.label,
            factor=factor.factor,
            basis_reference=factor.basis_reference,
            input_ampacity_amperes=_normalized(current),
            output_ampacity_amperes=output,
        ))
        current = output
        combined *= factor.factor

    payload: dict[str, Any] = {
        "schema_version": 1,
        "project_id": model.project_id,
        "document_id": model.document_id,
        "context_id": model.context_id,
        "context_evidence_sha256": context_evidence_sha256,
        "model_sha256": canonical_model_sha256(model),
        "input": calculation_input,
        "route_source": route.source,
        "combined_derating_factor": _normalized(combined),
        "derated_ampacity_amperes": _normalized(current),
        "stages": tuple(stages),
        "formula": "Iz_derated=Iz_base*product(reviewed_derating_factors_in_order)",
        "warnings": (
            "Base ampacity and every factor basis are reviewed inputs; this calculation does not verify their external source or applicability.",
            "No load-current comparison, cable sizing, protective-device coordination, short-circuit, voltage-drop, earth-conductor, or standards-compliance conclusion is performed.",
        ),
        "cable_selection_performed": False,
        "protective_device_selection_performed": False,
        "compliance_evaluation_performed": False,
        "drawing_mutation_performed": False,
    }
    return CableAmpacityResult(**payload, result_sha256=_canonical_sha256(payload))


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
        raise CableAmpacityError(f"{name} must be a bounded lowercase identifier.")


def _text(value: str, name: str, maximum: int) -> None:
    if not isinstance(value, str) or not value or len(value.encode("utf-8")) > maximum:
        raise CableAmpacityError(f"{name} must be non-empty and at most {maximum} bytes.")
    if any(ord(character) < 32 and character != "\t" for character in value):
        raise CableAmpacityError(f"{name} contains unsupported control characters.")


def _number(value: float, name: str, *, minimum: float, maximum: float = 1_000_000_000.0) -> None:
    if (
        not isinstance(value, (int, float))
        or isinstance(value, bool)
        or not math.isfinite(value)
        or not minimum <= float(value) <= maximum
    ):
        raise CableAmpacityError(f"{name} is outside its supported numeric range.")
