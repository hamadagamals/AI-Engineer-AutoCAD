"""Build and validate the non-executable school L4 gym batch-2 preview."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import uuid
from datetime import UTC, datetime, timedelta
from pathlib import Path
from typing import Any


EXPECTED_SOURCE_SHA256 = "941551BE095D7CC743924ACDFAE335E8CB549D648DB1C9ABDECADF669BE41A1A"
EXPECTED_FAMILY_SHA256 = "404CCB847E33841D36200D31080A2BB661B1AA941AEFBF170C372055172A139F"
EXPECTED_IES_SHA256 = "648B25C3299A591FEDF87FF72D67DE3FF93D81416467CA6811AEAFEADE6258A8"
EXPECTED_BATCH1_PREVIEW_SHA256 = "5E78A601B372501A07612B4B120B9E73878F7FEAD4718C4CC5F39F40EB6F273C"
EXPECTED_HANDOFF_SHA256 = "012317B492EBDD3132F52335209ECC933F58D442BF4512F323DCD15F0ED98129"
EXPECTED_VERIFICATION_SHA256 = "C5C3AC0A4999AFBCBAEE32624BD25D9FB042D477E8C6AB7189F5B67C1964CD3B"
MOUNTING_Z_MM = 9500.0


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


def _file(root: Path, path: Path, label: str) -> Path:
    resolved = path.expanduser().resolve(strict=True)
    if not resolved.is_file() or resolved.is_symlink() or not resolved.is_relative_to(root):
        raise ValueError(f"{label} must be a regular file inside the workspace")
    return resolved


def build_preview(
    *,
    workspace_root: Path,
    layout_preview_path: Path,
    batch1_source_model_path: Path,
    batch1_handoff_path: Path,
    batch1_verification_path: Path,
    family_path: Path,
    ies_path: Path,
    output_model_path: Path,
    expires_minutes: int = 120,
) -> dict[str, Any]:
    """Build the exact remaining 22-point preview without opening Revit."""
    root = workspace_root.expanduser().resolve(strict=True)
    layout_path = _file(root, layout_preview_path, "Layout preview")
    source = _file(root, batch1_source_model_path, "Batch-1 source model")
    handoff_path = _file(root, batch1_handoff_path, "Batch-1 handoff")
    verification_path = _file(root, batch1_verification_path, "Batch-1 verification")
    family = _file(root, family_path, "L4 family")
    ies = _file(root, ies_path, "L4 IES")
    output = output_model_path.expanduser().resolve(strict=False)
    if output.suffix.lower() != ".rvt" or not output.is_relative_to(root) or output.exists() or output.is_symlink():
        raise ValueError("Batch-2 output must be a new RVT path inside the workspace")
    if not 1 <= expires_minutes <= 120:
        raise ValueError("Preview expiry must be within 1..120 minutes")
    expected_hashes = (
        (source, EXPECTED_SOURCE_SHA256, "Batch-1 source model"),
        (handoff_path, EXPECTED_HANDOFF_SHA256, "Batch-1 handoff"),
        (verification_path, EXPECTED_VERIFICATION_SHA256, "Batch-1 verification"),
        (family, EXPECTED_FAMILY_SHA256, "L4 family"),
        (ies, EXPECTED_IES_SHA256, "L4 IES"),
    )
    for path, expected, label in expected_hashes:
        if _sha256(path) != expected:
            raise ValueError(f"{label} SHA-256 changed")

    handoff = json.loads(handoff_path.read_text(encoding="utf-8-sig"))
    verification = json.loads(verification_path.read_text(encoding="utf-8-sig"))
    existing_ids = handoff.get("created_element_ids")
    batch1_points = handoff.get("point_ids")
    connectors = verification.get("connector_counts")
    if (
        handoff.get("preview_sha256") != EXPECTED_BATCH1_PREVIEW_SHA256
        or handoff.get("output_sha256") != EXPECTED_SOURCE_SHA256
        or handoff.get("created_family_instances") != 23
        or not isinstance(existing_ids, list)
        or len(existing_ids) != 23
        or len(set(existing_ids)) != 23
        or not isinstance(batch1_points, list)
        or len(batch1_points) != 23
        or verification.get("handoff_request_id") != handoff.get("request_id")
        or verification.get("output_sha256") != EXPECTED_SOURCE_SHA256
        or verification.get("verified_element_ids") != existing_ids
        or verification.get("verified_family_instances") != 23
        or verification.get("placement_matches_preview") is not True
        or not isinstance(connectors, list)
        or connectors != [1] * 23
    ):
        raise ValueError("Batch-1 accepted evidence is inconsistent")

    layout = json.loads(layout_path.read_text(encoding="utf-8-sig"))
    room = layout.get("room", {})
    all_points = layout.get("placement", {}).get("points")
    if (
        layout.get("execution_authorized") is not False
        or room.get("element_id") != 221494
        or room.get("level_id") != 30
        or room.get("name") != "GYM 3500"
        or not isinstance(all_points, list)
        or len(all_points) != 45
    ):
        raise ValueError("L4 layout preview identity is invalid")
    expected_batch1 = [point for point in all_points if (int(point["grid_column"]) + int(point["grid_row"])) % 2 == 0]
    batch2 = [point for point in all_points if (int(point["grid_column"]) + int(point["grid_row"])) % 2 == 1]
    if [point["point_id"] for point in expected_batch1] != batch1_points or len(batch2) != 22:
        raise ValueError("Batch-1 evidence and layout partition do not match")
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
        for index, point in enumerate(batch2, start=1)
    ]
    created = datetime.now(UTC)
    artifact: dict[str, Any] = {
        "schema_version": 1,
        "preview_id": str(uuid.uuid4()),
        "created_utc": created.isoformat(),
        "expires_utc": (created + timedelta(minutes=expires_minutes)).isoformat(),
        "action": "place_remaining_exact_loaded_family_instances_save_as",
        "status": "review_required",
        "execution_allowed": False,
        "provider_egress_performed": False,
        "document_mutation_performed": False,
        "source": {
            "model_relative_path": str(source.relative_to(root)).replace(os.sep, "/"),
            "model_sha256": EXPECTED_SOURCE_SHA256,
            "document_title": source.stem,
            "protected_source_must_remain_unchanged": True,
        },
        "output": {
            "model_relative_path": str(output.relative_to(root)).replace(os.sep, "/"),
            "overwrite_allowed": False,
            "backup_required": True,
            "reopen_verification_required": True,
        },
        "predecessor": {
            "preview_sha256": EXPECTED_BATCH1_PREVIEW_SHA256,
            "handoff_relative_path": str(handoff_path.relative_to(root)).replace(os.sep, "/"),
            "handoff_sha256": EXPECTED_HANDOFF_SHA256,
            "verification_relative_path": str(verification_path.relative_to(root)).replace(os.sep, "/"),
            "verification_sha256": EXPECTED_VERIFICATION_SHA256,
            "existing_instance_count": 23,
            "existing_element_ids": existing_ids,
            "existing_point_ids": batch1_points,
        },
        "family": {
            "relative_path": str(family.relative_to(root)).replace(os.sep, "/"),
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
            "relative_path": str(ies.relative_to(root)).replace(os.sep, "/"),
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
            "mounting_height_status": "ENGINEER_APPROVED_FOR_L4_GYM_BATCHES",
        },
        "batch": {
            "batch_number": 2,
            "batch_count": 2,
            "placement_count": 22,
            "selection_method": "complementary checkerboard cells where grid_column + grid_row is odd",
            "placements": placements,
            "resulting_total_instance_count": 45,
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


def load_preview(path: Path, *, workspace_root: Path, now: datetime | None = None) -> dict[str, Any]:
    root = workspace_root.expanduser().resolve(strict=True)
    candidate = _file(root, path, "Batch-2 preview")
    value = json.loads(candidate.read_text(encoding="utf-8-sig"))
    if not isinstance(value, dict) or value.get("schema_version") != 1 or value.get("preview_sha256") != _artifact_hash(value):
        raise ValueError("Batch-2 preview identity or SHA-256 is invalid")
    expires = datetime.fromisoformat(str(value.get("expires_utc", "")))
    if expires.tzinfo is None or (now or datetime.now(UTC)) >= expires:
        raise ValueError("Batch-2 preview expired")
    if (
        value.get("action") != "place_remaining_exact_loaded_family_instances_save_as"
        or value.get("status") != "review_required"
        or value.get("execution_allowed") is not False
        or value.get("provider_egress_performed") is not False
        or value.get("document_mutation_performed") is not False
    ):
        raise ValueError("Batch-2 preview safety state is invalid")
    bound = (
        (value["source"]["model_relative_path"], EXPECTED_SOURCE_SHA256, "Batch-1 source model"),
        (value["predecessor"]["handoff_relative_path"], EXPECTED_HANDOFF_SHA256, "Batch-1 handoff"),
        (value["predecessor"]["verification_relative_path"], EXPECTED_VERIFICATION_SHA256, "Batch-1 verification"),
        (value["family"]["relative_path"], EXPECTED_FAMILY_SHA256, "L4 family"),
        (value["photometry"]["relative_path"], EXPECTED_IES_SHA256, "L4 IES"),
    )
    for relative, expected, label in bound:
        if _sha256(_file(root, root / relative, label)) != expected:
            raise ValueError(f"{label} SHA-256 changed")
    output = root / value["output"]["model_relative_path"]
    placements = value.get("batch", {}).get("placements")
    if output.exists() or output.is_symlink() or not isinstance(placements, list) or len(placements) != 22:
        raise ValueError("Batch-2 output or placement contract is invalid")
    if any(point.get("index") != index or point.get("z_mm") != MOUNTING_Z_MM for index, point in enumerate(placements, 1)):
        raise ValueError("Batch-2 point contract is invalid")
    return value


def write_plot(path: Path, artifact: dict[str, Any], layout: dict[str, Any]) -> None:
    from PIL import Image, ImageDraw, ImageFont

    room = layout["room"]
    points = layout["placement"]["points"]
    batch2_ids = {point["point_id"] for point in artifact["batch"]["placements"]}
    minimum, maximum = room["bbox_min"], room["bbox_max"]
    canvas = Image.new("RGB", (1600, 1120), "white")
    draw = ImageDraw.Draw(canvas)
    try:
        title = ImageFont.truetype("C:/Windows/Fonts/arialbd.ttf", 36)
        body = ImageFont.truetype("C:/Windows/Fonts/arial.ttf", 22)
        small = ImageFont.truetype("C:/Windows/Fonts/arial.ttf", 15)
    except OSError:
        title = body = small = ImageFont.load_default()
    left, top, right, bottom = 150, 155, 1450, 930
    width = maximum["x_mm"] - minimum["x_mm"]
    depth = maximum["y_mm"] - minimum["y_mm"]
    def mapped(point: dict[str, Any]) -> tuple[float, float]:
        return (
            left + (point["x_mm"] - minimum["x_mm"]) / width * (right - left),
            bottom - (point["y_mm"] - minimum["y_mm"]) / depth * (bottom - top),
        )
    draw.text((800, 55), "GYM 3500 - L4 batch 2 review (22 remaining points)", font=title, fill="#17365D", anchor="mm")
    draw.rectangle((left, top, right, bottom), outline="#17365D", width=5)
    for point in points:
        x, y = mapped(point)
        remaining = point["point_id"] in batch2_ids
        color = "#70AD47" if remaining else "#A6A6A6"
        if remaining:
            draw.rectangle((x - 16, y - 16, x + 16, y + 16), fill=color, outline="white", width=2)
        else:
            draw.ellipse((x - 12, y - 12, x + 12, y + 12), fill=color, outline="white", width=2)
        draw.text((x, y - 27), point["point_id"].split("-")[-1], font=small, fill="#303030", anchor="ms")
    draw.ellipse((190, 982, 214, 1006), fill="#A6A6A6")
    draw.text((230, 994), "Verified batch 1: 23 existing", font=body, fill="#303030", anchor="lm")
    draw.rectangle((650, 979, 678, 1007), fill="#70AD47")
    draw.text((695, 994), "Proposed batch 2: 22 remaining", font=body, fill="#303030", anchor="lm")
    draw.text((800, 1060), "Mounting Z = 9,500 mm. Review only; no Revit write authorized.", font=body, fill="#9C5700", anchor="mm")
    path.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(path, format="PNG", optimize=True)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--workspace-root", required=True, type=Path)
    parser.add_argument("--layout-preview", required=True, type=Path)
    parser.add_argument("--source-model", required=True, type=Path)
    parser.add_argument("--handoff", required=True, type=Path)
    parser.add_argument("--verification", required=True, type=Path)
    parser.add_argument("--family", required=True, type=Path)
    parser.add_argument("--ies", required=True, type=Path)
    parser.add_argument("--output-model", required=True, type=Path)
    parser.add_argument("--preview-output", required=True, type=Path)
    parser.add_argument("--plot-output", type=Path)
    args = parser.parse_args()
    artifact = build_preview(
        workspace_root=args.workspace_root, layout_preview_path=args.layout_preview,
        batch1_source_model_path=args.source_model, batch1_handoff_path=args.handoff,
        batch1_verification_path=args.verification, family_path=args.family, ies_path=args.ies,
        output_model_path=args.output_model,
    )
    write_preview(args.preview_output, artifact)
    if args.plot_output:
        write_plot(args.plot_output, artifact, json.loads(args.layout_preview.read_text(encoding="utf-8-sig")))
    print(json.dumps({
        "preview": str(args.preview_output.resolve()), "preview_sha256": artifact["preview_sha256"],
        "batch_2_placements": 22, "resulting_total": 45, "mounting_z_mm": MOUNTING_Z_MM,
    }))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
