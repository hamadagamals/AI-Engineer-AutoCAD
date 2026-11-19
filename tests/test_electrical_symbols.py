"""Tests for the versioned, hash-approved electrical symbol catalog."""

from __future__ import annotations

import json
from datetime import UTC, datetime
from pathlib import Path

import pytest

from agent.exceptions import ConfirmationRequiredError, ElectricalSymbolCatalogError
import integrations.electrical_symbols as symbol_module
from integrations.electrical_symbols import (
    ElectricalSymbolCatalog,
    calculate_definition_sha256,
    register_catalog_definition,
)


PROJECT_ROOT = Path(__file__).resolve().parents[1]
CATALOG_PATH = PROJECT_ROOT / "plugins" / "electrical_symbols" / "catalog.json"


def test_builtin_catalog_exposes_bounded_stable_metadata() -> None:
    catalog = ElectricalSymbolCatalog.load(CATALOG_PATH)

    assert catalog.catalog_version == 1
    assert len(catalog.symbols) == 1
    symbol = catalog.get("socket_single")
    assert symbol.block_name == "AIENG_E_SOCKET_SINGLE_V1"
    assert symbol.insertion_layer == "E-POWR-DEVC"
    assert symbol.units == "millimeters"
    assert symbol.attribute_tags == ()
    assert len(symbol.geometry) == 3
    assert catalog.to_public_dict()["symbols"] == [symbol.to_public_dict()]


def test_catalog_detects_unapproved_definition_edits(tmp_path: Path) -> None:
    document = json.loads(CATALOG_PATH.read_text(encoding="utf-8"))
    document["symbols"][0]["description"] = "Changed without hash approval"
    changed = tmp_path / "catalog.json"
    changed.write_text(json.dumps(document), encoding="utf-8")

    with pytest.raises(ElectricalSymbolCatalogError, match="hash mismatch"):
        ElectricalSymbolCatalog.load(changed)


@pytest.mark.parametrize(
    ("field", "value", "message"),
    [
        ("attributes", ["TAG"], "empty attributes"),
        ("units", "inches", "millimeters"),
        ("insertion_layer", "USER-LAYER", "insertion layer"),
    ],
)
def test_catalog_rejects_unsupported_symbol_contracts(
    tmp_path: Path, field: str, value: object, message: str
) -> None:
    document = json.loads(CATALOG_PATH.read_text(encoding="utf-8"))
    document["symbols"][0][field] = value
    invalid = tmp_path / f"{field}.json"
    invalid.write_text(json.dumps(document), encoding="utf-8")

    with pytest.raises(ElectricalSymbolCatalogError, match=message):
        ElectricalSymbolCatalog.load(invalid)


def test_catalog_rejects_unknown_or_malformed_symbol_id() -> None:
    catalog = ElectricalSymbolCatalog.load(CATALOG_PATH)

    with pytest.raises(ElectricalSymbolCatalogError, match="not allowlisted"):
        catalog.get("panel_main")
    with pytest.raises(ElectricalSymbolCatalogError, match="ID is invalid"):
        catalog.get("../../socket")


def test_catalog_missing_file_uses_project_exception(tmp_path: Path) -> None:
    with pytest.raises(ElectricalSymbolCatalogError, match="Cannot resolve"):
        ElectricalSymbolCatalog.load(tmp_path / "missing.json")


def _proposal() -> dict[str, object]:
    document = json.loads(CATALOG_PATH.read_text(encoding="utf-8"))["symbols"][0]
    document.update(
        {
            "id": "socket_custom",
            "block_name": "AIENG_E_SOCKET_CUSTOM_V1",
            "description": "Reviewed user-authored electrical symbol socket_custom.",
        }
    )
    document.pop("definition_sha256")
    document["definition_sha256"] = calculate_definition_sha256(document)
    return document


