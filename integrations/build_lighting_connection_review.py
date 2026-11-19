"""Reproduce the exact reviewed connection record for lighting.dwg acceptance."""

from __future__ import annotations

import hashlib
import json
import os
import tempfile
from dataclasses import asdict
from pathlib import Path

from agent.exceptions import AutoCADContextError
from integrations.autocad_context import map_context_to_electrical_model, parse_autocad_context
from integrations.autocad_project_conventions import load_project_convention_profile
from integrations.autocad_project_topology import (
    assemble_reviewed_connection_anchors,
    load_connection_anchor_capture,
    parse_reviewed_route_membership,
    parse_reviewed_topology,
)
from integrations.autocad_review_channel import (
    ConnectionPairReviewRequest,
    EnqueuedReview,
    load_connection_pair_review_result,
    validate_review_result_binding,
)


DEVICE_CONTEXT_ID = "8683c155257048be8784dafc4a977b28"
ROUTE_CONTEXT_ID = "9446c5a6a484430d9dfd1753c1c21628"
ANNOTATION_REVIEWS = (
    ("e682307eecd7490baa26f66f5d73798d", "1A1C3109", "start", "1A1C2F8D"),
    ("7a60903e0a1f43649a901c6702577421", "1A1C3093", "start", "1A1C2F8D"),
)


def build(project_root: Path) -> Path:
    """Validate every exact input and atomically retain the self-hashed review."""
    root = project_root.resolve(strict=True)
    workspace = (root / "workspace").resolve(strict=True)
    autocad = workspace / ".ai-engineer" / "autocad"
    contexts = autocad / "context" / "inbox"
    conventions = autocad / "project-conventions"
    topology_root = autocad / "project-topology"
    catalog = root / "plugins" / "electrical_symbols" / "catalog.json"

    device_path = contexts / f"{DEVICE_CONTEXT_ID}.json"
    route_path = contexts / f"{ROUTE_CONTEXT_ID}.json"
    device_bytes = device_path.read_bytes()
    route_bytes = route_path.read_bytes()
    device_digest = hashlib.sha256(device_bytes).hexdigest()
    route_digest = hashlib.sha256(route_bytes).hexdigest()
    device_context = parse_autocad_context(json.loads(device_bytes.decode("utf-8")))
    route_context = parse_autocad_context(json.loads(route_bytes.decode("utf-8")))
    device_profile = load_project_convention_profile(conventions / "lighting-8683c155.json")
    route_profile = load_project_convention_profile(conventions / "lighting-routes-9446c5a6.json")
    device_model = map_context_to_electrical_model(
        device_context,
        evidence_sha256=device_digest,
        symbol_catalog_path=catalog,
        conventions=device_profile,
    )
    route_model = map_context_to_electrical_model(
        route_context,
        evidence_sha256=route_digest,
        symbol_catalog_path=catalog,
        conventions=route_profile,
    )
    topology = parse_reviewed_topology(
        json.loads((topology_root / "lighting-db3-ga2-circuit-4.json").read_text(encoding="utf-8"))
    )
    route_membership = parse_reviewed_route_membership(
        json.loads((topology_root / "lighting-db3-ga2-circuit-4-routes.json").read_text(encoding="utf-8"))
    )

    captures = []
    for path in sorted((autocad / "connection-anchors" / "inbox").glob("*.json")):
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        captures.append((load_connection_anchor_capture(path, expected_sha256=digest), digest))
    captures.sort(key=lambda item: (item[0].selection.route_object_id, item[0].selection.endpoint))

    annotation_handles = set(route_membership.annotation_route_object_ids)
    if annotation_handles != {"1A1C3109", "1A1C3093"}:
        raise AutoCADContextError("Reviewed annotation routes do not match the exact engineer classification.")
    channel = autocad / "review-channel"
    for request_id, route_handle, endpoint, device_handle in ANNOTATION_REVIEWS:
        request_path = channel / "processed" / f"{request_id}.json"
        request_bytes = request_path.read_bytes()
        queued = EnqueuedReview(
            ConnectionPairReviewRequest(**json.loads(request_bytes.decode("utf-8"))),
            request_path,
            hashlib.sha256(request_bytes).hexdigest(),
        )
        result_path = channel / "outbox" / f"{request_id}.json"
        result_bytes = result_path.read_bytes()
        result_digest = hashlib.sha256(result_bytes).hexdigest()
        result = load_connection_pair_review_result(result_path, expected_sha256=result_digest)
        validate_review_result_binding(result, queued)
        if (
            result.status != "rejected"
            or result.route_object_id != route_handle
            or result.endpoint != endpoint
            or result.device_object_id != device_handle
        ):
            raise AutoCADContextError("Annotation-route evidence does not match the reviewed decision.")

    review = assemble_reviewed_connection_anchors(
        device_model=device_model,
        route_model=route_model,
        topology=topology,
        route_membership=route_membership,
        captures=tuple(captures),
        unresolved_endpoints=(),
        review_id="topology:lighting-db3-ga2-circuit-4-connections",
        reviewed_by="engineer:hamada-gamal",
        reviewed_on="2026-08-16",
        assumptions=(
            "Every one of the 14 electrical-route endpoints is represented exactly once.",
            "All connection decisions use clear endpoint-marker-v2 or earlier individually clear capture evidence.",
            "Two reviewed polylines form a circuit-number arrow/leader and are excluded from cable connectivity and length.",
            "Route crossings, control tags, cable type, installed cable length, and protection are not inferred.",
        ),
    )
    encoded = json.dumps(asdict(review), ensure_ascii=False, indent=2, sort_keys=True).encode("utf-8") + b"\n"
    target = topology_root / "lighting-db3-ga2-circuit-4-connections.json"
    _write_or_verify(target, encoded)
    print(f"Review SHA-256: {review.review_sha256}")
    print(f"File SHA-256: {hashlib.sha256(encoded).hexdigest()}")
    print(f"Connected endpoints: {len(review.connections)}")
    print(f"Unresolved endpoints: {len(review.unresolved_endpoints)}")
    print("Complete endpoint accounting: true")
    print("Drawing mutation performed: false")
    readiness_path = _build_calculation_readiness(
        topology_root=topology_root,
        source_drawing_sha256=topology.source_drawing_sha256,
        device_context_sha256=device_digest,
        route_context_sha256=route_digest,
        connection_review_sha256=review.review_sha256,
        device_count=len(device_model.devices),
        route_count=len(route_membership.route_object_ids),
        connected_endpoint_count=len(review.connections),
        unresolved_endpoint_count=len(review.unresolved_endpoints),
        load_count=len(device_model.loads),
        cable_count=len(device_model.cables),
        conductor_count=len(device_model.conductors),
        protection_count=len(device_model.protection_devices),
    )
    print(f"Calculation readiness: {readiness_path}")
    return target


