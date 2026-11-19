from __future__ import annotations

import hashlib
import json
import zipfile
from dataclasses import replace
from pathlib import Path

import pytest

from agent.exceptions import RevitIntegrationError
from integrations.revit_lighting_official_refresh import (
    OfficialIesSelection,
    _canonical_hash,
    build_remaining_parameter_preview,
    extract_and_build_refresh,
    write_refresh,
)


IES = """IESNA:LM-63-2002
[LUMCAT] 123
[LUMINAIRE] Exact fixture
TILT=NONE
1 2500.4 1 1 1 1 2 1 1 0
1 1 19
0
0
1
""".encode()


def _hash(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest().upper()


def _fixture(tmp_path: Path, *, member: str = "fixture.ies") -> tuple[Path, Path, OfficialIesSelection]:
    downloads = tmp_path / "downloads"
    extracted = tmp_path / "extracted"
    downloads.mkdir()
    extracted.mkdir()
    archive = downloads / "official.zip"
    with zipfile.ZipFile(archive, "w") as bundle:
        bundle.writestr(member, IES)
    selection = OfficialIesSelection(
        design_ids=("L2",), product_order_code="123", product_lumens=2500,
        product_watts=19, source_url="https://example.test/official",
        archive_relative_path=archive.name, archive_sha256=_hash(archive.read_bytes()),
        entry_name=member, entry_sha256=_hash(IES), output_relative_path=f"reviewed/{Path(member).name}",
    )
    return downloads, extracted, selection


def test_extracts_exact_ready_selection_and_writes_new_artifact(tmp_path: Path) -> None:
    downloads, extracted, selection = _fixture(tmp_path)
    result = extract_and_build_refresh(
        download_root=downloads, extracted_root=extracted, selections=[selection]
    )
    item = result["selections"][0]
    assert item["disposition"] == "READY_FOR_PARAMETER_PREVIEW"
    assert item["ies"]["lumens"] == 2500.4
    assert item["ies"]["input_watts"] == 19
    assert (extracted / selection.output_relative_path).read_bytes() == IES
    target = tmp_path / "refresh.json"
    write_refresh(result, target)
    with pytest.raises(RevitIntegrationError, match="new JSON"):
        write_refresh(result, target)


def test_rejects_changed_archive_hash(tmp_path: Path) -> None:
    downloads, extracted, selection = _fixture(tmp_path)
    changed = replace(selection, archive_sha256="0" * 64)
    with pytest.raises(RevitIntegrationError, match="identity"):
        extract_and_build_refresh(download_root=downloads, extracted_root=extracted, selections=[changed])


def test_rejects_archive_path_traversal(tmp_path: Path) -> None:
    downloads, extracted, selection = _fixture(tmp_path, member="../fixture.ies")
    with pytest.raises(RevitIntegrationError, match="unsafe"):
        extract_and_build_refresh(download_root=downloads, extracted_root=extracted, selections=[selection])


def test_holds_nonmatching_photometry(tmp_path: Path) -> None:
    downloads, extracted, selection = _fixture(tmp_path)
    held = OfficialIesSelection(
        design_ids=selection.design_ids, product_order_code=selection.product_order_code,
        product_lumens=2400, product_watts=18, source_url=selection.source_url,
        archive_relative_path=selection.archive_relative_path, archive_sha256=selection.archive_sha256,
        entry_name=selection.entry_name, entry_sha256=selection.entry_sha256,
        output_relative_path=selection.output_relative_path,
    )
    result = extract_and_build_refresh(download_root=downloads, extracted_root=extracted, selections=[held])
    assert result["selections"][0]["disposition"] == "HOLD_PRODUCT_IES_CONFLICT"


def test_builds_bounded_remaining_parameter_preview(tmp_path: Path) -> None:
    source = tmp_path / "source.rvt"
    source.write_bytes(b"l1-output")
    scratch_hash = "A" * 64
    refresh = {
        "schema_version": 1, "execution_allowed": False,
        "selections": [
            {
                "product_order_code": "911401816685", "design_ids": ["L2"],
                "disposition": "READY_FOR_PARAMETER_PREVIEW",
                "output_relative_path": "batten/L2.ies", "entry_sha256": "B" * 64,
                "ies": {"lumens": 2500.4, "input_watts": 19},
            },
            {
                "product_order_code": "910505100047", "design_ids": ["L5", "L6"],
                "disposition": "READY_FOR_PARAMETER_PREVIEW",
                "output_relative_path": "wet/L5.ies", "entry_sha256": "C" * 64,
                "ies": {"lumens": 4000.6, "input_watts": 30},
            },
        ],
    }
    refresh["preview_sha256"] = _canonical_hash(refresh)
    refresh_path = tmp_path / "refresh.json"
    refresh_path.write_text(json.dumps(refresh), encoding="utf-8")
    l1_report = tmp_path / "l1.json"
    l1_report.write_text(json.dumps({
        "reopened_verified": True, "source_document_mutated": False,
        "source_sha256_before": scratch_hash,
        "output_sha256": hashlib.sha256(source.read_bytes()).hexdigest().upper(),
    }), encoding="utf-8")
    old = tmp_path / "old.json"
    instances = []
    for design_id, family, type_name, ies in (
        ("L2", "Batten", "BN126C LED25_41_64S_840 PSU L1200", "WT210C LED39S_840 PMMA PSU L1500.ies"),
        ("L5-L6", "Wet", "WT120C 40S_840 PSU L1200", "WT198C LED18S840 PSU PCO L600.ies"),
    ):
        instances.append({
            "design_id": design_id, "family_name": family, "type_name": type_name,
            "parameters": [
                {"name": "Apparent Load", "value": "19 VA"},
                {"name": "Wattage Comments", "value": "19W"},
                {"name": "Photometric Web File", "value": ies},
            ],
        })
    old.write_text(json.dumps({
        "reopened_verified": True, "source_document_mutated": False,
        "output_sha256": scratch_hash, "instances": instances,
    }), encoding="utf-8")
    result = build_remaining_parameter_preview(
        refresh_path=refresh_path, source_rvt=source, l1_verification_report=l1_report,
        original_scratch_report=old, output_relative_path="workspace/out.rvt",
    )
    assert result["targets"][0]["changes"][0]["new"] == "L2.ies"
    assert result["targets"][1]["changes"][0]["new"] == "30 VA"
    assert result["held_targets"][0]["design_id"] == "L4"
