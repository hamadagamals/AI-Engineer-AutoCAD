"""Hash-bound clause evidence and deterministic fail-closed compliance findings."""

from __future__ import annotations

import hashlib
import json
import re
from dataclasses import dataclass
from datetime import date
from decimal import Decimal, InvalidOperation
from enum import Enum
from typing import Any, Mapping

from agent.exceptions import ComplianceEvidenceError
from core.code_profile import ProjectCodeProfile
from core.electrical_model import JsonRecord


CLAUSE_EVIDENCE_VERSION = "m22.clause_evidence.v1"
RULE_VERSION = "m22.compliance_rule.v1"
FACT_VERSION = "m22.engineering_fact.v1"
RESULT_VERSION = "m22.compliance_result.v1"
_IDENTIFIER = re.compile(r"[a-z][a-z0-9_.:-]{0,127}")
_SHA256 = re.compile(r"[0-9a-f]{64}")
_DATE = re.compile(r"\d{4}-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12]\d|3[01])")


class ComparisonOperator(str, Enum):
    """Supported deterministic comparisons."""

    LESS_THAN_OR_EQUAL = "lte"
    GREATER_THAN_OR_EQUAL = "gte"
    EQUAL = "eq"


class ComplianceOutcome(str, Enum):
    """A finding state; UNKNOWN and CONFLICT deliberately fail closed."""

    PASS = "pass"
    FAIL = "fail"
    UNKNOWN = "unknown"
    CONFLICT = "conflict"


@dataclass(frozen=True, slots=True)
class CodeClauseEvidence(JsonRecord):
    """A reviewed clause paraphrase bound to an exact pinned profile and source."""

    schema_version: int
    evidence_version: str
    evidence_id: str
    profile_id: str
    profile_sha256: str
    authority_id: str
    source_id: str
    source_edition: str
    source_content_sha256: str
    clause_locator: str
    topic: str
    rule_summary: str
    retrieved_on: str
    reviewed_by: str
    assumptions: tuple[str, ...]
    full_clause_text_stored: bool
    provider_authority_claimed: bool
    evidence_sha256: str

    def __post_init__(self) -> None:
        if self.schema_version != 1 or self.evidence_version != CLAUSE_EVIDENCE_VERSION:
            raise ComplianceEvidenceError("Unsupported clause-evidence version.")
        for value, name in (
            (self.evidence_id, "clause evidence ID"),
            (self.profile_id, "clause profile ID"),
            (self.authority_id, "clause authority ID"),
            (self.source_id, "clause source ID"),
        ):
            _identifier(value, name)
        _sha256(self.profile_sha256, "clause profile hash")
        _text(self.source_edition, "clause source edition", 64)
        _sha256(self.source_content_sha256, "clause source content hash")
        _text(self.clause_locator, "clause locator", 256)
        _text(self.topic, "clause topic", 128)
        _text(self.rule_summary, "reviewed clause rule summary", 1024)
        _date(self.retrieved_on, "clause retrieval date")
        _text(self.reviewed_by, "clause reviewer", 128)
        _texts(self.assumptions, "clause assumptions", 1, 16, 512)
        if self.full_clause_text_stored:
            raise ComplianceEvidenceError("Clause evidence cannot store full protected text.")
        if self.provider_authority_claimed:
            raise ComplianceEvidenceError("A provider cannot be claimed as code authority.")
        _verify_self_hash(self, "evidence_sha256", "Clause evidence")


