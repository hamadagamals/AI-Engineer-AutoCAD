"""Build hash-bound M22 live-review requests from a downloaded official source."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import tempfile
from pathlib import Path
from typing import Any

from core.code_profile import (
    CodeAuthority,
    CodeSourceIdentity,
    EvidenceAccessBasis,
    create_project_code_profile,
)
from core.compliance_evidence import (
    ComparisonOperator,
    create_clause_evidence,
    create_compliance_rule,
    create_engineering_fact,
)


def build(source_path: Path, output_directory: Path) -> dict[str, Any]:
    """Create PASS/FAIL/UNKNOWN/CONFLICT acceptance requests and a hash manifest."""
    source = source_path.resolve(strict=True)
    content = source.read_bytes()
    if not 1 <= len(content) <= 2_000_000:
        raise ValueError("Official source must contain 1 through 2000000 bytes.")
    if b"3.05 m" not in content:
        raise ValueError("Official source does not contain the reviewed 3.05 m marker.")
    source_sha256 = hashlib.sha256(content).hexdigest()
    code_source = CodeSourceIdentity(
        source_id="source:osha-1910-305-live",
        authority_name="United States Occupational Safety and Health Administration",
        standard_name="29 CFR 1910.305 electrical wiring methods",
        edition="e-CFR retrieved 2026-08-12",
        publication_date="2007-02-14",
        effective_from="2007-08-13",
        effective_to=None,
        access_basis=EvidenceAccessBasis.OFFICIAL_PUBLIC,
        locator=(
            "https://www.osha.gov/laws-regs/regulations/standardnumber/1910/1910.305"
        ),
        retrieved_on="2026-08-12",
        content_sha256=source_sha256,
    )
    profile = create_project_code_profile(
        profile_id="code-profile:osha-live-acceptance",
        project_id="project:m22-live-acceptance",
        jurisdiction="US OSHA acceptance fixture; not active Egyptian project authority",
        project_type="Milestone 22 read-only functional acceptance fixture",
        sources=(code_source,),
        authorities=(
            CodeAuthority(
                "authority:osha",
                1,
                code_source.source_id,
                ("temporary_open_branch_circuit_support_spacing",),
            ),
        ),
        requirements=(),
        reviewed_by="engineer:gamal",
        reviewed_on="2026-08-12",
        assumptions=(
            "Functional acceptance only; this US rule is not authority for the Egyptian project.",
            "No full protected publication text is stored in the compliance request.",
        ),
    )
    clause = create_clause_evidence(
        profile,
        evidence_id="evidence:osha-1910-305-a2-vb",
        authority_id="authority:osha",
        clause_locator="29 CFR 1910.305(a)(2)(v)(B)",
        topic="temporary_open_branch_circuit_support_spacing",
        rule_summary=(
            "For this reviewed temporary-wiring case, open conductors fastened at ceiling "
            "height use a maximum support spacing of 3.05 m."
        ),
        reviewed_by="engineer:gamal",
        assumptions=(
            "Applicability is intentionally limited to this functional acceptance fixture.",
        ),
    )
    rule = create_compliance_rule(
        evidence=clause,
        rule_id="rule:maximum-support-spacing",
        fact_id="fact:maximum-observed-support-spacing",
        operator=ComparisonOperator.LESS_THAN_OR_EQUAL,
        required_value="3.05",
        unit="meter",
        formula="maximum_observed_support_spacing_m <= 3.05",
        assumptions=("The observed spacing fact is supplied by a reviewed fixture record.",),
    )

    output = output_directory.resolve()
    output.mkdir(parents=True, exist_ok=True)
    manifest_cases: dict[str, Any] = {}
    facts_by_case: dict[str, list[Any]] = {}
    for case, values in (
        ("pass", ("3.00",)),
        ("fail", ("3.10",)),
        ("unknown", ()),
        ("conflict", ("3.00", "3.10")),
    ):
        facts = []
        observations = []
        for index, value in enumerate(values, start=1):
            observation = {
                "schema_version": 1,
                "case": case,
                "observation_index": index,
                "fact_id": "fact:maximum-observed-support-spacing",
                "value": value,
                "unit": "meter",
                "reviewed_by": "engineer:gamal",
                "reviewed_on": "2026-08-12",
                "fixture_only": True,
            }
            observation_sha256 = _sha256(observation)
            observations.append({**observation, "observation_sha256": observation_sha256})
            facts.append(
                create_engineering_fact(
                    fact_id="fact:maximum-observed-support-spacing",
                    context_id=f"context:m22-{case}-{index}",
                    source_result_sha256=observation_sha256,
                    value=value,
                    unit="meter",
                    basis_reference=f"Reviewed M22 live acceptance observation {case}:{index}.",
                    reviewed_by="engineer:gamal",
                    assumptions=("Fixture observation only; no production drawing fact is claimed.",),
                )
            )
        request = {
            "evaluation_id": f"compliance:m22-live-{case}",
            "profile": profile.to_dict(),
            "clauses": [clause.to_dict()],
            "rules": [rule.to_dict()],
            "facts": [item.to_dict() for item in facts],
            "evaluated_by": "engineer:gamal",
            "evaluated_on": "2026-08-12",
            "assumptions": [
                "Functional live acceptance only; not an Egyptian project compliance decision."
            ],
        }
        request_path = output / f"m22-{case}-request.json"
        _atomic_json(request_path, request)
        facts_by_case[case] = observations
        manifest_cases[case] = {
            "request_path": request_path.name,
            "request_sha256": hashlib.sha256(request_path.read_bytes()).hexdigest(),
            "expected_outcome": case,
            "observations": observations,
        }
    manifest = {
        "schema_version": 1,
        "acceptance_version": "m22.live_acceptance.v1",
        "official_source_path": str(source),
        "official_source_bytes": len(content),
        "official_source_sha256": source_sha256,
        "official_source_locator": code_source.locator,
        "profile_sha256": profile.profile_sha256,
        "clause_evidence_sha256": clause.evidence_sha256,
        "rule_sha256": rule.rule_sha256,
        "cases": manifest_cases,
        "provider_retrieval_performed_by_evaluator": False,
        "regulatory_approval_performed": False,
        "drawing_mutation_performed": False,
    }
    manifest_path = output / "manifest.json"
    _atomic_json(manifest_path, manifest)
    return {**manifest, "manifest_path": str(manifest_path)}


def _sha256(value: object) -> str:
    encoded = json.dumps(
        value, ensure_ascii=False, sort_keys=True, separators=(",", ":")
    ).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest()


def _atomic_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary = tempfile.mkstemp(prefix=f".{path.name}.", dir=path.parent)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as stream:
            json.dump(value, stream, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    except Exception:
        try:
            os.unlink(temporary)
        except FileNotFoundError:
            pass
        raise


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    arguments = parser.parse_args()
    result = build(arguments.source, arguments.output)
    print(json.dumps(result, ensure_ascii=False, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
