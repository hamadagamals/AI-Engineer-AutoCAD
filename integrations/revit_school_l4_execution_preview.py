"""Create a hash-bound first-batch Revit preview for the school gym L4 layout."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import uuid
from datetime import UTC, datetime, timedelta
from pathlib import Path
from typing import Any


EXPECTED_SOURCE_SHA256 = "D0D0C48E4E800B0045567D23A94A656AA83C7589BF872D6C6E58EB81381809F3"
EXPECTED_FAMILY_SHA256 = "404CCB847E33841D36200D31080A2BB661B1AA941AEFBF170C372055172A139F"
EXPECTED_IES_SHA256 = "648B25C3299A591FEDF87FF72D67DE3FF93D81416467CA6811AEAFEADE6258A8"
MOUNTING_Z_MM = 9_500.0
MAXIMUM_BATCH = 32


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def _artifact_hash(value: dict[str, Any]) -> str:
    payload = {key: item for key, item in value.items() if key != "preview_sha256"}
    encoded = json.dumps(payload, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
    return hashlib.sha256(encoded.encode("utf-8")).hexdigest().upper()


def _resolve_workspace_file(root: Path, path: Path, label: str) -> Path:
    resolved = path.expanduser().resolve(strict=True)
    if not resolved.is_file() or resolved.is_symlink() or not resolved.is_relative_to(root):
        raise ValueError(f"{label} must be a regular file inside the workspace")
    return resolved


def build_preview(
    *,
    workspace_root: Path,
    layout_preview_path: Path,
    source_model_path: Path,
    family_path: Path,
    ies_path: Path,
    output_model_path: Path,
    expires_minutes: int = 120,
) -> dict[str, Any]:
    """Build the first non-executable 23-point batch; never open or modify Revit."""
    root = workspace_root.expanduser().resolve(strict=True)
    layout_path = _resolve_workspace_file(root, layout_preview_path, "Layout preview")
    source_path = _resolve_workspace_file(root, source_model_path, "Source model")
    exact_family_path = _resolve_workspace_file(root, family_path, "L4 family")
    exact_ies_path = _resolve_workspace_file(root, ies_path, "L4 IES")
    output_path = output_model_path.expanduser().resolve(strict=False)
    if output_path.suffix.lower() != ".rvt" or not output_path.is_relative_to(root):
        raise ValueError("Output model must be a new RVT path inside the workspace")
    if output_path.exists() or output_path.is_symlink():
        raise ValueError("Output model already exists; overwrite is not allowed")
    if _sha256(source_path) != EXPECTED_SOURCE_SHA256:
        raise ValueError("School source model SHA-256 changed")
    if _sha256(exact_family_path) != EXPECTED_FAMILY_SHA256:
        raise ValueError("L4 family SHA-256 changed")
    if _sha256(exact_ies_path) != EXPECTED_IES_SHA256:
        raise ValueError("L4 IES SHA-256 changed")
    if not 1 <= expires_minutes <= 120:
        raise ValueError("Preview expiry must be within 1..120 minutes")

    layout = json.loads(layout_path.read_text(encoding="utf-8-sig"))
    all_points = layout.get("placement", {}).get("points")
    room = layout.get("room", {})
    if (
        layout.get("execution_authorized") is not False
        or room.get("element_id") != 221494
        or room.get("level_id") != 30
        or room.get("name") != "GYM 3500"
        or not isinstance(all_points, list)
        or len(all_points) != 45
    ):
        raise ValueError("L4 layout preview identity is invalid")
    if MOUNTING_Z_MM >= float(room["bbox_max"]["z_mm"]):
        raise ValueError("Mounting height must remain below the recorded room top")

    batch_one = [
        point for point in all_points
        if (int(point["grid_column"]) + int(point["grid_row"])) % 2 == 0
    ]
    batch_two = [point for point in all_points if point not in batch_one]
    if len(batch_one) != 23 or len(batch_two) != 22 or len(batch_one) > MAXIMUM_BATCH:
        raise ValueError("The bounded checkerboard batch split is invalid")

    placements = [
        {
            "index": index,
            "point_id": point["point_id"],
            "grid_column": point["grid_column"],
            "grid_row": point["grid_row"],
            "x_mm": point["x_mm"],
            "y_mm": point["y_mm"],
            "z_mm": MOUNTING_Z_MM,
            "rotation_degrees": 0.0,
        }
        for index, point in enumerate(batch_one, start=1)
    ]
    created = datetime.now(UTC)
    artifact: dict[str, Any] = {
        "schema_version": 1,
        "preview_id": str(uuid.uuid4()),
        "created_utc": created.isoformat(),
        "expires_utc": (created + timedelta(minutes=expires_minutes)).isoformat(),
        "action": "load_exact_family_and_place_level_based_instances_save_as",
        "status": "review_required",
        "execution_allowed": False,
        "provider_egress_performed": False,
        "document_mutation_performed": False,
        "source": {
            "model_relative_path": str(source_path.relative_to(root)).replace(os.sep, "/"),
            "model_sha256": EXPECTED_SOURCE_SHA256,
            "document_title": "School-Architectural-electrical-working",
            "protected_source_must_remain_unchanged": True,
        },
        "output": {
            "model_relative_path": str(output_path.relative_to(root)).replace(os.sep, "/"),
            "overwrite_allowed": False,
            "backup_required": True,
            "reopen_verification_required": True,
        },
        "family": {
            "relative_path": str(exact_family_path.relative_to(root)).replace(os.sep, "/"),
            "sha256": EXPECTED_FAMILY_SHA256,
            "family_name": "63_UN_HighBay_Coreline Highbay BY120P G5 PSU 105-150_Signify",
            "type_name": "BY120P G5 LED105S_840 WB PSU",
            "category": "Lighting Fixtures",
            "placement_type": "OneLevelBased",
            "host_requirement": "level",
            "voltage_v": 230,
            "active_power_w": 67,
            "apparent_load_va": None,
            "power_factor": None,
        },
        "photometry": {
            "relative_path": str(exact_ies_path.relative_to(root)).replace(os.sep, "/"),
            "sha256": EXPECTED_IES_SHA256,
            "lumens": 10_501.2,
            "input_w": 67,
        },
        "room": {
            "element_id": 221494,
            "name": "GYM 3500",
            "level_id": 30,
            "level_name": "Level 1",
            "room_top_z_mm": room["bbox_max"]["z_mm"],
            "mounting_z_mm": MOUNTING_Z_MM,
            "clearance_below_room_top_mm": round(float(room["bbox_max"]["z_mm"]) - MOUNTING_Z_MM, 3),
            "mounting_height_status": "PROVISIONAL_ENGINEERING_ASSUMPTION_REQUIRES_PREVIEW_APPROVAL",
        },
        "batch": {
            "batch_number": 1,
            "batch_count": 2,
            "placement_count": len(placements),
            "selection_method": "checkerboard cells where grid_column + grid_row is even",
            "placements": placements,
            "remaining_point_ids_for_batch_2": [point["point_id"] for point in batch_two],
            "batch_2_source_sha256": None,
            "batch_2_status": "DEFERRED_UNTIL_BATCH_1_REOPENED_OUTPUT_IS_VERIFIED",
        },
        "engineering_scope": {
            "full_layout_quantity": 45,
            "target_lux": 300,
            "lumen_method_result_lux": 306.7,
            "full_active_load_w": 3015,
            "claim_of_code_compliance": False,
            "open_checks": [
                "Licensed Egyptian illuminance clause",
                "IES point-by-point calculation at 9.5 m mounting height",
                "Sports glare and impact review",
                "Power factor, VA, current, circuit and protection",
            ],
        },
        "preview_sha256": "",
    }
    artifact["preview_sha256"] = _artifact_hash(artifact)
    return artifact


def write_preview(path: Path, artifact: dict[str, Any]) -> None:
    if path.exists() or path.is_symlink():
        raise ValueError("Preview output already exists; overwrite is not allowed")
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(f".{path.name}.{uuid.uuid4().hex}.tmp")
    temporary.write_text(json.dumps(artifact, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    os.replace(temporary, path)


def load_preview(
    path: Path,
    *,
    workspace_root: Path,
    now: datetime | None = None,
) -> dict[str, Any]:
    """Validate a stored preview and every bound file before any future execution."""
    root = workspace_root.expanduser().resolve(strict=True)
    candidate = _resolve_workspace_file(root, path, "Execution preview")
    artifact = json.loads(candidate.read_text(encoding="utf-8-sig"))
    if not isinstance(artifact, dict) or artifact.get("schema_version") != 1:
        raise ValueError("Execution preview schema is invalid")
    if artifact.get("preview_sha256") != _artifact_hash(artifact):
        raise ValueError("Execution preview SHA-256 is invalid")
    if (
        artifact.get("action") != "load_exact_family_and_place_level_based_instances_save_as"
        or artifact.get("status") != "review_required"
        or artifact.get("execution_allowed") is not False
        or artifact.get("document_mutation_performed") is not False
        or artifact.get("provider_egress_performed") is not False
    ):
        raise ValueError("Execution preview safety state is invalid")
    expires = datetime.fromisoformat(str(artifact.get("expires_utc", "")))
    current = now or datetime.now(UTC)
    if expires.tzinfo is None or current >= expires:
        raise ValueError("Execution preview expired")

    source = root / artifact["source"]["model_relative_path"]
    family = root / artifact["family"]["relative_path"]
    ies = root / artifact["photometry"]["relative_path"]
    output = root / artifact["output"]["model_relative_path"]
    if _sha256(_resolve_workspace_file(root, source, "Source model")) != EXPECTED_SOURCE_SHA256:
        raise ValueError("Execution preview source model changed")
    if _sha256(_resolve_workspace_file(root, family, "L4 family")) != EXPECTED_FAMILY_SHA256:
        raise ValueError("Execution preview L4 family changed")
    if _sha256(_resolve_workspace_file(root, ies, "L4 IES")) != EXPECTED_IES_SHA256:
        raise ValueError("Execution preview L4 IES changed")
    if output.exists() or output.is_symlink() or output.suffix.lower() != ".rvt" or not output.resolve(strict=False).is_relative_to(root):
        raise ValueError("Execution preview output is unsafe or already exists")

    batch = artifact.get("batch", {})
    placements = batch.get("placements")
    remaining = batch.get("remaining_point_ids_for_batch_2")
    if (
        batch.get("batch_number") != 1
        or batch.get("batch_count") != 2
        or not isinstance(placements, list)
        or len(placements) != 23
        or not isinstance(remaining, list)
        or len(remaining) != 22
        or batch.get("batch_2_source_sha256") is not None
    ):
        raise ValueError("Execution preview batch contract is invalid")
    point_ids: set[str] = set()
    for index, placement in enumerate(placements, start=1):
        if (
            placement.get("index") != index
            or placement.get("z_mm") != MOUNTING_Z_MM
            or placement.get("rotation_degrees") != 0.0
            or not isinstance(placement.get("point_id"), str)
            or placement["point_id"] in point_ids
        ):
            raise ValueError("Execution preview placement contract is invalid")
        point_ids.add(placement["point_id"])
    if point_ids.intersection(remaining) or len(point_ids.union(remaining)) != 45:
        raise ValueError("Execution preview point partition is invalid")
    return artifact


def write_plot(path: Path, artifact: dict[str, Any], layout: dict[str, Any]) -> None:
    """Render a review image; this does not alter the model or preview fingerprint."""
    from PIL import Image, ImageDraw, ImageFont

    room = layout["room"]
    all_points = layout["placement"]["points"]
    batch_one_ids = {point["point_id"] for point in artifact["batch"]["placements"]}
    minimum = room["bbox_min"]
    maximum = room["bbox_max"]
    width = maximum["x_mm"] - minimum["x_mm"]
    depth = maximum["y_mm"] - minimum["y_mm"]
    canvas_width, canvas_height = 1600, 1120
    left, top, right, bottom = 150, 155, 1450, 930
    image = Image.new("RGB", (canvas_width, canvas_height), "white")
    draw = ImageDraw.Draw(image)
    try:
        title_font = ImageFont.truetype("C:/Windows/Fonts/arialbd.ttf", 36)
        body_font = ImageFont.truetype("C:/Windows/Fonts/arial.ttf", 22)
        small_font = ImageFont.truetype("C:/Windows/Fonts/arial.ttf", 15)
    except OSError:
        title_font = body_font = small_font = ImageFont.load_default()

    def map_point(x_mm: float, y_mm: float) -> tuple[float, float]:
        x = left + (x_mm - minimum["x_mm"]) / width * (right - left)
        y = bottom - (y_mm - minimum["y_mm"]) / depth * (bottom - top)
        return x, y

    draw.text((canvas_width / 2, 55), "GYM 3500 - L4 high-bay placement preview (9 x 5)", font=title_font, fill="#17365D", anchor="mm")
    draw.rectangle((left, top, right, bottom), outline="#17365D", width=5)
    for column in range(1, 9):
        x = left + (right - left) * column / 9
        draw.line((x, top, x, bottom), fill="#D9E2F3", width=1)
    for row in range(1, 5):
        y = top + (bottom - top) * row / 5
        draw.line((left, y, right, y), fill="#D9E2F3", width=1)
    for point in all_points:
        batch_one = point["point_id"] in batch_one_ids
        x, y = map_point(float(point["x_mm"]), float(point["y_mm"]))
        color = "#4472C4" if batch_one else "#70AD47"
        if batch_one:
            draw.ellipse((x - 16, y - 16, x + 16, y + 16), fill=color, outline="white", width=2)
        else:
            draw.rectangle((x - 15, y - 15, x + 15, y + 15), fill=color, outline="white", width=2)
        draw.text((x, y - 27), point["point_id"].split("-")[-1], font=small_font, fill="#303030", anchor="ms")

    draw.ellipse((190, 982, 220, 1012), fill="#4472C4")
    draw.text((235, 997), "Batch 1: 23 points", font=body_font, fill="#303030", anchor="lm")
    draw.rectangle((610, 982, 640, 1012), fill="#70AD47")
    draw.text((655, 997), "Batch 2: 22 points after batch 1 verification", font=body_font, fill="#303030", anchor="lm")
    draw.text((canvas_width / 2, 1065), "Mounting Z = 9,500 mm (provisional). No Revit write authorized.", font=body_font, fill="#9C5700", anchor="mm")
    path.parent.mkdir(parents=True, exist_ok=True)
    image.save(path, format="PNG", optimize=True)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--workspace-root", required=True, type=Path)
    parser.add_argument("--layout-preview", required=True, type=Path)
    parser.add_argument("--source-model", required=True, type=Path)
    parser.add_argument("--family", required=True, type=Path)
    parser.add_argument("--ies", required=True, type=Path)
    parser.add_argument("--output-model", required=True, type=Path)
    parser.add_argument("--preview-output", required=True, type=Path)
    parser.add_argument("--plot-output", type=Path)
    args = parser.parse_args()
    artifact = build_preview(
        workspace_root=args.workspace_root,
        layout_preview_path=args.layout_preview,
        source_model_path=args.source_model,
        family_path=args.family,
        ies_path=args.ies,
        output_model_path=args.output_model,
    )
    write_preview(args.preview_output, artifact)
    if args.plot_output:
        layout = json.loads(args.layout_preview.read_text(encoding="utf-8-sig"))
        write_plot(args.plot_output, artifact, layout)
    print(json.dumps({
        "preview": str(args.preview_output.resolve()),
        "preview_sha256": artifact["preview_sha256"],
        "batch_1_placements": artifact["batch"]["placement_count"],
        "batch_2_deferred": len(artifact["batch"]["remaining_point_ids_for_batch_2"]),
        "mounting_z_mm": artifact["room"]["mounting_z_mm"],
    }))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
