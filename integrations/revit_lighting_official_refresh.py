"""Validate exact official Signify IES archives and build a non-executable refresh record."""

from __future__ import annotations

import hashlib
import json
import os
import zipfile
from dataclasses import asdict, dataclass
from pathlib import Path, PurePosixPath
from typing import Any

from agent.exceptions import RevitIntegrationError
from integrations.revit_lighting_photometry_preview import parse_ies


@dataclass(frozen=True, slots=True)
class OfficialIesSelection:
    design_ids: tuple[str, ...]
    product_order_code: str
    product_lumens: float
    product_watts: float
    source_url: str
    archive_relative_path: str
    archive_sha256: str
    entry_name: str
    entry_sha256: str
    output_relative_path: str


def _hash(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def _bytes_hash(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest().upper()


def _inside(path: Path, root: Path) -> bool:
    return path == root or path.is_relative_to(root)


def _canonical_hash(value: dict[str, Any]) -> str:
    encoded = json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest().upper()


def extract_and_build_refresh(
    *,
    download_root: Path,
    extracted_root: Path,
    selections: list[OfficialIesSelection],
) -> dict[str, Any]:
    """Extract only exact reviewed IES entries and return a self-hashed review artifact."""
    downloads = download_root.resolve(strict=True)
    extracted = extracted_root.resolve(strict=True)
    if not selections or len({item.product_order_code for item in selections}) != len(selections):
        raise RevitIntegrationError("Official IES refresh requires unique product selections.")

    records: list[dict[str, Any]] = []
    for selection in selections:
        archive = (downloads / selection.archive_relative_path).resolve(strict=True)
        output = (extracted / selection.output_relative_path).resolve(strict=False)
        if (
            archive.is_symlink()
            or not _inside(archive, downloads)
            or archive.suffix.lower() != ".zip"
            or archive.stat().st_size > 10_000_000
            or _hash(archive) != selection.archive_sha256.upper()
            or not _inside(output, extracted)
            or output.suffix.lower() != ".ies"
            or output.name != PurePosixPath(selection.entry_name).name
        ):
            raise RevitIntegrationError("Official IES archive or output identity is invalid.")

        with zipfile.ZipFile(archive) as bundle:
            members = bundle.infolist()
            if not 1 <= len(members) <= 32:
                raise RevitIntegrationError("Official IES archive member count is outside bounds.")
            for member in members:
                member_path = PurePosixPath(member.filename.replace("\\", "/"))
                if (
                    member.is_dir()
                    or member.file_size > 2_000_000
                    or member_path.is_absolute()
                    or ".." in member_path.parts
                    or member_path.suffix.lower() != ".ies"
                    or ((member.external_attr >> 16) & 0o170000) == 0o120000
                ):
                    raise RevitIntegrationError("Official IES archive contains an unsafe member.")
            matches = [member for member in members if member.filename == selection.entry_name]
            if len(matches) != 1:
                raise RevitIntegrationError("Reviewed IES archive entry is missing or ambiguous.")
            data = bundle.read(matches[0])
        if _bytes_hash(data) != selection.entry_sha256.upper():
            raise RevitIntegrationError("Reviewed IES entry hash differs from the recorded value.")

        output.parent.mkdir(parents=True, exist_ok=True)
        if output.exists():
            if output.is_symlink() or _hash(output) != selection.entry_sha256.upper():
                raise RevitIntegrationError("Existing extracted IES differs from reviewed evidence.")
        else:
            temporary = output.with_name(f".{output.stem}-{os.getpid()}.ies")
            try:
                temporary.write_bytes(data)
                if _hash(temporary) != selection.entry_sha256.upper():
                    raise RevitIntegrationError("Extracted IES hash verification failed.")
                os.replace(temporary, output)
            finally:
                temporary.unlink(missing_ok=True)

        ies = parse_ies(output)
        lumen_delta = round(ies.lumens - selection.product_lumens, 3)
        watt_delta = round(ies.input_watts - selection.product_watts, 3)
        ready = (
            ies.catalog == selection.product_order_code
            and abs(lumen_delta) <= 1.0
            and abs(watt_delta) <= 0.01
        )
        records.append({
            **asdict(selection),
            "design_ids": list(selection.design_ids),
            "extracted_sha256": _hash(output),
            "ies": asdict(ies),
            "product_vs_ies_lumen_delta": lumen_delta,
            "product_vs_ies_watt_delta": watt_delta,
            "disposition": "READY_FOR_PARAMETER_PREVIEW" if ready else "HOLD_PRODUCT_IES_CONFLICT",
        })

    artifact: dict[str, Any] = {
        "schema_version": 1,
        "action": "review_official_lighting_photometry_refresh",
        "status": "review_required",
        "execution_allowed": False,
        "document_mutation_permitted": False,
        "provider_egress_performed": True,
        "selections": records,
        "unresolved_reviews": [
            "L4 remains held: official product data is 10500 lm/63 W while its current exact official IES is 10501.2 lm/67 W.",
            "Licensed Egyptian illuminance clauses and responsible-engineer acceptance remain required before school placement.",
        ],
    }
    artifact["preview_sha256"] = _canonical_hash(artifact)
    return artifact


def write_refresh(artifact: dict[str, Any], destination: Path) -> None:
    """Atomically write a new refresh artifact without overwriting prior evidence."""
    target = destination.resolve(strict=False)
    if target.suffix.lower() != ".json" or target.exists() or target.is_symlink():
        raise RevitIntegrationError("Official lighting refresh destination must be a new JSON file.")
    target.parent.resolve(strict=True)
    temporary = target.with_name(f".{target.name}.tmp")
    try:
        temporary.write_text(json.dumps(artifact, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        os.replace(temporary, target)
    finally:
        temporary.unlink(missing_ok=True)


def build_remaining_parameter_preview(
    *,
    refresh_path: Path,
    source_rvt: Path,
    l1_verification_report: Path,
    original_scratch_report: Path,
    output_relative_path: str,
) -> dict[str, Any]:
    """Build the approval artifact for the exact L2 and L5-L6 parameter corrections."""
    try:
        refresh = json.loads(refresh_path.resolve(strict=True).read_text(encoding="utf-8-sig"))
        l1_report = json.loads(l1_verification_report.resolve(strict=True).read_text(encoding="utf-8-sig"))
        scratch_report = json.loads(original_scratch_report.resolve(strict=True).read_text(encoding="utf-8-sig"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise RevitIntegrationError(f"Remaining lighting parameter evidence is invalid: {exc}") from exc

    embedded = refresh.get("preview_sha256")
    refresh_body = dict(refresh)
    refresh_body.pop("preview_sha256", None)
    selections = refresh.get("selections")
    source = source_rvt.resolve(strict=True)
    instances = scratch_report.get("instances")
    if (
        refresh.get("schema_version") != 1
        or refresh.get("execution_allowed") is not False
        or embedded != _canonical_hash(refresh_body)
        or not isinstance(selections, list)
        or len(selections) != 2
        or any(item.get("disposition") != "READY_FOR_PARAMETER_PREVIEW" for item in selections)
        or l1_report.get("reopened_verified") is not True
        or l1_report.get("source_document_mutated") is not False
        or _hash(source) != str(l1_report.get("output_sha256", "")).upper()
        or scratch_report.get("reopened_verified") is not True
        or scratch_report.get("source_document_mutated") is not False
        or l1_report.get("source_sha256_before") != scratch_report.get("output_sha256")
        or not isinstance(instances, list)
    ):
        raise RevitIntegrationError("Remaining lighting parameter evidence failed identity validation.")

    selection_by_code = {item.get("product_order_code"): item for item in selections}
    l2 = selection_by_code.get("911401816685")
    wet = selection_by_code.get("910505100047")
    if not isinstance(l2, dict) or l2.get("design_ids") != ["L2"]:
        raise RevitIntegrationError("Exact L2 official photometry selection is unavailable.")
    if not isinstance(wet, dict) or wet.get("design_ids") != ["L5", "L6"]:
        raise RevitIntegrationError("Exact L5-L6 official photometry selection is unavailable.")

    instance_by_id = {item.get("design_id"): item for item in instances if isinstance(item, dict)}
    expected = {
        "L2": (
            "BN126C LED25_41_64S_840 PSU L1200",
            "WT210C LED39S_840 PMMA PSU L1500.ies",
        ),
        "L5-L6": (
            "WT120C 40S_840 PSU L1200",
            "WT198C LED18S840 PSU PCO L600.ies",
        ),
    }
    loaded: dict[str, dict[str, str]] = {}
    for design_id, (type_name, old_ies) in expected.items():
        instance = instance_by_id.get(design_id)
        if not isinstance(instance, dict) or instance.get("type_name") != type_name:
            raise RevitIntegrationError(f"Verified scratch identity differs for {design_id}.")
        values = {
            item.get("name"): item.get("value")
            for item in instance.get("parameters", [])
            if isinstance(item, dict)
        }
        if (
            values.get("Apparent Load") != "19 VA"
            or values.get("Wattage Comments") != "19W"
            or values.get("Photometric Web File") != old_ies
        ):
            raise RevitIntegrationError(f"Verified starting parameters differ for {design_id}.")
        loaded[design_id] = values

    output = Path(output_relative_path)
    if output.is_absolute() or output.suffix.lower() != ".rvt" or ".." in output.parts:
        raise RevitIntegrationError("Remaining parameter output path must be a relative RVT path.")
    preview: dict[str, Any] = {
        "schema_version": 1,
        "action": "update_l2_l5_l6_parameters_in_new_scratch_rvt",
        "status": "approval_required",
        "execution_allowed": False,
        "document_mutation_permitted": False,
        "save_allowed": False,
        "source_scratch_rvt_sha256": _hash(source),
        "official_refresh_sha256": embedded,
        "targets": [
            {
                "design_id": "L2",
                "family_name": instance_by_id["L2"].get("family_name"),
                "type_name": expected["L2"][0],
                "changes": [{
                    "parameter": "Photometric Web File",
                    "old": expected["L2"][1],
                    "new": Path(l2["output_relative_path"]).name,
                }],
                "ies_relative_path": l2["output_relative_path"],
                "ies_sha256": l2["entry_sha256"],
                "ies_lumens": l2["ies"]["lumens"],
                "ies_input_watts": l2["ies"]["input_watts"],
            },
            {
                "design_id": "L5-L6",
                "family_name": instance_by_id["L5-L6"].get("family_name"),
                "type_name": expected["L5-L6"][0],
                "changes": [
                    {"parameter": "Apparent Load", "old": "19 VA", "new": "30 VA"},
                    {"parameter": "Wattage Comments", "old": "19W", "new": "30W"},
                    {
                        "parameter": "Photometric Web File",
                        "old": expected["L5-L6"][1],
                        "new": Path(wet["output_relative_path"]).name,
                    },
                ],
                "ies_relative_path": wet["output_relative_path"],
                "ies_sha256": wet["entry_sha256"],
                "ies_lumens": wet["ies"]["lumens"],
                "ies_input_watts": wet["ies"]["input_watts"],
            },
        ],
        "held_targets": [{
            "design_id": "L4",
            "reason": "Official product page is 10500 lm/63 W while exact official IES is 10501.2 lm/67 W.",
        }],
        "output_relative_path": output.as_posix(),
        "verification": [
            "save only to the exact new output path without overwrite",
            "reopen output and verify both family/type identities",
            "verify exact parameter values, IES filenames, and one connector per target",
            "verify L1 remains 31 VA/31W with its exact IES",
            "verify source scratch RVT hash remains unchanged",
        ],
    }
    preview["preview_sha256"] = _canonical_hash(preview)
    return preview
