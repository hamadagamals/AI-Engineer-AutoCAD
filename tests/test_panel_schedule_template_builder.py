"""Source-level safety checks for the approved Excel COM template builder."""

from pathlib import Path


PROJECT_ROOT = Path(__file__).resolve().parents[1]
SCRIPT = PROJECT_ROOT / "integrations" / "build_panel_schedule_from_template.ps1"


def test_builder_preserves_template_and_rejects_existing_output() -> None:
    source = SCRIPT.read_text(encoding="utf-8")

    assert "OutputPath must not overwrite the registered template" in source
    assert "OutputPath already exists and will not be overwritten" in source
    assert "Registered template changed during generation" in source
    assert "[IO.File]::Copy($templatePath, $outputFullPath, $false)" in source


def test_builder_rejects_formula_overwrites_and_errors() -> None:
    source = SCRIPT.read_text(encoding="utf-8")

    assert "Request cannot overwrite formula cell" in source
    assert "Estimated styling cannot be applied to formula cell" in source
    assert "formula error cell(s)" in source
    assert "formula_totals_recolored = $false" in source
