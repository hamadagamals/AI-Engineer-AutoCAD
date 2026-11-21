"""Integrity and deliberate scope checks for second-PC continuation bundles."""

import hashlib
import io
import json
import os
import subprocess
import tempfile
import zipfile
from pathlib import Path

import pytest

from agent.exceptions import PackagingError
from integrations.build_transfer_bundle import build_bundle

ROOT = Path(__file__).resolve().parents[1]
_R03_DRAWING = ROOT / "workspace" / "reports" / "hutchison-ports-panel-ga-20260914" / "HUTCHISON_PANEL_GA_PREVIEW_R03.dwg"
_SKIP_MISSING_R03 = pytest.mark.skipif(
    not _R03_DRAWING.is_file(),
    reason="Reviewed R03 field-project DWG is absent in this checkout (gitignored workspace/reports).",
)


@_SKIP_MISSING_R03
def test_transfer_bundle_preserves_source_tests_assets_and_honest_status(tmp_path: Path) -> None:
    output = tmp_path / "transfer.zip"
    result = build_bundle(ROOT, output)
    assert result["sha256"] == hashlib.sha256(output.read_bytes()).hexdigest()
    with zipfile.ZipFile(output) as archive:
        assert archive.testzip() is None
        manifest = json.loads(archive.read("TRANSFER-MANIFEST.json"))
        assert manifest["component_move_execution_enabled"] is False
        for name, expected in manifest["files"].items():
            assert hashlib.sha256(archive.read(name)).hexdigest() == expected
        assert "tests/test_autocad_component_moves.py" in archive.namelist()
        assert "INSTALL-TRANSFER.ps1" in archive.namelist()
        assert all("__pycache__" not in name and not name.endswith((".bak", ".pyc")) for name in archive.namelist())
        project = "project-assets/hutchison-ports-panel-ga-20260914/"
        assert hashlib.sha256(archive.read(project + "HUTCHISON_PANEL_GA_PREVIEW_R03.dwg")).hexdigest() == "e0bf950ad6e204b9760c0a42bfa0da289ccb7ee730239c1590e6901bdec4bb04"
        assert len([name for name in archive.namelist() if name.startswith(project + "source-dwg/")]) == 17
        with zipfile.ZipFile(io.BytesIO(archive.read("AI-Engineer-portable-r50.zip"))) as portable:
            assert "integrations/autocad_component_moves.py" in portable.namelist()
            assert "integrations/autocad_drawing_summary.py" in portable.namelist()
            assert "tests/test_autocad_drawing_summary.py" in archive.namelist()
            assert "docs/WORK_QUEUE.md" in portable.namelist()
            assert 'new ProjectAgentPaletteControl()' in portable.read("integrations/autocad_context_plugin/PaletteCommands.cs").decode()
            assert "GEMINI_API_KEY" not in json.loads(portable.read("data/settings.json"))
    with pytest.raises(PackagingError, match="overwrite"):
        build_bundle(ROOT, output)


def test_transfer_startup_selects_destination_autocad_and_retains_secure_key_prompt() -> None:
    launcher = (ROOT / "START-AI-ENGINEER-WITH-GEMINI.ps1").read_text(encoding="utf-8")
    assert "[ValidateSet(2021, 2023)]" in launcher
    assert "AutoCADYear = $AutoCADYear" in launcher
    assert "$previewArguments.AutoCADRoot = $AutoCADRoot" in launcher
    assert "-AsSecureString" in launcher
    installer = (ROOT / "docs/transfer-20260917/INSTALL-TRANSFER.ps1").read_text(encoding="utf-8")
    assert "Destination already exists" in installer
    assert "Transfer checksum mismatch" in installer
    assert "Remove-Item" not in installer


@_SKIP_MISSING_R03
@pytest.mark.skipif(os.name != "nt", reason="Windows PowerShell transfer installer acceptance")
def test_windows_transfer_installer_restores_assets_and_rejects_corruption() -> None:
    with tempfile.TemporaryDirectory(prefix="aier50-") as directory:
        temporary = Path(directory).resolve(strict=True)
        output = temporary / "transfer.zip"
        build_bundle(ROOT, output)
        bundle = temporary / "bundle"
        with zipfile.ZipFile(output) as archive:
            archive.extractall(bundle)
        destination = temporary / "app"
        command = ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                   str(bundle / "INSTALL-TRANSFER.ps1"), "-Destination", str(destination)]
        complete = subprocess.run(command, capture_output=True, text=True, timeout=60, check=False)
        assert complete.returncode == 0, complete.stdout + complete.stderr
        assert (destination / "tests/test_autocad_component_moves.py").is_file()
        assert (destination / "integrations/autocad_component_moves.py").is_file()
        assert (destination / "START-HERE-AR.md").is_file()
        drawing = destination / "workspace/review-drawings/HUTCHISON_PANEL_GA_PREVIEW_R03.dwg"
        assert hashlib.sha256(drawing.read_bytes()).hexdigest() == "e0bf950ad6e204b9760c0a42bfa0da289ccb7ee730239c1590e6901bdec4bb04"
        refused = subprocess.run(command, capture_output=True, text=True, timeout=30, check=False)
        assert refused.returncode != 0
        assert "Destination already exists" in refused.stderr
        (bundle / "START-HERE-AR.md").write_bytes(b"changed test artifact")
        command[-1] = str(temporary / "bad")
        corrupted = subprocess.run(command, capture_output=True, text=True, timeout=30, check=False)
        assert corrupted.returncode != 0
        assert "checksum mismatch" in corrupted.stderr
        assert not (temporary / "bad").exists()