@dataclass(frozen=True, slots=True)
class ComplianceRule(JsonRecord):
    """A deterministic comparison derived from one reviewed clause evidence record."""

    rule_version: str
    rule_id: str
    evidence_id: str
    evidence_sha256: str
    fact_id: str
    operator: ComparisonOperator
    required_value: str
    unit: str
    formula: str
    assumptions: tuple[str, ...]
    rule_sha256: str

    def __post_init__(self) -> None:
        if self.rule_version != RULE_VERSION:
            raise ComplianceEvidenceError("Unsupported compliance-rule version.")
        for value, name in (
            (self.rule_id, "compliance rule ID"),
            (self.evidence_id, "compliance evidence ID"),
            (self.fact_id, "engineering fact ID"),
        ):
            _identifier(value, name)
        _sha256(self.evidence_sha256, "rule evidence hash")
        if not isinstance(self.operator, ComparisonOperator):
            raise ComplianceEvidenceError("Compliance comparison operator is invalid.")
        _decimal(self.required_value, "required rule value")
        _text(self.unit, "compliance rule unit", 64)
        _text(self.formula, "compliance rule formula", 512)
        _texts(self.assumptions, "compliance rule assumptions", 1, 16, 512)
        _verify_self_hash(self, "rule_sha256", "Compliance rule")


@dataclass(frozen=True, slots=True)
class EngineeringFact(JsonRecord):
    """One exact model/calculation fact with its upstream result hash."""

    fact_version: str
    fact_id: str
    context_id: str
    source_result_sha256: str
    value: str
    unit: str
    basis_reference: str
    reviewed_by: str
    assumptions: tuple[str, ...]
    fact_sha256: str

    def __post_init__(self) -> None:
        if self.fact_version != FACT_VERSION:
            raise ComplianceEvidenceError("Unsupported engineering-fact version.")
        _identifier(self.fact_id, "engineering fact ID")
        _identifier(self.context_id, "engineering fact context ID")
        _sha256(self.source_result_sha256, "engineering fact source-result hash")
        _decimal(self.value, "engineering fact value")
        _text(self.unit, "engineering fact unit", 64)
        _text(self.basis_reference, "engineering fact basis", 512)
        _text(self.reviewed_by, "engineering fact reviewer", 128)
        _texts(self.assumptions, "engineering fact assumptions", 1, 16, 512)
        _verify_self_hash(self, "fact_sha256", "Engineering fact")


@dataclass(frozen=True, slots=True)
class ComplianceFinding(JsonRecord):
    """One reproducible rule finding."""

    rule_id: str
    fact_id: str
    outcome: ComplianceOutcome
    actual_value: str | None
    required_value: str
    unit: str
    formula: str
    clause_evidence_sha256: str
    message: str

    def __post_init__(self) -> None:
        _identifier(self.rule_id, "finding rule ID")
        _identifier(self.fact_id, "finding fact ID")
        if not isinstance(self.outcome, ComplianceOutcome):
            raise ComplianceEvidenceError("Compliance finding outcome is invalid.")
        if self.actual_value is not None:
            _decimal(self.actual_value, "finding actual value")
        _decimal(self.required_value, "finding required value")
        _text(self.unit, "finding unit", 64)
        _text(self.formula, "finding formula", 512)
        _sha256(self.clause_evidence_sha256, "finding clause evidence hash")
        _text(self.message, "finding message", 512)