def _build_calculation_readiness(
    *,
    topology_root: Path,
    source_drawing_sha256: str,
    device_context_sha256: str,
    route_context_sha256: str,
    connection_review_sha256: str,
    device_count: int,
    route_count: int,
    connected_endpoint_count: int,
    unresolved_endpoint_count: int,
    load_count: int,
    cable_count: int,
    conductor_count: int,
    protection_count: int,
) -> Path:
    findings = [
        {"status": "PASS", "requirement": "drawing-units", "evidence": "AutoCAD INSUNITS=4; reviewed unit is millimeter."},
        {"status": "PASS", "requirement": "panel-circuit-membership", "evidence": f"{device_count} reviewed devices belong to DB3-GA2 / circuit 4."},
        {"status": "PASS", "requirement": "route-endpoint-accounting", "evidence": f"{connected_endpoint_count} connected and {unresolved_endpoint_count} unresolved endpoints account for all {route_count * 2}."},
        {"status": "UNKNOWN", "requirement": "connected-loads", "evidence": f"The exact model contains {load_count} typed load objects; fixture wattages are not present."},
        {"status": "UNKNOWN", "requirement": "nominal-voltage-phase-power-factor", "evidence": "No reviewed circuit voltage, phase system, power factor, efficiency, or demand/diversity inputs are bound to this circuit."},
        {"status": "UNKNOWN", "requirement": "cable-conductors", "evidence": f"The exact model contains {cable_count} cables and {conductor_count} conductors; type, cores, material, size, and impedance are not present."},
        {"status": "UNKNOWN", "requirement": "installed-cable-length", "evidence": f"Observed centerline is not installed length and {unresolved_endpoint_count} route endpoints remain unresolved."},
        {"status": "UNKNOWN", "requirement": "protection", "evidence": f"The exact model contains {protection_count} typed protective devices; rating, curve, poles, and breaking capacity are absent."},
        {"status": "UNKNOWN", "requirement": "project-code-profile", "evidence": "No project-specific Egyptian/IEC edition and clause set is pinned to this lighting acceptance record."},
    ]
    payload = {
        "schema_version": 1,
        "readiness_version": "autocad.real_project_calculation_readiness.v1",
        "report_id": "readiness:lighting-db3-ga2-circuit-4",
        "source_drawing_sha256": source_drawing_sha256,
        "device_context_sha256": device_context_sha256,
        "route_context_sha256": route_context_sha256,
        "connection_review_sha256": connection_review_sha256,
        "panel_name": "DB3-GA2",
        "circuit_name": "4",
        "findings": findings,
        "ready_for_deterministic_calculation": False,
        "calculation_performed": False,
        "compliance_evaluation_performed": False,
        "drawing_mutation_performed": False,
    }
    canonical = json.dumps(payload, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
    payload["report_sha256"] = hashlib.sha256(canonical).hexdigest()
    encoded = json.dumps(payload, ensure_ascii=False, indent=2, sort_keys=True).encode("utf-8") + b"\n"
    target = topology_root / "lighting-db3-ga2-circuit-4-calculation-readiness.json"
    _write_or_verify(target, encoded)
    return target


def _write_or_verify(path: Path, content: bytes) -> None:
    if path.exists():
        if path.read_bytes() != content:
            raise AutoCADContextError(f"Existing connection review differs and was not overwritten: {path}")
        return
    descriptor, name = tempfile.mkstemp(dir=path.parent, prefix=f".{path.name}.", suffix=".tmp")
    temporary = Path(name)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        if temporary.exists():
            temporary.unlink()


if __name__ == "__main__":
    build(Path(__file__).resolve().parents[1])
