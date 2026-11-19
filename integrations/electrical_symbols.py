"""Validated, versioned electrical symbol catalog for bounded AutoCAD writers."""

from __future__ import annotations

import hashlib
import json
import math
import os
import re
import tempfile
import uuid
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path
from typing import Any, Callable, Mapping

from agent.exceptions import ConfirmationRequiredError, ElectricalSymbolCatalogError


_MAX_CATALOG_BYTES = 65_536
_MAX_SYMBOLS = 100
_MAX_ENTITIES = 32
_MAX_COORDINATE = 1_000_000_000
_IDENTIFIER_PATTERN = re.compile(r"[a-z][a-z0-9_]{0,47}")
_BLOCK_PATTERN = re.compile(r"AIENG_E_[A-Z0-9_]{1,48}_V[1-9][0-9]{0,3}")
_LAYER_PATTERN = re.compile(r"E-[A-Z0-9]{4}(?:-[A-Z0-9]{4})?")
_SHA256_PATTERN = re.compile(r"[0-9a-f]{64}")


@dataclass(frozen=True, slots=True)
class LineGeometry:
    """One validated line inside a block definition."""

    start: tuple[float, float, float]
    end: tuple[float, float, float]


@dataclass(frozen=True, slots=True)
class CircleGeometry:
    """One validated circle inside a block definition."""

    center: tuple[float, float, float]
    radius: float


SymbolGeometry = LineGeometry | CircleGeometry


@dataclass(frozen=True, slots=True)
class ElectricalSymbol:
    """One approved electrical symbol definition."""

    symbol_id: str
    block_name: str
    version: int
    description: str
    units: str
    insertion_layer: str
    layer_color: int
    linetype: str
    base_point: tuple[float, float, float]
    attribute_tags: tuple[str, ...]
    geometry: tuple[SymbolGeometry, ...]
    definition_sha256: str

    def to_public_dict(self) -> dict[str, object]:
        """Return bounded catalog metadata without duplicating drawing geometry."""
        return {
            "id": self.symbol_id,
            "block_name": self.block_name,
            "version": self.version,
            "description": self.description,
            "units": self.units,
            "insertion_layer": self.insertion_layer,
            "layer_color": self.layer_color,
            "linetype": self.linetype,
            "base_point": list(self.base_point),
            "attribute_tags": list(self.attribute_tags),
            "entity_count": len(self.geometry),
            "definition_sha256": self.definition_sha256,
        }


@dataclass(frozen=True, slots=True)
class ElectricalSymbolCatalog:
    """Immutable validated view of a catalog file."""

    catalog_version: int
    symbols: tuple[ElectricalSymbol, ...]

    @classmethod
    def load(cls, path: Path) -> ElectricalSymbolCatalog:
        """Load a bounded UTF-8 catalog and verify every definition hash."""
        try:
            resolved = path.expanduser().resolve(strict=True)
        except OSError as exc:
            raise ElectricalSymbolCatalogError(
                f"Cannot resolve electrical symbol catalog: {type(exc).__name__}."
            ) from exc
        if path.is_symlink() or not resolved.is_file():
            raise ElectricalSymbolCatalogError(
                "Electrical symbol catalog must be a regular non-link file."
            )
        try:
            raw = resolved.read_bytes()
        except OSError as exc:
            raise ElectricalSymbolCatalogError(
                f"Cannot read electrical symbol catalog: {type(exc).__name__}."
            ) from exc
        if not raw or len(raw) > _MAX_CATALOG_BYTES:
            raise ElectricalSymbolCatalogError(
                "Electrical symbol catalog is empty or exceeds 65536 bytes."
            )
        try:
            document = json.loads(raw.decode("utf-8"))
        except (UnicodeError, json.JSONDecodeError) as exc:
            raise ElectricalSymbolCatalogError(
                "Electrical symbol catalog must be valid UTF-8 JSON."
            ) from exc
        root = _exact_mapping(
            document,
            {"schema_version", "catalog_version", "symbols"},
            "catalog",
        )
        if root["schema_version"] != 1:
            raise ElectricalSymbolCatalogError(
                "Electrical symbol catalog schema_version must be 1."
            )
        catalog_version = _positive_integer(root["catalog_version"], "catalog_version")
        raw_symbols = root["symbols"]
        if (
            not isinstance(raw_symbols, list)
            or not raw_symbols
            or len(raw_symbols) > _MAX_SYMBOLS
        ):
            raise ElectricalSymbolCatalogError(
                "Electrical symbol catalog must contain 1 through 100 symbols."
            )
        symbols = tuple(_parse_symbol(item) for item in raw_symbols)
        identifiers = [item.symbol_id for item in symbols]
        block_names = [item.block_name for item in symbols]
        if len(identifiers) != len(set(identifiers)):
            raise ElectricalSymbolCatalogError("Electrical symbol IDs must be unique.")
        if len(block_names) != len(set(block_names)):
            raise ElectricalSymbolCatalogError("Electrical block names must be unique.")
        return cls(catalog_version, symbols)

    def get(self, symbol_id: str) -> ElectricalSymbol:
        """Return one exact allowlisted symbol ID."""
        if not isinstance(symbol_id, str) or _IDENTIFIER_PATTERN.fullmatch(symbol_id) is None:
            raise ElectricalSymbolCatalogError("Electrical symbol ID is invalid.")
        for symbol in self.symbols:
            if symbol.symbol_id == symbol_id:
                return symbol
        raise ElectricalSymbolCatalogError(
            f"Electrical symbol is not allowlisted: {symbol_id}."
        )

    def to_public_dict(self) -> dict[str, object]:
        """Return stable catalog metadata suitable for a command result."""
        return {
            "schema_version": 1,
            "catalog_version": self.catalog_version,
            "symbols": [item.to_public_dict() for item in self.symbols],
        }


