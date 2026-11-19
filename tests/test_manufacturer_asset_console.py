"""Tests for the interactive manufacturer-asset launcher's bounded input helpers."""

from __future__ import annotations

import pytest

from agent.exceptions import ManufacturerEvidenceError
from core.manufacturer_asset_collection import ManufacturerAssetKind
from integrations.manufacturer_asset_console import _domains, _index, _indices, _kinds


def test_console_parses_official_domains_and_ordered_unique_kinds() -> None:
    assert _domains("www.se.com, download.schneider-electric.com") == (
        "www.se.com", "download.schneider-electric.com",
    )
    assert _kinds("pdf,3d,2d,image,pdf") == (
        ManufacturerAssetKind.DATA_SHEET,
        ManufacturerAssetKind.CAD_3D,
        ManufacturerAssetKind.CAD_2D,
        ManufacturerAssetKind.IMAGE,
    )


def test_console_parses_one_based_ordered_choices() -> None:
    assert _index("2", 3) == 1
    assert _indices("3,1,2", 3) == (2, 0, 1)
    with pytest.raises(ManufacturerEvidenceError, match="unique"):
        _indices("1,1", 3)
    with pytest.raises(ManufacturerEvidenceError, match="outside"):
        _index("4", 3)


def test_console_rejects_unknown_asset_kind() -> None:
    with pytest.raises(ManufacturerEvidenceError, match="Unsupported"):
        _kinds("pdf,exe")
