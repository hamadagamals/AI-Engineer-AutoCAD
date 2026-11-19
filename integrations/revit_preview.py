"""Hash-bound, non-mutating Revit placement preview artifacts."""

from __future__ import annotations

import hashlib
import json
import math
import os
import uuid
from datetime import UTC, datetime, timedelta
from pathlib import Path

from agent.exceptions import RevitIntegrationError


MAXIMUM_PLACEMENTS = 32
MAXIMUM_COORDINATE_MM = 1_000_000.0
PLACEMENT_HOST_REQUIREMENTS = {
    "OneLevelBased": "level",
    "OneLevelBasedHosted": "hosted_element_unresolved",
    "TwoLevelsBased": "two_levels",
    "WorkPlaneBased": "work_plane_or_face",
    "ViewBased": "view",
    "CurveBased": "curve",
    "CurveBasedDetail": "detail_curve",
}


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def _artifact_hash(value: dict[str, object]) -> str:
    payload = {key: item for key, item in value.items() if key != "preview_sha256"}
    encoded = json.dumps(payload, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
    return hashlib.sha256(encoded.encode("utf-8")).hexdigest().upper()


def create_placement_preview(
    *,
    workspace_root: Path,
    source_model: Path,
    expected_source_sha256: str,
    document_title: str,
    family_name: str,
    type_name: str,
    symbol_id: int,
    family_placement_type: str,
    host_requirement: str,
    level_name: str,
    level_id: int,
    reference_element_id: int,
    elevation_from_level_mm: float,
    points_mm: list[tuple[float, float, float]],
    output_path: Path,
    rotations_degrees: list[float] | None = None,
    host_faces: list[dict[str, object]] | None = None,
    expires_minutes: int = 30,
) -> dict[str, object]:
    """Create one bounded review-only placement plan; never open or mutate Revit."""
    root = workspace_root.expanduser().resolve(strict=True)
    source = source_model.expanduser().resolve(strict=True)
    output = output_path.expanduser().resolve(strict=False)
    if not root.is_dir() or not source.is_file() or source.is_symlink():
        raise RevitIntegrationError("Revit preview source must be a regular workspace file.")
    if not source.is_relative_to(root) or not output.is_relative_to(root):
        raise RevitIntegrationError("Revit preview paths must stay inside the workspace.")
    actual_hash = _sha256(source)
    if actual_hash != expected_source_sha256.upper():
        raise RevitIntegrationError("Revit preview source SHA-256 does not match.")
    texts = (document_title, family_name, type_name)
    if any(not item.strip() or len(item) > 260 for item in texts):
        raise RevitIntegrationError("Revit placement identity is invalid.")
    if not isinstance(symbol_id, int) or isinstance(symbol_id, bool) or symbol_id <= 0:
        raise RevitIntegrationError("Revit placement symbol id is invalid.")
    if not isinstance(level_id, int) or isinstance(level_id, bool) or level_id < -1:
        raise RevitIntegrationError("Revit placement level id is invalid.")
    if (
        not isinstance(reference_element_id, int)
        or isinstance(reference_element_id, bool)
        or reference_element_id <= 0
    ):
        raise RevitIntegrationError("Revit placement reference element id is invalid.")
    if (
        not isinstance(elevation_from_level_mm, (int, float))
        or isinstance(elevation_from_level_mm, bool)
        or not math.isfinite(elevation_from_level_mm)
        or abs(elevation_from_level_mm) > MAXIMUM_COORDINATE_MM
    ):
        raise RevitIntegrationError("Revit placement elevation from level is invalid.")
    if PLACEMENT_HOST_REQUIREMENTS.get(family_placement_type) != host_requirement:
        raise RevitIntegrationError("Revit placement mode and host requirement do not match.")
    level_based = family_placement_type == "OneLevelBased"
    face_based = family_placement_type == "WorkPlaneBased"
    if level_based and (level_id <= 0 or not level_name.strip() or len(level_name) > 260):
        raise RevitIntegrationError("Level-based Revit placement requires one exact level.")
    if not level_based and not face_based:
        raise RevitIntegrationError("This Revit preview supports only level- or face-based placement.")
    if not 1 <= len(points_mm) <= MAXIMUM_PLACEMENTS:
        raise RevitIntegrationError("Revit preview requires 1..32 placements.")
    if face_based:
        if not isinstance(host_faces, list) or len(host_faces) != len(points_mm):
            raise RevitIntegrationError("Face-based Revit preview requires one exact host face per placement.")
    elif host_faces is not None:
        raise RevitIntegrationError("Level-based Revit preview cannot contain host-face bindings.")
    rotations = [0.0] * len(points_mm) if rotations_degrees is None else rotations_degrees
    if len(rotations) != len(points_mm):
        raise RevitIntegrationError("Revit preview requires one rotation per placement.")
    placements: list[dict[str, object]] = []
    for index, (point, rotation) in enumerate(zip(points_mm, rotations, strict=True), start=1):
        if len(point) != 3 or any(
            not isinstance(value, (int, float))
            or isinstance(value, bool)
            or not math.isfinite(value)
            or abs(value) > MAXIMUM_COORDINATE_MM
            for value in point
        ):
            raise RevitIntegrationError("Revit preview coordinate is invalid.")
        if (
            not isinstance(rotation, (int, float))
            or isinstance(rotation, bool)
            or not math.isfinite(float(rotation))
            or not 0 <= float(rotation) < 360
        ):
            raise RevitIntegrationError(
                "Revit placement rotation must be normalized within 0 <= angle < 360 degrees."
            )
        placement: dict[str, object] = {
            "index": index,
            "x_mm": round(float(point[0]), 3),
            "y_mm": round(float(point[1]), 3),
            "z_mm": round(float(point[2]), 3),
            "rotation_degrees": round(float(rotation), 6),
        }
        if face_based:
            placement["host_face"] = _validate_host_face(host_faces[index - 1])
        placements.append(placement)
    if family_placement_type == "OneLevelBased" and any(
        placement["z_mm"] != placements[0]["z_mm"] for placement in placements[1:]
    ):
        raise RevitIntegrationError(
            "OneLevelBased placements on one level must share one physical elevation."
        )
    if not 1 <= expires_minutes <= 120:
        raise RevitIntegrationError("Revit preview expiry must be within 1..120 minutes.")
    created = datetime.now(UTC)
    artifact: dict[str, object] = {
        "schema_version": 1,
        "preview_id": str(uuid.uuid4()),
        "created_utc": created.isoformat(),
        "expires_utc": (created + timedelta(minutes=expires_minutes)).isoformat(),
        "source_file": source.name,
        "source_sha256": actual_hash,
        "document_title": document_title.strip(),
        "action": "place_family_instances",
        "family_name": family_name.strip(),
        "type_name": type_name.strip(),
        "symbol_id": symbol_id,
        "family_placement_type": family_placement_type,
        "host_requirement": host_requirement,
        "level_name": level_name.strip(),
        "level_id": level_id,
        "reference_element_id": reference_element_id,
        "elevation_from_level_mm": round(float(elevation_from_level_mm), 3),
        "placements": placements,
        "status": "review_required",
        "execution_allowed": False,
        "provider_egress_performed": False,
        "document_mutation_performed": False,
        "preview_sha256": "",
    }
    artifact["preview_sha256"] = _artifact_hash(artifact)
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_name(f".{output.name}.{uuid.uuid4().hex}.tmp")
    temporary.write_text(json.dumps(artifact, ensure_ascii=False, indent=2), encoding="utf-8")
    os.replace(temporary, output)
    return artifact


def _validate_host_face(value: object) -> dict[str, object]:
    if not isinstance(value, dict) or set(value) != {
        "kind", "link_instance_id", "linked_element_id", "linked_document_title",
        "linked_category_key", "face_stable_reference",
    }:
        raise RevitIntegrationError("Revit host-face binding fields are invalid.")
    for field in ("link_instance_id", "linked_element_id"):
        identity = value.get(field)
        if not isinstance(identity, int) or isinstance(identity, bool) or identity <= 0:
            raise RevitIntegrationError("Revit host-face identity is invalid.")
    title = value.get("linked_document_title")
    stable = value.get("face_stable_reference")
    if (
        value.get("kind") != "linked_face"
        or value.get("linked_category_key") != "ceilings"
        or not isinstance(title, str) or not title.strip() or len(title) > 260
        or not isinstance(stable, str) or not stable.strip() or len(stable) > 4096
    ):
        raise RevitIntegrationError("Revit host face must be an exact linked ceiling face.")
    return dict(value)


def load_placement_preview(path: Path, *, workspace_root: Path) -> dict[str, object]:
    root = workspace_root.expanduser().resolve(strict=True)
    candidate = path.expanduser().resolve(strict=True)
    if not candidate.is_file() or candidate.is_symlink() or not candidate.is_relative_to(root):
        raise RevitIntegrationError("Revit preview escaped the workspace.")
    value = json.loads(candidate.read_text(encoding="utf-8"))
    if not isinstance(value, dict) or value.get("schema_version") != 1:
        raise RevitIntegrationError("Revit preview schema is invalid.")
    if value.get("preview_sha256") != _artifact_hash(value):
        raise RevitIntegrationError("Revit preview hash is invalid.")
    if value.get("execution_allowed") is not False or value.get("document_mutation_performed") is not False:
        raise RevitIntegrationError("Revit preview unexpectedly permits execution or mutation.")
    if value.get("action") != "place_family_instances" or value.get("status") != "review_required":
        raise RevitIntegrationError("Revit preview action or status is invalid.")
    placements = value.get("placements")
    if not isinstance(placements, list) or not 1 <= len(placements) <= MAXIMUM_PLACEMENTS:
        raise RevitIntegrationError("Revit preview placements are invalid.")
    expected_elevation: float | None = None
    for expected_index, placement in enumerate(placements, start=1):
        if not isinstance(placement, dict) or placement.get("index") != expected_index:
            raise RevitIntegrationError("Revit preview placement order is invalid.")
        coordinates = (placement.get("x_mm"), placement.get("y_mm"), placement.get("z_mm"))
        # Schema-v1 previews created before rotation support remain verifiable as zero degrees.
        rotation = placement.get("rotation_degrees", 0.0)
        if any(
            not isinstance(coordinate, (int, float))
            or isinstance(coordinate, bool)
            or not math.isfinite(float(coordinate))
            or abs(float(coordinate)) > MAXIMUM_COORDINATE_MM
            for coordinate in coordinates
        ):
            raise RevitIntegrationError("Revit preview coordinate is invalid.")
        if (
            not isinstance(rotation, (int, float))
            or isinstance(rotation, bool)
            or not math.isfinite(float(rotation))
            or not 0 <= float(rotation) < 360
        ):
            raise RevitIntegrationError("Revit preview rotation is invalid.")
        if expected_elevation is None:
            expected_elevation = float(coordinates[2])
        elif value.get("family_placement_type") == "OneLevelBased" and float(coordinates[2]) != expected_elevation:
            raise RevitIntegrationError(
                "OneLevelBased placements on one level must share one physical elevation."
            )
        host_face = placement.get("host_face")
        if value.get("family_placement_type") == "WorkPlaneBased":
            _validate_host_face(host_face)
        elif host_face is not None:
            raise RevitIntegrationError("Level-based Revit preview contains a host-face binding.")
    symbol_id = value.get("symbol_id")
    level_id = value.get("level_id")
    reference_element_id = value.get("reference_element_id")
    elevation_from_level_mm = value.get("elevation_from_level_mm")
    placement_type = value.get("family_placement_type")
    if not isinstance(symbol_id, int) or isinstance(symbol_id, bool) or symbol_id <= 0:
        raise RevitIntegrationError("Revit preview symbol id is invalid.")
    if not isinstance(level_id, int) or isinstance(level_id, bool) or level_id < -1:
        raise RevitIntegrationError("Revit preview level id is invalid.")
    if (
        not isinstance(reference_element_id, int)
        or isinstance(reference_element_id, bool)
        or reference_element_id <= 0
    ):
        raise RevitIntegrationError("Revit preview reference element id is invalid.")
    if (
        not isinstance(elevation_from_level_mm, (int, float))
        or isinstance(elevation_from_level_mm, bool)
        or not math.isfinite(float(elevation_from_level_mm))
        or abs(float(elevation_from_level_mm)) > MAXIMUM_COORDINATE_MM
    ):
        raise RevitIntegrationError("Revit preview elevation from level is invalid.")
    if PLACEMENT_HOST_REQUIREMENTS.get(placement_type) != value.get("host_requirement"):
        raise RevitIntegrationError("Revit preview placement metadata is invalid.")
    if placement_type == "OneLevelBased" and (
        level_id <= 0
        or not isinstance(value.get("level_name"), str)
        or not value["level_name"].strip()
    ):
        raise RevitIntegrationError("Level-based Revit preview lacks its exact level.")
    if placement_type not in {"OneLevelBased", "WorkPlaneBased"}:
        raise RevitIntegrationError("Revit preview placement type is not supported.")
    return value