@dataclass(frozen=True, slots=True)
class ComplianceResult(JsonRecord):
    """Self-hashed evaluation result that never performs retrieval, approval, or drafting."""

    schema_version: int
    result_version: str
    evaluation_id: str
    project_id: str
    profile_id: str
    profile_sha256: str
    clause_evidence_sha256s: tuple[str, ...]
    rule_sha256s: tuple[str, ...]
    fact_sha256s: tuple[str, ...]
    findings: tuple[ComplianceFinding, ...]
    complete: bool
    all_requirements_satisfied: bool
    evaluated_by: str
    evaluated_on: str
    assumptions: tuple[str, ...]
    provider_retrieval_performed: bool
    regulatory_approval_performed: bool
    drawing_mutation_performed: bool
    result_sha256: str

    def __post_init__(self) -> None:
        if self.schema_version != 1 or self.result_version != RESULT_VERSION:
            raise ComplianceEvidenceError("Unsupported compliance-result version.")
        _identifier(self.evaluation_id, "compliance evaluation ID")
        _identifier(self.project_id, "compliance project ID")
        _identifier(self.profile_id, "compliance profile ID")
        _sha256(self.profile_sha256, "compliance profile hash")
        for values, name in (
            (self.clause_evidence_sha256s, "clause evidence hashes"),
            (self.rule_sha256s, "rule hashes"),
            (self.fact_sha256s, "fact hashes"),
        ):
            if not isinstance(values, tuple):
                raise ComplianceEvidenceError(f"{name} must be a tuple.")
            for value in values:
                _sha256(value, name)
            if len(values) != len(set(values)):
                raise ComplianceEvidenceError(f"{name} must be unique.")
        if not self.findings or any(not isinstance(item, ComplianceFinding) for item in self.findings):
            raise ComplianceEvidenceError("Compliance result requires typed findings.")
        expected_complete = all(
            item.outcome not in {ComplianceOutcome.UNKNOWN, ComplianceOutcome.CONFLICT}
            for item in self.findings
        )
        expected_satisfied = expected_complete and all(
            item.outcome is ComplianceOutcome.PASS for item in self.findings
        )
        if self.complete is not expected_complete:
            raise ComplianceEvidenceError("Compliance result completeness is inconsistent.")
        if self.all_requirements_satisfied is not expected_satisfied:
            raise ComplianceEvidenceError("Compliance satisfaction state is inconsistent.")
        _text(self.evaluated_by, "compliance evaluator", 128)
        _date(self.evaluated_on, "compliance evaluation date")
        _texts(self.assumptions, "compliance evaluation assumptions", 1, 16, 512)
        if (
            self.provider_retrieval_performed
            or self.regulatory_approval_performed
            or self.drawing_mutation_performed
        ):
            raise ComplianceEvidenceError(
                "Compliance evaluation cannot claim retrieval, regulatory approval, or drawing mutation."
            )
        _verify_self_hash(self, "result_sha256", "Compliance result")


def create_clause_evidence(
    profile: ProjectCodeProfile,
    *,
    evidence_id: str,
    authority_id: str,
    clause_locator: str,
    topic: str,
    rule_summary: str,
    reviewed_by: str,
    assumptions: tuple[str, ...],
) -> CodeClauseEvidence:
    """Create reviewed evidence from the exact source pinned by an authority."""
    if not isinstance(profile, ProjectCodeProfile):
        raise ComplianceEvidenceError("Clause evidence requires a typed project code profile.")
    authority = next((item for item in profile.authorities if item.authority_id == authority_id), None)
    if authority is None:
        raise ComplianceEvidenceError("Clause authority is not pinned in the project profile.")
    source = next(item for item in profile.sources if item.source_id == authority.source_id)
    payload: dict[str, Any] = {
        "schema_version": 1,
        "evidence_version": CLAUSE_EVIDENCE_VERSION,
        "evidence_id": evidence_id,
        "profile_id": profile.profile_id,
        "profile_sha256": profile.profile_sha256,
        "authority_id": authority.authority_id,
        "source_id": source.source_id,
        "source_edition": source.edition,
        "source_content_sha256": source.content_sha256,
        "clause_locator": clause_locator,
        "topic": topic,
        "rule_summary": rule_summary,
        "retrieved_on": source.retrieved_on,
        "reviewed_by": reviewed_by,
        "assumptions": assumptions,
        "full_clause_text_stored": False,
        "provider_authority_claimed": False,
    }
    return CodeClauseEvidence(**payload, evidence_sha256=_canonical_sha256(payload))


def create_compliance_rule(
    *, evidence: CodeClauseEvidence, rule_id: str, fact_id: str,
    operator: ComparisonOperator, required_value: str, unit: str,
    formula: str, assumptions: tuple[str, ...],
) -> ComplianceRule:
    """Create a self-hashed deterministic rule from reviewed clause evidence."""
    if not isinstance(evidence, CodeClauseEvidence):
        raise ComplianceEvidenceError("Compliance rule requires typed clause evidence.")
    payload: dict[str, Any] = {
        "rule_version": RULE_VERSION, "rule_id": rule_id,
        "evidence_id": evidence.evidence_id, "evidence_sha256": evidence.evidence_sha256,
        "fact_id": fact_id, "operator": operator, "required_value": required_value,
        "unit": unit, "formula": formula, "assumptions": assumptions,
    }
    return ComplianceRule(**payload, rule_sha256=_canonical_sha256(payload))