def test_catalog_registration_requires_confirmation_and_creates_backup(
    tmp_path: Path,
) -> None:
    catalog_path = tmp_path / "catalog.json"
    original = CATALOG_PATH.read_bytes()
    catalog_path.write_bytes(original)
    backup_directory = tmp_path / "backup"
    proposal = _proposal()

    with pytest.raises(ConfirmationRequiredError, match="confirmation"):
        register_catalog_definition(catalog_path, backup_directory, proposal)
    assert catalog_path.read_bytes() == original
    assert not backup_directory.exists()

    result = register_catalog_definition(
        catalog_path,
        backup_directory,
        proposal,
        confirmed=True,
        clock=lambda: datetime(2026, 8, 6, tzinfo=UTC),
    )

    assert result.catalog_version == 2
    assert result.symbol.symbol_id == "socket_custom"
    assert result.backup_path.read_bytes() == original
    registered = ElectricalSymbolCatalog.load(catalog_path)
    assert registered.catalog_version == 2
    assert registered.get("socket_custom").block_name == "AIENG_E_SOCKET_CUSTOM_V1"


def test_catalog_registration_rejects_duplicates_without_mutation(tmp_path: Path) -> None:
    catalog_path = tmp_path / "catalog.json"
    original = CATALOG_PATH.read_bytes()
    catalog_path.write_bytes(original)
    duplicate = json.loads(CATALOG_PATH.read_text(encoding="utf-8"))["symbols"][0]

    with pytest.raises(ElectricalSymbolCatalogError, match="already exists"):
        register_catalog_definition(
            catalog_path,
            tmp_path / "backup",
            duplicate,
            confirmed=True,
        )
    assert catalog_path.read_bytes() == original
    assert not (tmp_path / "backup").exists()


def test_catalog_registration_rolls_back_post_write_validation_failure(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    catalog_path = tmp_path / "catalog.json"
    original = CATALOG_PATH.read_bytes()
    catalog_path.write_bytes(original)
    real_load = symbol_module.ElectricalSymbolCatalog.load
    calls = 0

    def failing_load(cls: type[ElectricalSymbolCatalog], path: Path):
        nonlocal calls
        calls += 1
        if calls == 3:
            raise ElectricalSymbolCatalogError("post-write failure")
        return real_load(path)

    monkeypatch.setattr(
        symbol_module.ElectricalSymbolCatalog, "load", classmethod(failing_load)
    )

    with pytest.raises(ElectricalSymbolCatalogError, match="restored"):
        register_catalog_definition(
            catalog_path,
            tmp_path / "backup",
            _proposal(),
            confirmed=True,
            clock=lambda: datetime(2026, 8, 6, tzinfo=UTC),
        )
    assert catalog_path.read_bytes() == original
    assert len(list((tmp_path / "backup").glob("catalog-*.json"))) == 1


def test_catalog_registration_reports_failed_automatic_restore(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    catalog_path = tmp_path / "catalog.json"
    catalog_path.write_bytes(CATALOG_PATH.read_bytes())
    real_load = symbol_module.ElectricalSymbolCatalog.load
    real_replace = symbol_module.os.replace
    load_calls = 0
    replace_calls = 0

    def failing_load(cls: type[ElectricalSymbolCatalog], path: Path):
        nonlocal load_calls
        load_calls += 1
        if load_calls == 3:
            raise ElectricalSymbolCatalogError("post-write failure")
        return real_load(path)

    def failing_restore(source: Path, destination: Path) -> None:
        nonlocal replace_calls
        replace_calls += 1
        if replace_calls == 3:
            raise OSError("restore denied")
        real_replace(source, destination)

    monkeypatch.setattr(
        symbol_module.ElectricalSymbolCatalog, "load", classmethod(failing_load)
    )
    monkeypatch.setattr(symbol_module.os, "replace", failing_restore)

    with pytest.raises(
        ElectricalSymbolCatalogError, match="use the registration backup"
    ):
        register_catalog_definition(
            catalog_path,
            tmp_path / "backup",
            _proposal(),
            confirmed=True,
            clock=lambda: datetime(2026, 8, 6, tzinfo=UTC),
        )
    backups = list((tmp_path / "backup").glob("catalog-*.json"))
    assert len(backups) == 1
    assert backups[0].read_bytes() == CATALOG_PATH.read_bytes()