@dataclass(frozen=True, slots=True)
class CatalogRegistrationResult:
    """Evidence for one atomic, backed-up catalog registration."""

    catalog_path: Path
    backup_path: Path
    catalog_version: int
    symbol: ElectricalSymbol

    def to_dict(self) -> dict[str, object]:
        return {
            "catalog_path": str(self.catalog_path),
            "backup_path": str(self.backup_path),
            "catalog_version": self.catalog_version,
            "symbol": self.symbol.to_public_dict(),
        }


def validate_catalog_definition(proposal: Mapping[str, object]) -> ElectricalSymbol:
    """Validate one exact proposal without reading or mutating the catalog."""
    return _parse_symbol(proposal)


def register_catalog_definition(
    catalog_path: Path,
    backup_directory: Path,
    proposal: Mapping[str, object],
    *,
    confirmed: bool = False,
    clock: Callable[[], datetime] | None = None,
) -> CatalogRegistrationResult:
    """Atomically append one exact proposal after backup and complete revalidation."""
    if not confirmed:
        raise ConfirmationRequiredError(
            "Electrical symbol catalog registration requires explicit confirmation."
        )
    catalog = ElectricalSymbolCatalog.load(catalog_path)
    proposed = _parse_symbol(proposal)
    if any(item.symbol_id == proposed.symbol_id for item in catalog.symbols):
        raise ElectricalSymbolCatalogError(
            f"Electrical symbol ID already exists: {proposed.symbol_id}."
        )
    if any(item.block_name == proposed.block_name for item in catalog.symbols):
        raise ElectricalSymbolCatalogError(
            f"Electrical block name already exists: {proposed.block_name}."
        )
    resolved = catalog_path.resolve(strict=True)
    try:
        original = resolved.read_bytes()
        document = json.loads(original.decode("utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise ElectricalSymbolCatalogError(
            "Cannot reload electrical symbol catalog for registration."
        ) from exc
    if not isinstance(document, dict) or not isinstance(document.get("symbols"), list):
        raise ElectricalSymbolCatalogError(
            "Electrical symbol catalog changed after validation."
        )
    next_version = catalog.catalog_version + 1
    document["catalog_version"] = next_version
    document["symbols"].append(dict(proposal))
    encoded = (
        json.dumps(document, ensure_ascii=False, indent=2, sort_keys=False) + "\n"
    ).encode("utf-8")
    temporary = _write_temporary(resolved.parent, resolved.name, encoded)
    try:
        ElectricalSymbolCatalog.load(temporary)
        if resolved.read_bytes() != original:
            raise ElectricalSymbolCatalogError(
                "Electrical symbol catalog changed concurrently; registration stopped."
            )
        backup_directory.mkdir(parents=True, exist_ok=True)
        timestamp = _registration_timestamp(clock or (lambda: datetime.now(UTC)))
        backup = backup_directory / (
            f"catalog-{timestamp}-{uuid.uuid4().hex[:8]}.json"
        )
        _write_new_file(backup, original)
        try:
            os.replace(temporary, resolved)
            ElectricalSymbolCatalog.load(resolved)
        except (OSError, ElectricalSymbolCatalogError) as exc:
            _atomic_replace_bytes(resolved, original)
            raise ElectricalSymbolCatalogError(
                "Electrical symbol registration failed and the catalog was restored."
            ) from exc
        return CatalogRegistrationResult(resolved, backup, next_version, proposed)
    finally:
        if temporary.exists():
            temporary.unlink()


def _write_temporary(directory: Path, name: str, content: bytes) -> Path:
    try:
        descriptor, temporary_name = tempfile.mkstemp(
            dir=directory, prefix=f".{name}.", suffix=".tmp"
        )
        temporary = Path(temporary_name)
        with os.fdopen(descriptor, "wb") as handle:
            handle.write(content)
            handle.flush()
            os.fsync(handle.fileno())
        return temporary
    except OSError as exc:
        raise ElectricalSymbolCatalogError(
            f"Cannot stage electrical symbol catalog: {type(exc).__name__}."
        ) from exc


def _write_new_file(path: Path, content: bytes) -> None:
    if path.exists():
        raise ElectricalSymbolCatalogError("Electrical symbol backup path already exists.")
    temporary = _write_temporary(path.parent, path.name, content)
    try:
        os.replace(temporary, path)
    except OSError as exc:
        raise ElectricalSymbolCatalogError(
            f"Cannot create electrical symbol catalog backup: {type(exc).__name__}."
        ) from exc
    finally:
        if temporary.exists():
            temporary.unlink()


def _atomic_replace_bytes(path: Path, content: bytes) -> None:
    temporary = _write_temporary(path.parent, path.name, content)
    try:
        try:
            os.replace(temporary, path)
        except OSError as exc:
            raise ElectricalSymbolCatalogError(
                "Cannot restore the electrical symbol catalog automatically; "
                "use the registration backup before continuing."
            ) from exc
    finally:
        if temporary.exists():
            temporary.unlink()


def _registration_timestamp(clock: Callable[[], datetime]) -> str:
    value = clock()
    if value.tzinfo is None:
        raise ElectricalSymbolCatalogError(
            "Electrical symbol registration clock must be timezone-aware."
        )
    return value.astimezone(UTC).strftime("%Y%m%dT%H%M%S%fZ")


def _parse_symbol(value: object) -> ElectricalSymbol:
    required = {
        "id",
        "block_name",
        "version",
        "description",
        "units",
        "insertion_layer",
        "layer_color",
        "linetype",
        "base_point",
        "attributes",
        "entities",
        "definition_sha256",
    }
    item = _exact_mapping(value, required, "symbol")
    symbol_id = _pattern_string(item["id"], _IDENTIFIER_PATTERN, "symbol id")
    block_name = _pattern_string(item["block_name"], _BLOCK_PATTERN, "block name")
    version = _positive_integer(item["version"], "symbol version")
    description = item["description"]
    if not isinstance(description, str) or not 1 <= len(description) <= 160:
        raise ElectricalSymbolCatalogError(
            "Electrical symbol description must contain 1 through 160 characters."
        )
    if item["units"] != "millimeters":
        raise ElectricalSymbolCatalogError("Electrical symbol units must be millimeters.")
    insertion_layer = _pattern_string(
        item["insertion_layer"], _LAYER_PATTERN, "insertion layer"
    )
    layer_color = _positive_integer(item["layer_color"], "layer color")
    if layer_color > 255:
        raise ElectricalSymbolCatalogError("Electrical symbol layer color must be ACI 1–255.")
    if item["linetype"] != "CONTINUOUS":
        raise ElectricalSymbolCatalogError(
            "Electrical symbol linetype must be CONTINUOUS."
        )
    base_point = _point(item["base_point"], "base point")
    attributes = item["attributes"]
    if attributes != []:
        raise ElectricalSymbolCatalogError(
            "Milestone 11 symbols must declare an empty attributes array."
        )
    raw_entities = item["entities"]
    if (
        not isinstance(raw_entities, list)
        or not raw_entities
        or len(raw_entities) > _MAX_ENTITIES
    ):
        raise ElectricalSymbolCatalogError(
            "Electrical symbol must contain 1 through 32 geometry entities."
        )
    geometry = tuple(_parse_geometry(entity) for entity in raw_entities)
    definition_hash = _pattern_string(
        item["definition_sha256"], _SHA256_PATTERN, "definition SHA-256"
    )
    hash_input = dict(item)
    del hash_input["definition_sha256"]
    calculated = calculate_definition_sha256(hash_input)
    if calculated != definition_hash:
        raise ElectricalSymbolCatalogError(
            f"Electrical symbol definition hash mismatch: {symbol_id}."
        )
    return ElectricalSymbol(
        symbol_id=symbol_id,
        block_name=block_name,
        version=version,
        description=description,
        units="millimeters",
        insertion_layer=insertion_layer,
        layer_color=layer_color,
        linetype="CONTINUOUS",
        base_point=base_point,
        attribute_tags=(),
        geometry=geometry,
        definition_sha256=definition_hash,
    )


def _parse_geometry(value: object) -> SymbolGeometry:
    if not isinstance(value, Mapping) or not isinstance(value.get("type"), str):
        raise ElectricalSymbolCatalogError("Electrical symbol entity must be an object.")
    if value["type"] == "line":
        item = _exact_mapping(value, {"type", "start", "end"}, "line entity")
        start = _point(item["start"], "line start")
        end = _point(item["end"], "line end")
        if start == end:
            raise ElectricalSymbolCatalogError(
                "Electrical symbol line endpoints must differ."
            )
        return LineGeometry(start, end)
    if value["type"] == "circle":
        item = _exact_mapping(value, {"type", "center", "radius"}, "circle entity")
        radius = _number(item["radius"], "circle radius")
        if not 0 < radius <= _MAX_COORDINATE:
            raise ElectricalSymbolCatalogError(
                "Electrical symbol circle radius must be positive and bounded."
            )
        return CircleGeometry(_point(item["center"], "circle center"), radius)
    raise ElectricalSymbolCatalogError("Electrical symbol entity type is not allowlisted.")


def calculate_definition_sha256(definition: Mapping[str, object]) -> str:
    """Calculate the canonical approval digest for one definition without its hash."""
    return hashlib.sha256(
        json.dumps(
            dict(definition),
            ensure_ascii=False,
            sort_keys=True,
            separators=(",", ":"),
        ).encode("utf-8")
    ).hexdigest()


def catalog_block_name_is_valid(value: str) -> bool:
    """Return whether *value* can be registered without renaming."""
    return isinstance(value, str) and _BLOCK_PATTERN.fullmatch(value) is not None


def _exact_mapping(value: object, keys: set[str], label: str) -> dict[str, Any]:
    if not isinstance(value, Mapping) or set(value) != keys:
        raise ElectricalSymbolCatalogError(
            f"Electrical symbol {label} fields are incomplete or unexpected."
        )
    if not all(isinstance(key, str) for key in value):
        raise ElectricalSymbolCatalogError(f"Electrical symbol {label} keys are invalid.")
    return dict(value)


def _pattern_string(value: object, pattern: re.Pattern[str], label: str) -> str:
    if not isinstance(value, str) or pattern.fullmatch(value) is None:
        raise ElectricalSymbolCatalogError(f"Electrical symbol {label} is invalid.")
    return value


def _positive_integer(value: object, label: str) -> int:
    if not isinstance(value, int) or isinstance(value, bool) or value <= 0:
        raise ElectricalSymbolCatalogError(
            f"Electrical symbol {label} must be a positive integer."
        )
    return value


def _number(value: object, label: str) -> float:
    if (
        not isinstance(value, (int, float))
        or isinstance(value, bool)
        or not math.isfinite(value)
        or abs(value) > _MAX_COORDINATE
    ):
        raise ElectricalSymbolCatalogError(
            f"Electrical symbol {label} must be a finite bounded number."
        )
    normalized = round(float(value), 9)
    return 0.0 if normalized == 0 else normalized


def _point(value: object, label: str) -> tuple[float, float, float]:
    if not isinstance(value, list) or len(value) != 3:
        raise ElectricalSymbolCatalogError(
            f"Electrical symbol {label} must be a three-number array."
        )
    point = tuple(_number(coordinate, label) for coordinate in value)
    return point[0], point[1], point[2]