def create_engineering_fact(
    *, fact_id: str, context_id: str, source_result_sha256: str, value: str,
    unit: str, basis_reference: str, reviewed_by: str, assumptions: tuple[str, ...],
) -> EngineeringFact:
    """Create a self-hashed exact fact without interpreting drawing geometry."""
    payload: dict[str, Any] = {
        "fact_version": FACT_VERSION, "fact_id": fact_id, "context_id": context_id,
        "source_result_sha256": source_result_sha256, "value": value, "unit": unit,
        "basis_reference": basis_reference, "reviewed_by": reviewed_by,
        "assumptions": assumptions,
    }
    return EngineeringFact(**payload, fact_sha256=_canonical_sha256(payload))


def evaluate_compliance(
    profile: ProjectCodeProfile,
    *,
    evaluation_id: str,
    clauses: tuple[CodeClauseEvidence, ...],
    rules: tuple[ComplianceRule, ...],
    facts: tuple[EngineeringFact, ...],
    evaluated_by: str,
    evaluated_on: str,
    assumptions: tuple[str, ...],
) -> ComplianceResult:
    """Evaluate exact evidence; absent inputs become UNKNOWN and conflicts fail closed."""
    if not isinstance(profile, ProjectCodeProfile):
        raise ComplianceEvidenceError("Compliance evaluation requires a typed code profile.")
    _typed_tuple(clauses, CodeClauseEvidence, "clause evidence", 0, 256)
    _typed_tuple(rules, ComplianceRule, "compliance rules", 1, 256)
    _typed_tuple(facts, EngineeringFact, "engineering facts", 0, 512)
    _unique((item.evidence_id for item in clauses), "clause evidence IDs")
    _unique((item.rule_id for item in rules), "compliance rule IDs")
    _validate_clause_bindings(profile, clauses)

    clause_by_id = {item.evidence_id: item for item in clauses}
    facts_by_id: dict[str, list[EngineeringFact]] = {}
    for fact in facts:
        facts_by_id.setdefault(fact.fact_id, []).append(fact)
    rules_by_fact: dict[str, list[ComplianceRule]] = {}
    for rule in rules:
        rules_by_fact.setdefault(rule.fact_id, []).append(rule)

    findings: list[ComplianceFinding] = []
    for rule in rules:
        clause = clause_by_id.get(rule.evidence_id)
        if clause is None:
            findings.append(_finding(rule, ComplianceOutcome.UNKNOWN, None, "Reviewed clause evidence is missing."))
            continue
        if clause.evidence_sha256 != rule.evidence_sha256:
            raise ComplianceEvidenceError("Compliance rule clause-evidence hash does not match.")
        peers = rules_by_fact[rule.fact_id]
        signatures = {(item.operator, item.required_value, item.unit) for item in peers}
        if len(signatures) > 1:
            findings.append(_finding(rule, ComplianceOutcome.CONFLICT, None, "Conflicting reviewed rules target the same fact."))
            continue
        candidates = facts_by_id.get(rule.fact_id, [])
        if not candidates:
            findings.append(_finding(rule, ComplianceOutcome.UNKNOWN, None, "Required engineering fact is missing."))
            continue
        if len(candidates) > 1:
            findings.append(_finding(rule, ComplianceOutcome.CONFLICT, None, "Multiple engineering facts claim the same identity."))
            continue
        fact = candidates[0]
        if fact.unit != rule.unit:
            findings.append(_finding(rule, ComplianceOutcome.CONFLICT, fact.value, "Engineering fact and rule units conflict."))
            continue
        actual = Decimal(fact.value)
        required = Decimal(rule.required_value)
        passed = {
            ComparisonOperator.LESS_THAN_OR_EQUAL: actual <= required,
            ComparisonOperator.GREATER_THAN_OR_EQUAL: actual >= required,
            ComparisonOperator.EQUAL: actual == required,
        }[rule.operator]
        outcome = ComplianceOutcome.PASS if passed else ComplianceOutcome.FAIL
        findings.append(_finding(rule, outcome, fact.value, "Deterministic reviewed comparison completed."))

    complete = all(item.outcome not in {ComplianceOutcome.UNKNOWN, ComplianceOutcome.CONFLICT} for item in findings)
    payload: dict[str, Any] = {
        "schema_version": 1, "result_version": RESULT_VERSION,
        "evaluation_id": evaluation_id, "project_id": profile.project_id,
        "profile_id": profile.profile_id, "profile_sha256": profile.profile_sha256,
        "clause_evidence_sha256s": tuple(item.evidence_sha256 for item in clauses),
        "rule_sha256s": tuple(item.rule_sha256 for item in rules),
        "fact_sha256s": tuple(item.fact_sha256 for item in facts),
        "findings": tuple(findings), "complete": complete,
        "all_requirements_satisfied": complete and all(item.outcome is ComplianceOutcome.PASS for item in findings),
        "evaluated_by": evaluated_by, "evaluated_on": evaluated_on,
        "assumptions": assumptions, "provider_retrieval_performed": False,
        "regulatory_approval_performed": False, "drawing_mutation_performed": False,
    }
    return ComplianceResult(**payload, result_sha256=_canonical_sha256(payload))


