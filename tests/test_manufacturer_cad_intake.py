"""Regression tests for exact, evidence-first manufacturer CAD intake."""

from pathlib import Path

import pytest

from agent.exceptions import ManufacturerEvidenceError
from integrations.manufacturer_cad_intake import inspect_cad_assets


def _step(path: Path, product: str, *, complete: bool = True) -> Path:
    ending = "END-ISO-10303-21;" if complete else ""
    path.write_text(
        f"ISO-10303-21;\nHEADER;\nFILE_NAME('{product}',(''));\nENDSEC;\n"
        f"DATA;\n#1=PRODUCT('{product}','{product}','',());\nENDSEC;\n{ending}\n",
        encoding="latin-1",
    )
    return path


def test_exact_part_match_duplicate_detection_and_unbound_asset(tmp_path: Path) -> None:
    exact = _step(tmp_path / "RE22R1MYMR.stp", "RE22R1MYMR")
    duplicate = tmp_path / "copy.stp"
    duplicate.write_bytes(exact.read_bytes())
    other = _step(tmp_path / "C25W32D250.stp", "C25W32D250")
    result = inspect_cad_assets(
        (exact, duplicate, other), requested_part_numbers=("RE22R1MYMR", "C25B3TM160")
    )
    assert result[0].exact_requested_matches == ("RE22R1MYMR",)
    assert result[1].duplicate_of_sha256 == result[0].sha256
    assert result[2].exact_requested_matches == ()
    assert "unbound" in result[2].findings[0]


def test_incomplete_step_and_unsupported_asset_are_not_silently_accepted(tmp_path: Path) -> None:
    incomplete = _step(tmp_path / "A9F77316.stp", "A9F77316", complete=False)
    assert inspect_cad_assets((incomplete,), requested_part_numbers=("A9F77316",))[0].structurally_complete is False
    unsupported = tmp_path / "part.obj"
    unsupported.write_text("mesh", encoding="utf-8")
    with pytest.raises(ManufacturerEvidenceError, match="Unsupported"):
        inspect_cad_assets((unsupported,), requested_part_numbers=("A9F77316",))
