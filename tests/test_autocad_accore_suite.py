"""AutoCAD accoreconsole.exe regression harness presence and optional live runner."""

from __future__ import annotations

import os
import subprocess
import sys
from pathlib import Path

import pytest


ROOT = Path(__file__).resolve().parent.parent
HARNESS_CS = ROOT / "tests" / "autocad_accore_harness" / "AcCoreRegressionVerifier.cs"
RUNNER_PS1 = ROOT / "scripts" / "run_autocad_accore_tests.ps1"
BUILD_PS1 = ROOT / "integrations" / "autocad_context_plugin" / "build.ps1"
ACCORE_EXE = Path(r"C:\Program Files\Autodesk\AutoCAD 2021\accoreconsole.exe")


def test_accore_regression_harness_and_build_wired() -> None:
    assert HARNESS_CS.is_file(), f"Missing AutoCAD harness: {HARNESS_CS}"
    assert RUNNER_PS1.is_file(), f"Missing AutoCAD runner script: {RUNNER_PS1}"

    harness_text = HARNESS_CS.read_text(encoding="utf-8")
    assert "ACCOREREGRESS" in harness_text
    assert "ACCORE_REGRESSION_PASS" in harness_text
    assert "generate_panel_schedule_and_sld" in harness_text
    assert "audit_electrical_drawing" in harness_text
    assert "BENCHMARK_30C_300F_MS" in harness_text

    build_text = BUILD_PS1.read_text(encoding="utf-8")
    assert "CadSpatialIndex.cs" in build_text
    assert "CadPanelAndQaTools.cs" in build_text
    assert "CadProjectLegendDb.cs" in build_text
    assert "CadVisualCaptureTools.cs" in build_text


def test_live_accoreconsole_regression_suite() -> None:
    if os.environ.get("RUN_ACCORE_TESTS") != "1":
        pytest.skip("Set RUN_ACCORE_TESTS=1 on Windows with AutoCAD 2021+ installed to run live accoreconsole tests.")
    if sys.platform != "win32" or not ACCORE_EXE.is_file():
        pytest.skip("AutoCAD 2021 accoreconsole.exe is not available on this host.")

    proc = subprocess.run(
        [
            "powershell",
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            str(RUNNER_PS1),
        ],
        cwd=str(ROOT),
        capture_output=True,
        text=True,
        timeout=180,
    )
    assert proc.returncode == 0, f"accore runner failed ({proc.returncode}):\n{proc.stdout}\n{proc.stderr}"
    assert "ACCORE_REGRESSION_PASS" in proc.stdout, f"Missing PASS marker:\n{proc.stdout}\n{proc.stderr}"