def parse_clause_evidence(value: object) -> CodeClauseEvidence:
    """Parse one exact JSON-shaped clause record and verify its self hash."""
    item = _mapping(value, "clause evidence")
    _exact_keys(item, CodeClauseEvidence, "clause evidence")
    assumptions = _json_array(item["assumptions"], "clause assumptions")
    return CodeClauseEvidence(**{**item, "assumptions": tuple(assumptions)})


def parse_compliance_rule(value: object) -> ComplianceRule:
    """Parse one exact JSON-shaped deterministic rule and verify its self hash."""
    item = _mapping(value, "compliance rule")
    _exact_keys(item, ComplianceRule, "compliance rule")
    assumptions = _json_array(item["assumptions"], "compliance rule assumptions")
    operator = _enum(ComparisonOperator, item["operator"], "comparison operator")
    return ComplianceRule(
        **{**item, "operator": operator, "assumptions": tuple(assumptions)}
    )


def parse_engineering_fact(value: object) -> EngineeringFact:
    """Parse one exact JSON-shaped fact and verify its upstream and self hashes."""
    item = _mapping(value, "engineering fact")
    _exact_keys(item, EngineeringFact, "engineering fact")
    assumptions = _json_array(item["assumptions"], "engineering fact assumptions")
    return EngineeringFact(**{**item, "assumptions": tuple(assumptions)})


def _validate_clause_bindings(profile: ProjectCodeProfile, clauses: tuple[CodeClauseEvidence, ...]) -> None:
    authorities = {item.authority_id: item for item in profile.authorities}
    sources = {item.source_id: item for item in profile.sources}
    for clause in clauses:
        if clause.profile_id != profile.profile_id or clause.profile_sha256 != profile.profile_sha256:
            raise ComplianceEvidenceError("Clause evidence is bound to a different project profile.")
        authority = authorities.get(clause.authority_id)
        if authority is None or authority.source_id != clause.source_id:
            raise ComplianceEvidenceError("Clause evidence authority/source binding is invalid.")
        source = sources[clause.source_id]
        if clause.source_edition != source.edition or clause.source_content_sha256 != source.content_sha256:
            raise ComplianceEvidenceError("Clause evidence does not match the pinned source edition and hash.")


def _finding(rule: ComplianceRule, outcome: ComplianceOutcome, actual: str | None, message: str) -> ComplianceFinding:
    return ComplianceFinding(rule.rule_id, rule.fact_id, outcome, actual, rule.required_value, rule.unit, rule.formula, rule.evidence_sha256, message)


