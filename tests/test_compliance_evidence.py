"""Tests for deterministic, hash-bound, fail-closed compliance evidence."""

from __future__ import annotations

import hashlib
from dataclasses import replace

import pytest

from agent.exceptions import ComplianceEvidenceError
from core.code_profile import (
    CodeAuthority,
    CodeSourceIdentity,
    EvidenceAccessBasis,
    create_project_code_profile,
)
from core.compliance_evidence import (
    ComparisonOperator,
    ComplianceOutcome,
    create_clause_evidence,
    create_compliance_rule,
    create_engineering_fact,
    evaluate_compliance,
    parse_clause_evidence,
    parse_compliance_rule,
    parse_engineering_fact,
)
from core.code_profile import parse_project_code_profile
from integrations.build_m22_live_acceptance import build as build_live_acceptance


def _profile(*, profile_id: str = "code-profile:test", content: bytes = b"reviewed-source"):
    source = CodeSourceIdentity(
        source_id="source:test-2026",
        authority_name="Reviewed test authority",
        standard_name="Licensed project test standard",
        edition="2026",
        publication_date="2026-01-01",
        effective_from="2026-06-01",
        effective_to=None,
        access_basis=EvidenceAccessBasis.USER_PROVIDED_LICENSED,
        locator="document:licensed-test-copy",
        retrieved_on="2026-08-12",
        content_sha256=hashlib.sha256(content).hexdigest(),
    )
    return create_project_code_profile(
        profile_id=profile_id,
        project_id="project:test",
        jurisdiction="Test jurisdiction",
        project_type="Low-voltage test fixture",
        sources=(source,),
        authorities=(CodeAuthority("authority:test", 1, source.source_id, ("voltage_drop",)),),
        requirements=(),
        reviewed_by="engineer:gamal",
        reviewed_on="2026-08-12",
        assumptions=("Synthetic metadata only; no protected publication text is included.",),
    )


def _clause(profile):
    return create_clause_evidence(
        profile,
        evidence_id="evidence:voltage-drop",
        authority_id="authority:test",
        clause_locator="reviewed-test-clause-1",
        topic="voltage_drop",
        rule_summary="Reviewed synthetic fixture limit for deterministic tests.",
        reviewed_by="engineer:gamal",
        assumptions=("The reviewer mapped the fixture rule to the calculated fact.",),
    )


def _rule(clause, *, required: str = "5"):
    return create_compliance_rule(
        evidence=clause,
        rule_id="rule:voltage-drop",
        fact_id="fact:circuit-1-voltage-drop",
        operator=ComparisonOperator.LESS_THAN_OR_EQUAL,
        required_value=required,
        unit="percent",
        formula="calculated_voltage_drop_percent <= reviewed_limit_percent",
        assumptions=("The exact upstream calculation result is reviewed.",),
    )


def _fact(*, value: str = "4.2", source_hash: bytes = b"calculation"):
    return create_engineering_fact(
        fact_id="fact:circuit-1-voltage-drop",
        context_id="context:circuit-1",
        source_result_sha256=hashlib.sha256(source_hash).hexdigest(),
        value=value,
        unit="percent",
        basis_reference="Deterministic voltage-drop result for circuit:1.",
        reviewed_by="engineer:gamal",
        assumptions=("Route length and conductor inputs were already reviewed upstream.",),
    )


def _evaluate(profile, clause, rule, facts):
    return evaluate_compliance(
        profile,
        evaluation_id="compliance:test-1",
        clauses=(clause,),
        rules=(rule,),
        facts=facts,
        evaluated_by="engineer:gamal",
        evaluated_on="2026-08-12",
        assumptions=("This result is engineering review evidence, not regulatory approval.",),
    )


def test_pass_and_fail_are_deterministic_and_make_no_external_claims() -> None:
    profile = _profile()
    clause = _clause(profile)
    rule = _rule(clause)
    passed = _evaluate(profile, clause, rule, (_fact(),))
    failed = _evaluate(profile, clause, rule, (_fact(value="5.1"),))

    assert passed.findings[0].outcome is ComplianceOutcome.PASS
    assert passed.complete is True
    assert passed.all_requirements_satisfied is True
    assert failed.findings[0].outcome is ComplianceOutcome.FAIL
    assert failed.complete is True
    assert failed.all_requirements_satisfied is False
    assert passed.provider_retrieval_performed is False
    assert passed.regulatory_approval_performed is False
    assert passed.drawing_mutation_performed is False
    assert len(passed.result_sha256) == 64


