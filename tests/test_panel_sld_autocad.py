"""Tests for the reviewed DB3-GA2 AutoCAD SLD producer."""

from __future__ import annotations

import hashlib
import json
from pathlib import Path

import pytest

from agent.exceptions import AutoCADIntegrationError, ConfirmationRequiredError
from core.file_manager import FileManager
from integrations.autocad import (
    AutoCADInstallation,
    AutoCADManager,
    _ProcessResult,
    _parse_panel_sld_draft_markers,
)
from integrations.panel_sld_autocad import (
    BOUNDS,
    LAYERS,
    PROFILE,
    build_panel_sld_autocad_plan,
)


PROJECT_ROOT = Path(__file__).resolve().parents[1]
PREVIEW = PROJECT_ROOT / "workspace" / "reports" / "db3-ga2-sld-preview-20260824.json"


def _preview() -> dict[str, object]:
    return json.loads(PREVIEW.read_text(encoding="utf-8"))


def _manager(tmp_path: Path) -> tuple[AutoCADManager, Path]:
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    install = tmp_path / "AutoCAD 2021"
    install.mkdir()
    acad = install / "acad.exe"
    core = install / "accoreconsole.exe"
    acad.write_bytes(b"acad")
    core.write_bytes(b"core")
    files = FileManager([workspace], tmp_path / "backup")
    installation = AutoCADInstallation(
        "AutoCAD 2021", "24.0.47.0", "English", install, acad, core, True
    )
    return AutoCADManager(files, locator=lambda: installation), workspace


def _output(*, count: int, preview_sha: str, source_sha: str) -> str:
    return (
        "AIENG_ACADVER=24.0\nAIENG_INSUNITS=4\nAIENG_TILEMODE=1\n"
        f"AIENG_PROFILE={PROFILE}\nAIENG_PREVIEWSHA256={preview_sha}\n"
        f"AIENG_SOURCESHA256={source_sha}\nAIENG_REGIONBEFORE=0\n"
        f"AIENG_LAYERPREEXISTING=0\nAIENG_EXPECTEDCOUNT={count}\n"
        f"AIENG_CREATEDCOUNT={count}\nAIENG_CONTENTVALID=1\n"
        f"AIENG_BEFORECOUNT=4848\nAIENG_AFTERCOUNT={4848 + count}\n"
        "AIENG_DBMOD=0\nAIENG_DONE=1\n"
    )


def test_plan_is_exact_hash_bound_and_preserves_reviewed_unknowns() -> None:
    preview = _preview()
    digest = str(preview["preview_sha256"])
    plan = build_panel_sld_autocad_plan(preview, expected_preview_sha256=digest)

    assert plan.preview_sha256 == digest
    assert plan.bounds == BOUNDS
    assert plan.entity_count == 234
    assert "BLANK WAYS ARE NOT SPARES" in plan.script
    assert "SPARE CIRCUIT" not in plan.script
    assert "CB TBD" in plan.script
    assert "BUSBAR RATING TBD" in plan.script
    assert "400/230 V, 3 PHASE, 4 WIRE" in plan.script
    assert "180.0 W" in plan.script
    assert "LOAD TBD" in plan.script
    assert '(ssget "_C" (list 38000.000 -35000.000 0.0)' in plan.script
    assert '(command-s "_.QSAVE")' in plan.script
    assert all(name in plan.script for name, _ in LAYERS)

    changed = dict(preview)
    changed["panel"] = "DB3-GA1"
    with pytest.raises(ValueError, match="approved SHA-256"):
        build_panel_sld_autocad_plan(changed, expected_preview_sha256=digest)


def test_manager_creates_only_verified_scratch_output(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manager, workspace = _manager(tmp_path)
    source = workspace / "lighting-working.dwg"
    original = b"reviewed source drawing"
    source.write_bytes(original)
    preview = _preview()
    preview["source_drawing_sha256"] = hashlib.sha256(original).hexdigest()
    unsigned = dict(preview)
    unsigned.pop("preview_sha256")
    from integrations.panel_sld_autocad import canonical_sha256

    preview["preview_sha256"] = canonical_sha256(unsigned)
    preview_path = workspace / "db3-ga2-preview.json"
    preview_path.write_text(json.dumps(preview), encoding="utf-8")
    plan = build_panel_sld_autocad_plan(
        preview, expected_preview_sha256=str(preview["preview_sha256"])
    )
    captured: dict[str, Path] = {}

    def fake_run(command: tuple[str, ...], *, cwd: Path) -> _ProcessResult:
        captured["cwd"] = cwd
        scratch = cwd / "source.dwg"
        scratch.write_bytes(scratch.read_bytes() + b" verified sld")
        return _ProcessResult(
            0,
            _output(
                count=plan.entity_count,
                preview_sha=str(preview["preview_sha256"]),
                source_sha=hashlib.sha256(original).hexdigest(),
            ),
            "",
            False,
            False,
        )

    monkeypatch.setattr(manager, "_run_core_console", fake_run)
    result = manager.create_panel_sld_draft_scratch(
        source,
        preview_path=preview_path,
        expected_source_sha256=hashlib.sha256(original).hexdigest(),
        expected_preview_sha256=str(preview["preview_sha256"]),
        confirmed=True,
    )

    assert source.read_bytes() == original
    assert result.source_unchanged is True
    assert result.scratch_changed is True
    assert result.created_entity_count == 234
    assert result.region_entity_count_before == 0
    assert result.entity_count_after == result.entity_count_before + 234
    assert result.placement_bounds == BOUNDS
    assert (captured["cwd"] / "drawing-output.json").is_file()

    with pytest.raises(ConfirmationRequiredError):
        manager.create_panel_sld_draft_scratch(
            source,
            preview_path=preview_path,
            expected_source_sha256=hashlib.sha256(original).hexdigest(),
            expected_preview_sha256=str(preview["preview_sha256"]),
        )


def test_parser_rejects_occupied_region() -> None:
    preview = _preview()
    digest = str(preview["preview_sha256"])
    source = str(preview["source_drawing_sha256"])
    plan = build_panel_sld_autocad_plan(preview, expected_preview_sha256=digest)
    output = _output(count=plan.entity_count, preview_sha=digest, source_sha=source)
    with pytest.raises(AutoCADIntegrationError, match="reviewed preview and placement"):
        _parse_panel_sld_draft_markers(
            output.replace("AIENG_REGIONBEFORE=0", "AIENG_REGIONBEFORE=1"),
            expected_preview_sha256=digest,
            expected_source_sha256=source,
            expected_count=plan.entity_count,
            maximum_count=10_000,
        )
