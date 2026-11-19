from __future__ import annotations

import hashlib
import json
from pathlib import Path

import pytest

from agent.exceptions import RevitIntegrationError
from integrations.revit_lighting_photometry_preview import (
    BindingSpec,
    build_parameter_update_preview,
    build_preview,
    parse_ies,
    write_preview,
)


def _hash(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def _ies(path: Path, lumens: float = 2500, watts: float = 19) -> None:
    path.write_text(
        "IESNA:LM-63-2002\n[LUMCAT] ORDER\n[LUMINAIRE] TEST\nTILT=NONE\n"
        f"1 {lumens} 1 1 1 1 2 1 1 0\n1 1 {watts}\n0\n0\n1\n",
        encoding="utf-8",
    )


def _fixture(tmp_path: Path) -> tuple[Path, Path, Path, list[BindingSpec]]:
    library = tmp_path / "library"
    library.mkdir()
    instances = []
    specs = []
    for index, design_id in enumerate(("L1", "L2", "L4", "L5-L6"), 1):
        rfa = library / f"{design_id}.rfa"
        ies = library / f"{design_id}.ies"
        rfa.write_bytes(f"family-{design_id}".encode())
        _ies(ies, 1000 * index, 10 * index)
        type_name = f"TYPE-{design_id}"
        instances.append({
            "design_id": design_id,
            "family_name": f"FAMILY-{design_id}",
            "type_name": type_name,
            "category": "Lighting Fixtures",
            "connector_count": 1,
            "parameters": [],
        })
        specs.append(BindingSpec(
            design_id, type_name, rfa.name, _hash(rfa), f"ORDER-{index}", "220-240 V",
            1000 * index, 10 * index, ies.name, _hash(ies), "REVIEW", "evidence",
        ))
    rvt = tmp_path / "scratch.rvt"
    rvt.write_bytes(b"verified-rvt")
    report = tmp_path / "report.json"
    report.write_text(json.dumps({
        "schema_version": 1,
        "output_sha256": _hash(rvt),
        "source_document_mutated": False,
        "reopened_verified": True,
        "instances": instances,
    }), encoding="utf-8")
    return library, report, rvt, specs


def test_parse_ies_reads_lamp_output_and_input_watts(tmp_path: Path) -> None:
    path = tmp_path / "fixture.ies"
    _ies(path, 2883.8, 19.257)
    value = parse_ies(path)
    assert value.standard == "IESNA:LM-63-2002"
    assert value.lumens == 2883.8
    assert value.input_watts == 19.257


def test_build_preview_is_non_executable_and_self_hashed(tmp_path: Path) -> None:
    library, report, rvt, specs = _fixture(tmp_path)
    preview = build_preview(library_root=library, scratch_report=report, scratch_rvt=rvt, specs=specs)
    assert preview["execution_allowed"] is False
    assert preview["document_mutation_permitted"] is False
    assert len(preview["bindings"]) == 4
    assert len(preview["preview_sha256"]) == 64
    assert preview["bindings"][0]["product_vs_ies_lumen_delta"] == 0


def test_build_preview_rejects_changed_evidence(tmp_path: Path) -> None:
    library, report, rvt, specs = _fixture(tmp_path)
    (library / "L1.ies").write_text("changed", encoding="utf-8")
    with pytest.raises(RevitIntegrationError, match="hash differs"):
        build_preview(library_root=library, scratch_report=report, scratch_rvt=rvt, specs=specs)


def test_write_preview_refuses_overwrite(tmp_path: Path) -> None:
    destination = tmp_path / "preview.json"
    write_preview({"schema_version": 1}, destination)
    with pytest.raises(RevitIntegrationError, match="new JSON"):
        write_preview({"schema_version": 1}, destination)


def test_parameter_update_preview_binds_only_ready_l1(tmp_path: Path) -> None:
    source = tmp_path / "source.rvt"
    source.write_bytes(b"rvt")
    evidence = tmp_path / "photometry.json"
    evidence.write_text(json.dumps({
        "schema_version": 1,
        "execution_allowed": False,
        "scratch_rvt_sha256": _hash(source),
        "preview_sha256": "A" * 64,
        "bindings": [{
            "design_id": "L1",
            "disposition": "READY_FOR_PARAMETER_PREVIEW",
            "type_name": "TYPE-L1",
            "ies_relative_path": "ies/L1.ies",
            "ies_sha256": "B" * 64,
            "ies": {"lumens": 4310.8, "input_watts": 31.0},
            "scratch": {
                "family_name": "FAMILY-L1",
                "loaded_parameters": [
                    {"name": "Apparent Load", "value": "19 VA"},
                    {"name": "Wattage Comments", "value": "19W"},
                    {"name": "Photometric Web File", "value": "ES-M-PS-12S-827-PSD-MB-ALL-ALL.ies"},
                ],
            },
        }],
    }), encoding="utf-8")
    preview = build_parameter_update_preview(
        photometry_preview=evidence,
        source_scratch_rvt=source,
        output_relative_path="scratch/L1-validation.rvt",
    )
    assert preview["execution_allowed"] is False
    assert preview["save_allowed"] is False
    assert preview["changes"][0] == {"parameter": "Apparent Load", "old": "19 VA", "new": "31 VA"}
    assert len(preview["preview_sha256"]) == 64


def test_parameter_update_preview_rejects_changed_loaded_values(tmp_path: Path) -> None:
    source = tmp_path / "source.rvt"
    source.write_bytes(b"rvt")
    evidence = tmp_path / "photometry.json"
    evidence.write_text(json.dumps({
        "schema_version": 1,
        "execution_allowed": False,
        "scratch_rvt_sha256": _hash(source),
        "preview_sha256": "A" * 64,
        "bindings": [{
            "design_id": "L1",
            "disposition": "READY_FOR_PARAMETER_PREVIEW",
            "type_name": "TYPE-L1",
            "ies_relative_path": "L1.ies",
            "ies_sha256": "B" * 64,
            "ies": {"lumens": 4310.8, "input_watts": 31.0},
            "scratch": {"family_name": "FAMILY-L1", "loaded_parameters": []},
        }],
    }), encoding="utf-8")
    with pytest.raises(RevitIntegrationError, match="values changed"):
        build_parameter_update_preview(
            photometry_preview=evidence,
            source_scratch_rvt=source,
            output_relative_path="scratch/L1-validation.rvt",
        )