def _canonical_sha256(value: object) -> str:
    def convert(item: object) -> object:
        if isinstance(item, Enum): return item.value
        if isinstance(item, JsonRecord): return item.to_dict()
        if isinstance(item, tuple): return [convert(child) for child in item]
        if isinstance(item, dict): return {str(key): convert(child) for key, child in item.items()}
        return item
    encoded = json.dumps(convert(value), ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest()


def _verify_self_hash(record: JsonRecord, field: str, name: str) -> None:
    value = getattr(record, field)
    _sha256(value, f"{name} hash")
    payload = record.to_dict()
    del payload[field]
    if value != _canonical_sha256(payload):
        raise ComplianceEvidenceError(f"{name} SHA-256 does not match its content.")


def _identifier(value: object, name: str) -> None:
    if not isinstance(value, str) or _IDENTIFIER.fullmatch(value) is None:
        raise ComplianceEvidenceError(f"{name} is invalid.")


def _text(value: object, name: str, maximum: int) -> None:
    if not isinstance(value, str) or not value.strip() or value != value.strip() or len(value.encode("utf-8")) > maximum or any(ord(character) < 32 for character in value):
        raise ComplianceEvidenceError(f"{name} is invalid or exceeds {maximum} UTF-8 bytes.")


def _texts(values: object, name: str, minimum: int, maximum: int, maximum_bytes: int) -> None:
    if not isinstance(values, tuple) or not minimum <= len(values) <= maximum:
        raise ComplianceEvidenceError(f"{name} must contain {minimum} through {maximum} entries.")
    for value in values: _text(value, name, maximum_bytes)
    if len(values) != len(set(values)): raise ComplianceEvidenceError(f"{name} must not contain duplicates.")


def _date(value: object, name: str) -> None:
    if not isinstance(value, str) or _DATE.fullmatch(value) is None:
        raise ComplianceEvidenceError(f"{name} must use YYYY-MM-DD.")
    try: date.fromisoformat(value)
    except ValueError as exc: raise ComplianceEvidenceError(f"{name} is not a valid calendar date.") from exc


def _sha256(value: object, name: str) -> None:
    if not isinstance(value, str) or _SHA256.fullmatch(value) is None:
        raise ComplianceEvidenceError(f"{name} must be lowercase SHA-256.")


def _decimal(value: object, name: str) -> None:
    if not isinstance(value, str) or not value or value != value.strip():
        raise ComplianceEvidenceError(f"{name} must be a canonical decimal string.")
    try: parsed = Decimal(value)
    except InvalidOperation as exc: raise ComplianceEvidenceError(f"{name} is invalid.") from exc
    if not parsed.is_finite() or str(parsed) != value:
        raise ComplianceEvidenceError(f"{name} must be a finite canonical decimal string.")


def _typed_tuple(values: object, record_type: type, name: str, minimum: int, maximum: int) -> None:
    if not isinstance(values, tuple) or not minimum <= len(values) <= maximum or any(not isinstance(item, record_type) for item in values):
        raise ComplianceEvidenceError(f"{name} is invalid or outside its count limit.")


def _unique(values: object, name: str) -> None:
    items = tuple(values)
    if len(items) != len(set(items)): raise ComplianceEvidenceError(f"{name} must be unique.")


def _mapping(value: object, name: str) -> dict[str, Any]:
    if not isinstance(value, dict) or any(not isinstance(key, str) for key in value):
        raise ComplianceEvidenceError(f"{name} must be a JSON object with string keys.")
    return value


def _exact_keys(value: Mapping[str, object], record_type: type, name: str) -> None:
    if set(value) != set(record_type.__dataclass_fields__):
        raise ComplianceEvidenceError(f"{name} fields do not match the supported schema.")


def _json_array(value: object, name: str) -> list[object]:
    if not isinstance(value, list):
        raise ComplianceEvidenceError(f"{name} must be a JSON array.")
    return value


def _enum(enum_type: type[Enum], value: object, name: str) -> Enum:
    if not isinstance(value, str):
        raise ComplianceEvidenceError(f"{name} is invalid.")
    try:
        return enum_type(value)
    except ValueError as exc:
        raise ComplianceEvidenceError(f"{name} is unsupported.") from exc
