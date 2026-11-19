"""Build a deterministic read-only audit of the lighting drawing classifications.

The audit reports what is known, candidate, conflicting, and unknown.  It does not
promote spatial proximity to electrical connectivity and it deliberately defers panel
schedule and SLD approval to a clearer engineer-selected drawing.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from collections import Counter
from pathlib import Path
from typing import Any


EXPECTED_SOURCE_SHA256 = "1d08ccb0557e740f7d471e006d5accad0675d54b82e45d9aabc64e239ecfb117"
ACCEPTED_INVENTORY_SHA256 = "ee801a46c952a5c33b628092968517b16a35c7ed263ec513a6ee40f1c2ba7159"
ACCEPTED_ROUTE_MEMBERSHIP_SHA256 = "04d99b4bc8f20af3ef03b950c09370aceec48adf14c93388f4ca3abf8594d2e2"
ACCEPTED_INVENTORY_COUNTS = {
    "total_entities": 4848,
    "supported_observations": 4636,
    "unsupported_entities": 212,
    "raw_blocks": 968,
    "raw_route_primitives": 1798,
    "reviewed_devices": 336,
    "reviewed_routes": 7,
    "reviewed_annotation_polylines": 2,
    "route_candidates": 647,
    "unclassified_entities": 3856,
}


def _canonical_json(value: Any) -> bytes:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")


def _sha256(value: Any) -> str:
    return hashlib.sha256(_canonical_json(value)).hexdigest()


def _verify_candidate_evidence(evidence: dict[str, Any]) -> None:
    claimed = evidence.get("result_sha256")
    if not isinstance(claimed, str) or len(claimed) != 64:
        raise ValueError("Candidate evidence result_sha256 is missing or invalid")
    unhashed = dict(evidence)
    unhashed.pop("result_sha256")
    if _sha256(unhashed) != claimed:
        raise ValueError("Candidate evidence SHA-256 validation failed")
    source = evidence.get("source", {})
    if source.get("source_drawing_sha256") != EXPECTED_SOURCE_SHA256:
        raise ValueError("Candidate evidence is not bound to the accepted lighting.dwg source")
    if int(source.get("entity_count", -1)) != ACCEPTED_INVENTORY_COUNTS["total_entities"]:
        raise ValueError("Candidate evidence entity count differs from the accepted inventory")


def build_audit(evidence: dict[str, Any]) -> dict[str, Any]:
    """Aggregate exact classification/review status without drawing mutation."""

    _verify_candidate_evidence(evidence)
    circuits = evidence.get("circuits")
    if not isinstance(circuits, list):
        raise ValueError("Candidate evidence circuits must be a list")

    memberships = Counter(str(row.get("membership_status")) for row in circuits)
    load_sources: Counter[str] = Counter()
    load_confidence: Counter[str] = Counter()
    unresolved_descriptions: Counter[str] = Counter()
    for row in circuits:
        for load in row.get("loads", []):
            load_sources[str(load.get("source_class"))] += 1
            load_confidence[str(load.get("confidence"))] += 1
            if load.get("watts") is None or load.get("power_factor") is None:
                unresolved_descriptions[str(load.get("description") or "UNKNOWN")] += 1

    conflicts = [
        {
            "panel": row["panel"],
            "circuit": row["circuit"],
            "candidate_occurrence_count": row["candidate_occurrence_count"],
            "load_bearing_occurrence_count": row["load_bearing_occurrence_count"],
        }
        for row in circuits
        if row.get("occurrence_conflict")
    ]
    incomplete = [
        {
            "panel": row["panel"],
            "circuit": row["circuit"],
            "unresolved_load_count": row["unresolved_load_count"],
            "known_partial_w": row["known_partial_w"],
        }
        for row in circuits
        if isinstance(row.get("unresolved_load_count"), int) and row["unresolved_load_count"] > 0
    ]

    report: dict[str, Any] = {
        "schema_version": 1,
        "evidence_type": "lighting-classification-read-only-audit",
        "source": {
            "drawing_name": "lighting.dwg",
            "source_drawing_sha256": EXPECTED_SOURCE_SHA256,
            "candidate_evidence_sha256": evidence["result_sha256"],
            "accepted_inventory_sha256": ACCEPTED_INVENTORY_SHA256,
            "accepted_route_membership_sha256": ACCEPTED_ROUTE_MEMBERSHIP_SHA256,
        },
        "safety": {
            "drawing_read_performed": False,
            "drawing_mutation_performed": False,
            "provider_egress_performed": False,
            "spatial_membership_promoted_to_connectivity": False,
        },
        "engineer_scope": {
            "classification_audit_requested": True,
            "panel_schedule_review_deferred_to_clearer_drawing": True,
            "sld_review_deferred_to_clearer_drawing": True,
            "protection_and_cable_confirmation_deferred": True,
        },
        "accepted_inventory": ACCEPTED_INVENTORY_COUNTS,
        "candidate_classification": {
            "panel_count": len(evidence.get("schedule_panels", [])),
            "circuit_reference_count": len(circuits),
            "tag_observation_count": len(evidence.get("tag_observations", [])),
            "membership_status_counts": dict(sorted(memberships.items())),
            "conflicting_circuit_count": len(conflicts),
            "incomplete_load_circuit_count": len(incomplete),
            "reported_unresolved_load_count": sum(item["unresolved_load_count"] for item in incomplete),
            "representative_classified_load_count": sum(len(row.get("loads", [])) for row in circuits),
            "candidate_route_occurrence_total": sum(int(row.get("candidate_route_count") or 0) for row in circuits),
            "candidate_block_occurrence_total": sum(int(row.get("candidate_block_count") or 0) for row in circuits),
            "load_source_counts": dict(sorted(load_sources.items())),
            "load_confidence_counts": dict(sorted(load_confidence.items())),
            "unresolved_load_descriptions": dict(sorted(unresolved_descriptions.items())),
        },
        "priority_review": {
            "conflicting_circuits": conflicts,
            "incomplete_load_circuits": incomplete,
            "non_numeric_or_unassigned_references": evidence.get("review_items", []),
        },
        "interpretation": {
            "reviewed_routes_are_exact": True,
            "route_candidates_are_layer_or_spatial_candidates_only": True,
            "candidate_route_occurrence_total_is_not_a_unique_route_count": True,
            "candidate_block_occurrence_total_is_not_a_unique_device_count": True,
            "unknown_entities_must_remain_unknown_until_separate_review": True,
            "load_totals_are_not_final_for_conflicting_or_incomplete_circuits": True,
        },
    }
    report["result_sha256"] = _sha256(report)
    return report


def render_markdown(report: dict[str, Any]) -> str:
    candidate = report["candidate_classification"]
    inventory = report["accepted_inventory"]
    priorities = report["priority_review"]
    conflicts = ", ".join(
        f"{item['panel']}/{item['circuit']}" for item in priorities["conflicting_circuits"]
    ) or "None"
    incomplete = ", ".join(
        f"{item['panel']}/{item['circuit']} ({item['unresolved_load_count']})"
        for item in priorities["incomplete_load_circuits"]
    ) or "None"
    nonnumeric = ", ".join(
        f"{item['panel']}/{item['reference']}" for item in priorities["non_numeric_or_unassigned_references"]
    ) or "None"
    return f"""# lighting.dwg — Read-only classification audit

