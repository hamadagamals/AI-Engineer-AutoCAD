"""Create a non-executable L1 lighting sample preview for CLASSROOM 1500."""

from __future__ import annotations

import hashlib
import json
import os
import uuid
from datetime import UTC, datetime, timedelta
from pathlib import Path
from typing import Any


SOURCE_SHA256 = "FB72029C02CF900C70487BFD4636AF1C34806E31F62946324376BE38296C52D1"
INVENTORY_SHA256 = "1D16A2508FB47023C50646F6F8CD304CEA845EB176CBCE7E7643ABC6A53E87DF"
FAMILY_SHA256 = "7AEC687A27A089F4F607B78BEF730B9D1B58C65CE0529E7730EC2699E826DAC2"
IES_SHA256 = "7970BE1E07E695A158B20ACB80878EF70A4B9D103462FFC705F7AAB58F83D18D"
ROOM_ID = 221093
CEILING_ID = 278813
MOUNTING_Z_MM = 2743.2


def _hash(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def _fingerprint(value: dict[str, Any]) -> str:
    payload = {key: item for key, item in value.items() if key != "preview_sha256"}
    return hashlib.sha256(json.dumps(payload, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")).hexdigest().upper()


def _inside(root: Path, path: Path, label: str) -> Path:
    resolved = path.expanduser().resolve(strict=True)
    if not resolved.is_file() or resolved.is_symlink() or not resolved.is_relative_to(root):
        raise ValueError(f"{label} must be a regular workspace file")
    return resolved


def build_preview(
    *, workspace_root: Path, inventory_path: Path, source_model_path: Path,
    family_path: Path, ies_path: Path, output_model_path: Path, expires_minutes: int = 120,
) -> dict[str, Any]:
    root = workspace_root.resolve(strict=True)
    inventory_file = _inside(root, inventory_path, "Inventory")
    source = _inside(root, source_model_path, "Complete L4 source")
    family = _inside(root, family_path, "L1 family")
    ies = _inside(root, ies_path, "L1 IES")
    output = output_model_path.resolve(strict=False)
    expected = ((inventory_file, INVENTORY_SHA256, "Inventory"), (source, SOURCE_SHA256, "Source"),
                (family, FAMILY_SHA256, "L1 family"), (ies, IES_SHA256, "L1 IES"))
    for path, sha, label in expected:
        if _hash(path) != sha:
            raise ValueError(f"{label} SHA-256 changed")
    if output.suffix.lower() != ".rvt" or not output.is_relative_to(root) or output.exists() or output.is_symlink():
        raise ValueError("Classroom sample output must be a new workspace RVT")
    if not 1 <= expires_minutes <= 120:
        raise ValueError("Preview expiry must be within 1..120 minutes")

    inventory = json.loads(inventory_file.read_text(encoding="utf-8-sig"))
    rooms = inventory["categories"]["rooms"]["elements"]
    ceilings = inventory["categories"]["ceilings"]["elements"]
    room = next((item for item in rooms if item.get("element_id") == ROOM_ID), None)
    ceiling = next((item for item in ceilings if item.get("element_id") == CEILING_ID), None)
    if room is None or ceiling is None or room.get("name") != "CLASSROOM 1500" or room.get("level_id") != 30:
        raise ValueError("Classroom sample room/level identity changed")
    if ceiling.get("level_id") != 30 or ceiling.get("name") != "2' x 2' ACT System":
        raise ValueError("Classroom sample ceiling identity changed")
    if abs(float(ceiling["bbox_min"]["z_mm"]) - MOUNTING_Z_MM) > 0.01:
        raise ValueError("Classroom sample ceiling underside changed")
    minimum, maximum = room["bbox_min"], room["bbox_max"]
    width = float(maximum["x_mm"]) - float(minimum["x_mm"])
    depth = float(maximum["y_mm"]) - float(minimum["y_mm"])
    area = width * depth / 1_000_000
    if abs(area - 53.25602252) > 0.01:
        raise ValueError("Classroom sample area changed")
    points: list[dict[str, Any]] = []
    for row in range(3):
        for column in range(3):
            if row == 1 and column == 1:
                continue
            points.append({
                "index": len(points) + 1,
                "point_id": f"L1-CLASSROOM-1500-{len(points) + 1:02d}",
                "grid_column": column + 1, "grid_row": row + 1,
                "x_mm": round(float(minimum["x_mm"]) + (column + 0.5) * width / 3, 3),
                "y_mm": round(float(minimum["y_mm"]) + (row + 0.5) * depth / 3, 3),
                "z_mm": MOUNTING_Z_MM, "rotation_degrees": 0.0,
            })
    achieved = 8 * 4310.8 * 0.60 * 0.80 / area
    created = datetime.now(UTC)
    preview: dict[str, Any] = {
        "schema_version": 1, "preview_id": str(uuid.uuid4()),
        "created_utc": created.isoformat(), "expires_utc": (created + timedelta(minutes=expires_minutes)).isoformat(),
        "action": "load_l1_family_place_classroom1500_sample_save_as", "status": "review_required",
        "execution_allowed": False, "provider_egress_performed": False, "document_mutation_performed": False,
        "source": {"model_relative_path": str(source.relative_to(root)).replace(os.sep, "/"),
                   "model_sha256": SOURCE_SHA256, "document_title": source.stem,
                   "protected_source_must_remain_unchanged": True},
        "output": {"model_relative_path": str(output.relative_to(root)).replace(os.sep, "/"),
                   "overwrite_allowed": False, "backup_required": True, "reopen_verification_required": True},
        "room": {"element_id": ROOM_ID, "name": "CLASSROOM 1500", "level_id": 30, "level_name": "Level 1",
                 "bbox_min": minimum, "bbox_max": maximum, "bbox_area_m2": round(area, 6)},
        "ceiling": {"element_id": CEILING_ID, "name": ceiling["name"], "type_id": ceiling["type_id"],
                    "overlap_ratio": 1.0, "underside_z_mm": MOUNTING_Z_MM,
                    "host_note": "L1 is OneLevelBased; ceiling is geometry/elevation evidence, not an API host."},
        "family": {"relative_path": str(family.relative_to(root)).replace(os.sep, "/"), "sha256": FAMILY_SHA256,
                   "family_name": "RC132V G6 29_36_43S_830_40 PSD W60L60 OC",
                   "type_name": "RC132V G6 29_36_43S_830_40 PSD W60L60 OC", "placement_type": "OneLevelBased",
                   "voltage_v": 230, "apparent_load_va": 31, "active_power_w": 31},
        "photometry": {"relative_path": str(ies.relative_to(root)).replace(os.sep, "/"), "sha256": IES_SHA256,
                       "lumens": 4310.8, "input_w": 31, "cct_k": 4000},
        "calculation": {"target_lux": 300, "utilization_factor": 0.60, "maintenance_factor": 0.80,
                        "quantity": 8, "achieved_lux_lumen_method": round(achieved, 1),
                        "active_connected_load_w": 248},
        "placement": {"pattern": "3x3 perimeter with center omitted", "placement_count": 8,
                      "mounting_z_mm": MOUNTING_Z_MM, "points": points},
        "review_scope": {"sample_only": True, "bulk_distribution_authorized": False,
                         "claim_of_code_compliance": False,
                         "open_checks": ["Visual classroom review", "Exact ceiling-grid coordination",
                                         "Teacher board/projector/furniture coordination",
                                         "Licensed Egyptian illuminance clause", "IES point calculation and glare"]},
        "preview_sha256": "",
    }
    preview["preview_sha256"] = _fingerprint(preview)
    return preview


def write_preview(path: Path, value: dict[str, Any]) -> None:
    if path.exists() or path.is_symlink():
        raise ValueError("Classroom sample preview refuses overwrite")
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(f".{path.name}.{uuid.uuid4().hex}.tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    os.replace(temporary, path)


def load_preview(path: Path, *, workspace_root: Path, now: datetime | None = None) -> dict[str, Any]:
    root = workspace_root.resolve(strict=True)
    candidate = _inside(root, path, "Classroom sample preview")
    value = json.loads(candidate.read_text(encoding="utf-8-sig"))
    if value.get("preview_sha256") != _fingerprint(value) or value.get("action") != "load_l1_family_place_classroom1500_sample_save_as":
        raise ValueError("Classroom sample preview identity or SHA-256 is invalid")
    expires = datetime.fromisoformat(value["expires_utc"])
    if (now or datetime.now(UTC)) >= expires:
        raise ValueError("Classroom sample preview expired")
    if value.get("execution_allowed") is not False or value.get("review_scope", {}).get("bulk_distribution_authorized") is not False:
        raise ValueError("Classroom sample preview safety state is invalid")
    for section, key, expected, label in (("source", "model_relative_path", SOURCE_SHA256, "Source"),
                                           ("family", "relative_path", FAMILY_SHA256, "L1 family"),
                                           ("photometry", "relative_path", IES_SHA256, "L1 IES")):
        if _hash(_inside(root, root / value[section][key], label)) != expected:
            raise ValueError(f"{label} SHA-256 changed")
    output = root / value["output"]["model_relative_path"]
    if output.exists() or output.is_symlink() or len(value["placement"]["points"]) != 8:
        raise ValueError("Classroom sample output or points are invalid")
    return value


def write_plot(path: Path, preview: dict[str, Any]) -> None:
    from PIL import Image, ImageDraw, ImageFont
    room, points = preview["room"], preview["placement"]["points"]
    minimum, maximum = room["bbox_min"], room["bbox_max"]
    image = Image.new("RGB", (1400, 1050), "white")
    draw = ImageDraw.Draw(image)
    try:
        title = ImageFont.truetype("C:/Windows/Fonts/arialbd.ttf", 34)
        body = ImageFont.truetype("C:/Windows/Fonts/arial.ttf", 22)
        small = ImageFont.truetype("C:/Windows/Fonts/arial.ttf", 17)
    except OSError:
        title = body = small = ImageFont.load_default()
    left, top, right, bottom = 180, 170, 1220, 820
    width = maximum["x_mm"] - minimum["x_mm"]
    depth = maximum["y_mm"] - minimum["y_mm"]
    draw.text((700, 55), "CLASSROOM 1500 - L1 lighting sample", font=title, fill="#17365D", anchor="mm")
    draw.text((700, 105), "8 panels | 3 x 3 perimeter pattern | center omitted", font=body, fill="#404040", anchor="mm")
    draw.rectangle((left, top, right, bottom), outline="#17365D", width=5)
    for point in points:
        x = left + (point["x_mm"] - minimum["x_mm"]) / width * (right - left)
        y = bottom - (point["y_mm"] - minimum["y_mm"]) / depth * (bottom - top)
        draw.rectangle((x - 34, y - 34, x + 34, y + 34), fill="#4472C4", outline="white", width=3)
        draw.text((x, y), str(point["index"]), font=small, fill="white", anchor="mm")
    draw.text((700, 885), "Mounting Z 2,743.2 mm | Calculated average 310.8 lux | Connected load 248 W", font=body, fill="#303030", anchor="mm")
    draw.text((700, 940), "Sample review only. Ceiling grid, board, projector and furniture coordination remain open.", font=body, fill="#9C5700", anchor="mm")
    path.parent.mkdir(parents=True, exist_ok=True)
    image.save(path, "PNG", optimize=True)
