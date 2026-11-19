"""Tests for the read-only AutoCAD product-release readiness report."""

from __future__ import annotations

import json
from pathlib import Path

import pytest

import launcher
from agent.exceptions import PackagingError
from core.autocad_release_readiness import (
    AUTOCAD_PRODUCT_FILES,
    inspect_autocad_release_readiness,
)


def _ready_fixture(tmp_path: Path, year: int = 2021) -> tuple[Path, Path, dict[str, str]]:
    project = tmp_path / "project"
    project.mkdir()
    for relative in AUTOCAD_PRODUCT_FILES:
        path = project / Path(relative)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(f"fixture:{relative}\n", encoding="utf-8")
    autocad = tmp_path / f"AutoCAD {year}"
    autocad.mkdir()
    for name in (
        "acad.exe", "accoreconsole.exe", "acmgd.dll", "acdbmgd.dll",
        "accoremgd.dll", "AcWindows.dll",
    ):
        (autocad / name).write_bytes(b"fixture")
    windows = tmp_path / "Windows"
    compiler = windows / "Microsoft.NET" / "Framework64" / "v4.0.30319" / "csc.exe"
    compiler.parent.mkdir(parents=True)
    compiler.write_bytes(b"fixture")
    program_files_x86 = tmp_path / "Program Files (x86)"
    framework = "v4.8" if year == 2023 else "v4.7.2"
    references = (
        program_files_x86
        / "Reference Assemblies" / "Microsoft" / "Framework" / ".NETFramework" / framework
    )
    references.mkdir(parents=True)
    (references / "PresentationCore.dll").write_bytes(b"fixture")
    (references / "WindowsBase.dll").write_bytes(b"fixture")
    return project, autocad, {
        "WINDIR": str(windows),
        "ProgramFiles(x86)": str(program_files_x86),
    }


def test_complete_readiness_is_traceable_and_has_no_actions(tmp_path: Path) -> None:
    project, autocad, environment = _ready_fixture(tmp_path)
    report = inspect_autocad_release_readiness(
        project,
        autocad_root=autocad,
        environment=environment,
        platform_name="Windows",
        python_version=(3, 13, 14),
        port_probe=lambda port: port == 8765,
    )

    assert report.complete is True
    assert report.autocad_year == 2021
    assert report.expected_acadver == "24.0"
    assert report.framework_reference_version == "v4.7.2"
    assert len(report.product_files) == len(AUTOCAD_PRODUCT_FILES)
    assert all(len(item.sha256) == 64 for item in report.product_files)
    assert report.missing_autocad_files == ()
    assert report.missing_build_files == ()
    assert report.provider_egress_performed is False
    assert report.drawing_mutation_performed is False
    assert all(item.startswith("PASS") for item in report.findings)


def test_readiness_reports_missing_dependencies_and_busy_port(tmp_path: Path) -> None:
    project, autocad, environment = _ready_fixture(tmp_path)
    (autocad / "acad.exe").unlink()
    (project / AUTOCAD_PRODUCT_FILES[0]).unlink()
    report = inspect_autocad_release_readiness(
        project,
        autocad_root=autocad,
        environment=environment,
        platform_name="Windows",
        python_version=(3, 12, 9),
        port_probe=lambda port: False,
    )

    assert report.complete is False
    assert report.missing_autocad_files == ("acad.exe",)
    assert len(report.product_files) == len(AUTOCAD_PRODUCT_FILES) - 1
    assert any(item.startswith("FAIL | product-file:") for item in report.findings)
    assert any("FAIL | python" in item for item in report.findings)
    assert any("FAIL | localhost-port" in item for item in report.findings)


def test_autocad_2023_readiness_uses_exact_release_and_framework(tmp_path: Path) -> None:
    project, autocad, environment = _ready_fixture(tmp_path, 2023)
    report = inspect_autocad_release_readiness(
        project, autocad_root=autocad, autocad_year=2023,
        environment=environment, platform_name="Windows",
        python_version=(3, 13, 14), port_probe=lambda _port: True,
    )
    assert report.complete is True
    assert report.autocad_year == 2023
    assert report.expected_acadver == "24.2"
    assert report.framework_reference_version == "v4.8"
    assert any("autocad-2023-files" in item for item in report.findings)


@pytest.mark.parametrize("port", [0, 1023, 65536, True])
def test_readiness_rejects_unsafe_port(tmp_path: Path, port: object) -> None:
    with pytest.raises(PackagingError, match="port"):
        inspect_autocad_release_readiness(tmp_path, port=port)  # type: ignore[arg-type]


def test_launcher_autocad_readiness_is_machine_readable(
    capsys: pytest.CaptureFixture[str],
) -> None:
    assert launcher.main(["autocad-readiness"]) == 0
    value = json.loads(capsys.readouterr().out)
    assert value["readiness_version"] == "autocad.release_readiness.v2"
    assert value["provider_egress_performed"] is False
    assert value["drawing_mutation_performed"] is False
