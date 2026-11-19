"""Build a source-bound, non-executable Revit lighting photometry preview."""

from __future__ import annotations

import hashlib
import json
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any

from agent.exceptions import RevitIntegrationError


@dataclass(frozen=True, slots=True)
class IesData:
    standard: str
    luminaire: str
    catalog: str
    lumens: float
    input_watts: float


@dataclass(frozen=True, slots=True)
class BindingSpec:
    design_id: str
    type_name: str
    rfa_relative_path: str
    rfa_sha256: str
    product_order_code: str
    product_voltage: str
    product_lumens: float
    product_watts: float
    ies_relative_path: str
    ies_sha256: str
    disposition: str
    decision_note: str


def _hash(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def _inside(path: Path, root: Path) -> bool:
    return path == root or path.is_relative_to(root)


def parse_ies(path: Path) -> IesData:
    """Parse the bounded LM-63 identity and lamp/input data used by the preview."""
    source = path.resolve(strict=True)
    if source.is_symlink() or source.suffix.lower() != ".ies" or source.stat().st_size > 2_000_000:
        raise RevitIntegrationError("IES source is not a bounded regular .ies file.")
    try:
        lines = source.read_text(encoding="utf-8-sig").splitlines()
    except UnicodeError:
        lines = source.read_text(encoding="cp1252").splitlines()
    if not lines or not lines[0].strip().upper().startswith("IESNA:LM-63-"):
        raise RevitIntegrationError("IES source lacks a supported LM-63 header.")
    tilt_index = next((i for i, line in enumerate(lines) if line.strip().upper() == "TILT=NONE"), -1)
    if tilt_index < 0 or tilt_index + 2 >= len(lines):
        raise RevitIntegrationError("IES source must use the bounded TILT=NONE form.")
    try:
        lamp = [float(value) for value in lines[tilt_index + 1].split()]
        ballast = [float(value) for value in lines[tilt_index + 2].split()]
    except ValueError as exc:
        raise RevitIntegrationError("IES lamp or ballast data are invalid.") from exc
    if len(lamp) != 10 or len(ballast) != 3 or lamp[0] <= 0 or lamp[1] <= 0 or ballast[2] <= 0:
        raise RevitIntegrationError("IES lamp or input-watt data are outside the supported form.")

    def tag(name: str) -> str:
        prefix = f"[{name}]"
        return next((line[len(prefix):].strip() for line in lines if line.upper().startswith(prefix)), "")

    return IesData(
        standard=lines[0].strip(),
        luminaire=tag("LUMINAIRE"),
        catalog=tag("LUMCAT"),
        lumens=round(lamp[0] * lamp[1] * lamp[2], 3),
        input_watts=round(ballast[2], 3),
    )


def _canonical_hash(value: dict[str, Any]) -> str:
    encoded = json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest().upper()


def build_preview(
    *,
    library_root: Path,
    scratch_report: Path,
    scratch_rvt: Path,
    specs: list[BindingSpec],
) -> dict[str, Any]:
    """Validate exact evidence and build a self-hashed review preview with no write permission."""
    library = library_root.resolve(strict=True)
    report_path = scratch_report.resolve(strict=True)
    rvt_path = scratch_rvt.resolve(strict=True)
    try:
        report = json.loads(report_path.read_text(encoding="utf-8-sig"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise RevitIntegrationError(f"Scratch report is invalid: {exc}") from exc
    instances = report.get("instances")
    if (
        report.get("schema_version") != 1
        or report.get("reopened_verified") is not True
        or report.get("source_document_mutated") is not False
        or not isinstance(instances, list)
        or len(instances) != 4
        or _hash(rvt_path) != str(report.get("output_sha256", "")).upper()
    ):
        raise RevitIntegrationError("Scratch evidence failed identity or reopen validation.")
    if len(specs) != 4 or len({spec.design_id for spec in specs}) != 4:
        raise RevitIntegrationError("Photometry preview requires four unique design bindings.")
    instance_by_id = {item.get("design_id"): item for item in instances if isinstance(item, dict)}
    bindings: list[dict[str, Any]] = []
    for spec in specs:
        instance = instance_by_id.get(spec.design_id)
        if not isinstance(instance, dict) or instance.get("type_name") != spec.type_name:
            raise RevitIntegrationError(f"Scratch type identity differs for {spec.design_id}.")
        rfa = (library / spec.rfa_relative_path).resolve(strict=True)
        ies = (library / spec.ies_relative_path).resolve(strict=True)
        if not _inside(rfa, library) or not _inside(ies, library):
            raise RevitIntegrationError("Lighting evidence escaped the reviewed library root.")
        if _hash(rfa) != spec.rfa_sha256.upper() or _hash(ies) != spec.ies_sha256.upper():
            raise RevitIntegrationError(f"Lighting evidence hash differs for {spec.design_id}.")
        ies_data = parse_ies(ies)
        bindings.append({
            **asdict(spec),
            "ies": asdict(ies_data),
            "product_vs_ies_lumen_delta": round(ies_data.lumens - spec.product_lumens, 3),
            "product_vs_ies_watt_delta": round(ies_data.input_watts - spec.product_watts, 3),
            "scratch": {
                "family_name": instance.get("family_name"),
                "connector_count": instance.get("connector_count"),
                "category": instance.get("category"),
                "loaded_parameters": instance.get("parameters"),
            },
        })
    preview: dict[str, Any] = {
        "schema_version": 1,
        "action": "review_lighting_type_photometry_bindings",
        "status": "review_required",
        "execution_allowed": False,
        "document_mutation_permitted": False,
        "provider_egress_permitted": False,
        "scratch_report_sha256": _hash(report_path),
        "scratch_rvt_sha256": _hash(rvt_path),
        "bindings": bindings,
        "unresolved_reviews": [
            "L2 product page 2500 lm/19 W differs from IES 2883.8 lm/19.257 W.",
            "L4 product page 10500 lm/63 W differs from exact wide-beam IES 10501.2 lm/67 W.",
            "L5-L6 selected product is 4000 lm/30 W, while the closest reviewed IES is 4500 lm/30 W.",
            "Licensed Egyptian illuminance clauses and responsible-engineer acceptance remain required.",
        ],
    }
    preview["preview_sha256"] = _canonical_hash(preview)
    return preview


def write_preview(preview: dict[str, Any], destination: Path) -> None:
    """Write a new preview atomically without overwriting an earlier review artifact."""
    target = destination.resolve(strict=False)
    if target.suffix.lower() != ".json" or target.exists() or target.is_symlink():
        raise RevitIntegrationError("Photometry preview destination must be a new JSON file.")
    target.parent.resolve(strict=True)
    temporary = target.with_name(f".{target.name}.tmp")
    temporary.write_text(json.dumps(preview, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    temporary.replace(target)


def build_parameter_update_preview(
    *,
    photometry_preview: Path,
    source_scratch_rvt: Path,
    output_relative_path: str,
) -> dict[str, Any]:
    """Build the separate exact L1 scratch-parameter update approval artifact."""
    preview_path = photometry_preview.resolve(strict=True)
    source = source_scratch_rvt.resolve(strict=True)
    try:
        evidence = json.loads(preview_path.read_text(encoding="utf-8-sig"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise RevitIntegrationError(f"Photometry preview is invalid: {exc}") from exc
    bindings = evidence.get("bindings")
    if (
        evidence.get("schema_version") != 1
        or evidence.get("execution_allowed") is not False
        or not isinstance(bindings, list)
        or _hash(source) != evidence.get("scratch_rvt_sha256")
    ):
        raise RevitIntegrationError("Photometry preview failed its source or safety binding.")
    ready = [item for item in bindings if item.get("disposition") == "READY_FOR_PARAMETER_PREVIEW"]
    if len(ready) != 1 or ready[0].get("design_id") != "L1":
        raise RevitIntegrationError("Exactly L1 must be ready for the parameter update preview.")
    item = ready[0]
    ies = item.get("ies")
    scratch = item.get("scratch")
    if not isinstance(ies, dict) or not isinstance(scratch, dict):
        raise RevitIntegrationError("L1 evidence is incomplete.")
    loaded = {
        parameter.get("name"): parameter.get("value")
        for parameter in scratch.get("loaded_parameters", [])
        if isinstance(parameter, dict)
    }
    expected_old = {
        "Apparent Load": "19 VA",
        "Wattage Comments": "19W",
        "Photometric Web File": "ES-M-PS-12S-827-PSD-MB-ALL-ALL.ies",
    }
    if any(loaded.get(name) != value for name, value in expected_old.items()):
        raise RevitIntegrationError("L1 loaded parameter values changed since live verification.")
    output = Path(output_relative_path)
    if output.is_absolute() or output.suffix.lower() != ".rvt" or ".." in output.parts:
        raise RevitIntegrationError("Parameter preview output path must be a relative new RVT path.")
    result: dict[str, Any] = {
        "schema_version": 1,
        "action": "update_l1_parameters_in_new_scratch_rvt",
        "status": "approval_required",
        "execution_allowed": False,
        "document_mutation_permitted": False,
        "save_allowed": False,
        "source_scratch_rvt_sha256": _hash(source),
        "photometry_preview_sha256": evidence.get("preview_sha256"),
        "target": {
            "design_id": "L1",
            "family_name": scratch.get("family_name"),
            "type_name": item.get("type_name"),
        },
        "changes": [
            {"parameter": "Apparent Load", "old": "19 VA", "new": "31 VA"},
            {"parameter": "Wattage Comments", "old": "19W", "new": "31W"},
            {
                "parameter": "Photometric Web File",
                "old": "ES-M-PS-12S-827-PSD-MB-ALL-ALL.ies",
                "new": Path(item["ies_relative_path"]).name,
            },
        ],
        "ies_relative_path": item.get("ies_relative_path"),
        "ies_sha256": item.get("ies_sha256"),
        "ies_lumens": ies.get("lumens"),
        "ies_input_watts": ies.get("input_watts"),
        "output_relative_path": output.as_posix(),
        "verification": [
            "save only to the exact new output path without overwrite",
            "reopen output and verify family/type identity",
            "verify 31 VA, 31W, exact IES filename and one electrical connector",
            "verify source scratch RVT hash remains unchanged",
        ],
    }
    result["preview_sha256"] = _canonical_hash(result)
    return result