Result SHA-256: `{report['result_sha256']}`  
Source drawing SHA-256: `{report['source']['source_drawing_sha256']}`

## Outcome

- The accepted complete inventory contains **{inventory['total_entities']:,}** entities.
- Exactly reviewed classifications: **{inventory['reviewed_devices']} devices**, **{inventory['reviewed_routes']} electrical routes**, and **{inventory['reviewed_annotation_polylines']} annotation polylines**.
- Still candidate/unknown: **{inventory['route_candidates']} route candidates** and **{inventory['unclassified_entities']:,} unclassified entities**.
- The schedule-candidate evidence contains **{candidate['panel_count']} panels**, **{candidate['circuit_reference_count']} circuit references**, and **{candidate['tag_observation_count']} tag observations**.
- It contains **{candidate['conflicting_circuit_count']} conflicting circuit references** and **{candidate['reported_unresolved_load_count']} unresolved loads across {candidate['incomplete_load_circuit_count']} non-conflict circuit rows**.

## Required review order

1. Repeated-occurrence conflicts: {conflicts}.
2. Incomplete load rows (unresolved count): {incomplete}.
3. Non-numeric/unassigned reference: {nonnumeric}.
4. Review route candidates separately; layer/spatial proximity is not electrical connectivity.

## Engineer decision recorded

Panel Schedule and SLD final review, including protection, cables, phase allocation, and final loads, is deferred to another drawing where the evidence is clearer. Missing values in this drawing are not inferred.

## Safety and limitations

This report reused immutable accepted evidence. It did not open or modify AutoCAD, did not send data to a provider, and did not convert candidate paths or occurrence totals into confirmed circuits, unique routes, cable lengths, or installed quantities.
"""


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--evidence", type=Path, required=True)
    parser.add_argument("--json-output", type=Path, required=True)
    parser.add_argument("--markdown-output", type=Path, required=True)
    arguments = parser.parse_args()
    evidence = json.loads(arguments.evidence.read_text(encoding="utf-8"))
    report = build_audit(evidence)
    arguments.json_output.parent.mkdir(parents=True, exist_ok=True)
    arguments.markdown_output.parent.mkdir(parents=True, exist_ok=True)
    arguments.json_output.write_bytes(_canonical_json(report) + b"\n")
    arguments.markdown_output.write_text(render_markdown(report), encoding="utf-8")
    print(json.dumps({"result_sha256": report["result_sha256"], "circuits": report["candidate_classification"]["circuit_reference_count"]}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