def test_missing_fact_or_clause_is_unknown_and_fails_closed() -> None:
    profile = _profile()
    clause = _clause(profile)
    rule = _rule(clause)
    missing_fact = _evaluate(profile, clause, rule, ())
    missing_clause = evaluate_compliance(
        profile,
        evaluation_id="compliance:missing-clause",
        clauses=(), rules=(rule,), facts=(_fact(),),
        evaluated_by="engineer:gamal", evaluated_on="2026-08-12",
        assumptions=("Missing evidence must remain visible.",),
    )
    for result in (missing_fact, missing_clause):
        assert result.findings[0].outcome is ComplianceOutcome.UNKNOWN
        assert result.complete is False
        assert result.all_requirements_satisfied is False


def test_duplicate_facts_and_conflicting_rules_are_conflict() -> None:
    profile = _profile()
    clause = _clause(profile)
    rule = _rule(clause)
    fact_conflict = _evaluate(
        profile, clause, rule,
        (_fact(value="4.2", source_hash=b"a"), _fact(value="4.3", source_hash=b"b")),
    )
    competing = create_compliance_rule(
        evidence=clause, rule_id="rule:voltage-drop-other",
        fact_id=rule.fact_id, operator=ComparisonOperator.LESS_THAN_OR_EQUAL,
        required_value="3", unit="percent", formula="actual <= alternate_limit",
        assumptions=("A conflicting reviewed rule was supplied deliberately.",),
    )
    rule_conflict = evaluate_compliance(
        profile, evaluation_id="compliance:rule-conflict", clauses=(clause,),
        rules=(rule, competing), facts=(_fact(),), evaluated_by="engineer:gamal",
        evaluated_on="2026-08-12", assumptions=("Conflicts stop acceptance.",),
    )
    assert fact_conflict.findings[0].outcome is ComplianceOutcome.CONFLICT
    assert {item.outcome for item in rule_conflict.findings} == {ComplianceOutcome.CONFLICT}
    assert fact_conflict.complete is False
    assert rule_conflict.all_requirements_satisfied is False


def test_profile_source_mismatch_and_tampering_are_rejected() -> None:
    profile = _profile()
    clause = _clause(profile)
    rule = _rule(clause)
    other_profile = _profile(profile_id="code-profile:other", content=b"other")
    with pytest.raises(ComplianceEvidenceError, match="different project profile"):
        _evaluate(other_profile, clause, rule, (_fact(),))
    with pytest.raises(ComplianceEvidenceError, match="SHA-256"):
        replace(clause, rule_summary="Tampered after review.")
    with pytest.raises(ComplianceEvidenceError, match="canonical decimal"):
        _fact(value="04.20")


def test_rule_rejects_a_different_clause_hash_even_when_id_matches() -> None:
    profile = _profile()
    clause = _clause(profile)
    rule = _rule(clause)
    with pytest.raises(ComplianceEvidenceError, match="SHA-256"):
        evaluate_compliance(
            profile, evaluation_id="compliance:mismatch", clauses=(clause,),
            rules=(replace(rule, evidence_sha256="f" * 64, rule_sha256=rule.rule_sha256),),
            facts=(_fact(),), evaluated_by="engineer:gamal", evaluated_on="2026-08-12",
            assumptions=("Hash mismatch is rejected.",),
        )


def test_live_acceptance_builder_produces_all_fail_closed_cases(tmp_path) -> None:
    source = tmp_path / "official.html"
    source.write_bytes(b"Official public fixture marker: 3.05 m")
    output = tmp_path / "acceptance"
    manifest = build_live_acceptance(source, output)

    assert set(manifest["cases"]) == {"pass", "fail", "unknown", "conflict"}
    assert manifest["official_source_sha256"] == hashlib.sha256(source.read_bytes()).hexdigest()
    for case, expected in (
        ("pass", ComplianceOutcome.PASS),
        ("fail", ComplianceOutcome.FAIL),
        ("unknown", ComplianceOutcome.UNKNOWN),
        ("conflict", ComplianceOutcome.CONFLICT),
    ):
        request = __import__("json").loads(
            (output / f"m22-{case}-request.json").read_text(encoding="utf-8")
        )
        profile = parse_project_code_profile(request["profile"])
        clauses = tuple(parse_clause_evidence(item) for item in request["clauses"])
        rules = tuple(parse_compliance_rule(item) for item in request["rules"])
        facts = tuple(parse_engineering_fact(item) for item in request["facts"])
        result = evaluate_compliance(
            profile,
            evaluation_id=request["evaluation_id"],
            clauses=clauses,
            rules=rules,
            facts=facts,
            evaluated_by=request["evaluated_by"],
            evaluated_on=request["evaluated_on"],
            assumptions=tuple(request["assumptions"]),
        )
        assert result.findings[0].outcome is expected
        assert result.drawing_mutation_performed is False
