"""Immutable shared electrical project model with explicit provenance and unknowns."""

from __future__ import annotations

import math
import re
from dataclasses import dataclass, fields, is_dataclass
from enum import Enum
from typing import Any

from agent.exceptions import ElectricalModelError


_IDENTIFIER = re.compile(r"[a-z][a-z0-9_.:-]{0,127}")
_SOURCE_OBJECT_ID = re.compile(r"[0-9A-F]{1,32}")
_SHA256 = re.compile(r"[0-9a-f]{64}")
Point3D = tuple[float, float, float]


class Confidence(str, Enum):
    """Confidence attached to one observed or reviewed value."""

    OBSERVED = "observed"
    REVIEWED = "reviewed"
    INFERRED = "inferred"
    UNKNOWN = "unknown"


class PrivacyClassification(str, Enum):
    """Local handling classification for engineering context."""

    PROJECT_CONFIDENTIAL = "project_confidential"
    INTERNAL = "internal"
    PUBLIC = "public"


class SourceKind(str, Enum):
    """Allowlisted origins for shared-model facts."""

    AUTOCAD_SELECTION = "autocad_selection"
    AUTOCAD_DRAWING = "autocad_drawing"
    ENGINEER_INPUT = "engineer_input"
    CALCULATION = "calculation"
    CODE_EVIDENCE = "code_evidence"
    MANUFACTURER_EVIDENCE = "manufacturer_evidence"
    EXTERNAL_APPLICATION = "external_application"


class UnitSystem(str, Enum):
    """Supported normalized drawing unit systems for the initial model."""

    UNITLESS = "unitless"
    INCH = "inch"
    FOOT = "foot"
    MILLIMETER = "millimeter"
    CENTIMETER = "centimeter"
    METER = "meter"
    UNKNOWN = "unknown"


class JsonRecord:
    """Convert immutable model records to bounded JSON-compatible values."""

    def to_dict(self) -> dict[str, Any]:
        value = _json_value(self)
        if not isinstance(value, dict):
            raise ElectricalModelError("Electrical model record is not an object.")
        return value


@dataclass(frozen=True, slots=True)
class SourceReference(JsonRecord):
    """Stable reference to one source-application object."""

    application: str
    document_id: str
    object_id: str
    context_id: str

    def __post_init__(self) -> None:
        _require_identifier(self.application, "source application")
        _require_identifier(self.document_id, "source document ID")
        if _SOURCE_OBJECT_ID.fullmatch(self.object_id) is None:
            raise ElectricalModelError(
                "Source object ID must be 1 through 32 uppercase hexadecimal characters."
            )
        if re.fullmatch(r"[0-9a-f]{32}", self.context_id) is None:
            raise ElectricalModelError(
                "Source context ID must be 32 lowercase hexadecimal characters."
            )


@dataclass(frozen=True, slots=True)
class Provenance(JsonRecord):
    """Explain where model facts came from and whether they may leave the device."""

    source_kind: SourceKind
    captured_at: str
    confidence: Confidence
    privacy: PrivacyClassification
    provider_egress_allowed: bool
    evidence_sha256: str

    def __post_init__(self) -> None:
        if not isinstance(self.source_kind, SourceKind):
            raise ElectricalModelError("Provenance source kind is invalid.")
        if not isinstance(self.confidence, Confidence):
            raise ElectricalModelError("Provenance confidence is invalid.")
        if not isinstance(self.privacy, PrivacyClassification):
            raise ElectricalModelError("Provenance privacy classification is invalid.")
        if not isinstance(self.provider_egress_allowed, bool):
            raise ElectricalModelError("Provider egress flag must be boolean.")
        if not _is_utc_timestamp(self.captured_at):
            raise ElectricalModelError("Provenance captured_at must be a UTC timestamp.")
        if _SHA256.fullmatch(self.evidence_sha256) is None:
            raise ElectricalModelError("Provenance evidence hash must be SHA-256.")


@dataclass(frozen=True, slots=True)
class DrawingLayer(JsonRecord):
    """One observed AutoCAD layer and its bounded display state."""

    layer_id: str
    name: str
    color_index: int
    linetype: str
    is_off: bool
    is_frozen: bool
    is_locked: bool

    def __post_init__(self) -> None:
        _require_identifier(self.layer_id, "layer ID")
        _require_text(self.name, "layer name", 255)
        _require_text(self.linetype, "layer linetype", 255)
        if not isinstance(self.color_index, int) or isinstance(self.color_index, bool):
            raise ElectricalModelError("Layer color index must be an integer.")
        if not -255 <= self.color_index <= 256:
            raise ElectricalModelError("Layer color index is outside AutoCAD bounds.")
        if not all(isinstance(item, bool) for item in (self.is_off, self.is_frozen, self.is_locked)):
            raise ElectricalModelError("Layer state values must be boolean.")


@dataclass(frozen=True, slots=True)
class Drawing(JsonRecord):
    """One observed drawing representation in the shared project model."""

    drawing_id: str
    name: str
    unit_system: UnitSystem
    layout: str
    layer_ids: tuple[str, ...]
    database_modified: int

    def __post_init__(self) -> None:
        _require_identifier(self.drawing_id, "drawing ID")
        _require_text(self.name, "drawing name", 255)
        if not isinstance(self.unit_system, UnitSystem):
            raise ElectricalModelError("Drawing unit system is invalid.")
        _require_text(self.layout, "drawing layout", 255)
        _require_identifiers(self.layer_ids, "drawing layer IDs")
        if (
            not isinstance(self.database_modified, int)
            or isinstance(self.database_modified, bool)
            or not 0 <= self.database_modified <= 63
        ):
            raise ElectricalModelError("Drawing database-modified state is invalid.")


@dataclass(frozen=True, slots=True)
class Terminal(JsonRecord):
    """One explicitly known connection terminal."""

    terminal_id: str
    device_id: str
    name: str
    position: Point3D | None

    def __post_init__(self) -> None:
        _require_identifier(self.terminal_id, "terminal ID")
        _require_identifier(self.device_id, "terminal device ID")
        _require_text(self.name, "terminal name", 128)
        if self.position is not None:
            _require_point(self.position, "terminal position")


@dataclass(frozen=True, slots=True)
class Device(JsonRecord):
    """One recognized drawing device backed by a reviewed symbol definition."""

    device_id: str
    symbol_id: str
    block_name: str
    layer_id: str
    insertion_point: Point3D
    rotation_degrees: float
    scale: Point3D
    attributes: tuple[tuple[str, str], ...]
    terminals: tuple[Terminal, ...]
    source: SourceReference

    def __post_init__(self) -> None:
        for value, name in (
            (self.device_id, "device ID"),
            (self.symbol_id, "symbol ID"),
            (self.layer_id, "device layer ID"),
        ):
            _require_identifier(value, name)
        _require_text(self.block_name, "block name", 255)
        _require_point(self.insertion_point, "device insertion point")
        _require_finite(self.rotation_degrees, "device rotation")
        _require_point(self.scale, "device scale")
        if any(value == 0 for value in self.scale):
            raise ElectricalModelError(
                "Device scale values must be non-zero; negative values represent mirroring."
            )
        _require_pairs(self.attributes, "device attributes")
        _require_records(self.terminals, Terminal, "device terminals")
        if not isinstance(self.source, SourceReference):
            raise ElectricalModelError("Device source reference is invalid.")


@dataclass(frozen=True, slots=True)
class Panel(JsonRecord):
    panel_id: str
    name: str
    source: SourceReference | None

    def __post_init__(self) -> None:
        _require_identifier(self.panel_id, "panel ID")
        _require_text(self.name, "panel name", 128)
        _require_optional_record(self.source, SourceReference, "panel source")


@dataclass(frozen=True, slots=True)
class Circuit(JsonRecord):
    circuit_id: str
    panel_id: str | None
    name: str
    device_ids: tuple[str, ...]

    def __post_init__(self) -> None:
        _require_identifier(self.circuit_id, "circuit ID")
        if self.panel_id is not None:
            _require_identifier(self.panel_id, "circuit panel ID")
        _require_text(self.name, "circuit name", 128)
        _require_identifiers(self.device_ids, "circuit device IDs")


@dataclass(frozen=True, slots=True)
class ProtectionDevice(JsonRecord):
    protection_id: str
    circuit_id: str | None
    kind: str
    source: SourceReference | None

    def __post_init__(self) -> None:
        _require_identifier(self.protection_id, "protection ID")
        if self.circuit_id is not None:
            _require_identifier(self.circuit_id, "protection circuit ID")
        _require_text(self.kind, "protection kind", 64)
        _require_optional_record(self.source, SourceReference, "protection source")


@dataclass(frozen=True, slots=True)
class Cable(JsonRecord):
    cable_id: str
    circuit_id: str | None
    conductor_ids: tuple[str, ...]
    route_id: str | None
    source: SourceReference | None

    def __post_init__(self) -> None:
        _require_identifier(self.cable_id, "cable ID")
        if self.circuit_id is not None:
            _require_identifier(self.circuit_id, "cable circuit ID")
        if self.route_id is not None:
            _require_identifier(self.route_id, "cable route ID")
        _require_identifiers(self.conductor_ids, "cable conductor IDs")
        _require_optional_record(self.source, SourceReference, "cable source")


@dataclass(frozen=True, slots=True)
class Conductor(JsonRecord):
    conductor_id: str
    cable_id: str
    designation: str

    def __post_init__(self) -> None:
        _require_identifier(self.conductor_id, "conductor ID")
        _require_identifier(self.cable_id, "conductor cable ID")
        _require_text(self.designation, "conductor designation", 64)


@dataclass(frozen=True, slots=True)
class Load(JsonRecord):
    load_id: str
    device_id: str | None
    name: str
    source: SourceReference | None

    def __post_init__(self) -> None:
        _require_identifier(self.load_id, "load ID")
        if self.device_id is not None:
            _require_identifier(self.device_id, "load device ID")
        _require_text(self.name, "load name", 128)
        _require_optional_record(self.source, SourceReference, "load source")


@dataclass(frozen=True, slots=True)
class Route(JsonRecord):
    route_id: str
    layer_id: str
    points: tuple[Point3D, ...]
    source: SourceReference

    def __post_init__(self) -> None:
        _require_identifier(self.route_id, "route ID")
        _require_identifier(self.layer_id, "route layer ID")
        if not isinstance(self.points, tuple) or not 2 <= len(self.points) <= 4096:
            raise ElectricalModelError("Route must contain 2 through 4096 points.")
        for point in self.points:
            _require_point(point, "route point")
        if not isinstance(self.source, SourceReference):
            raise ElectricalModelError("Route source reference is invalid.")


@dataclass(frozen=True, slots=True)
class UnknownObject(JsonRecord):
    """An observed object whose engineering meaning was not guessed."""

    unknown_id: str
    original_type: str
    layer_id: str
    reason: str
    source: SourceReference

    def __post_init__(self) -> None:
        _require_identifier(self.unknown_id, "unknown object ID")
        _require_identifier(self.layer_id, "unknown object layer ID")
        _require_text(self.original_type, "unknown object type", 128)
        _require_text(self.reason, "unknown object reason", 256)
        if not isinstance(self.source, SourceReference):
            raise ElectricalModelError("Unknown object source reference is invalid.")


@dataclass(frozen=True, slots=True)
class ElectricalProjectModel(JsonRecord):
    """Schema-v1 shared model produced from one immutable context snapshot."""

    schema_version: int
    project_id: str
    document_id: str
    context_id: str
    unit_system: UnitSystem
    provenance: Provenance
    drawings: tuple[Drawing, ...]
    layers: tuple[DrawingLayer, ...]
    devices: tuple[Device, ...]
    panels: tuple[Panel, ...]
    circuits: tuple[Circuit, ...]
    protection_devices: tuple[ProtectionDevice, ...]
    cables: tuple[Cable, ...]
    conductors: tuple[Conductor, ...]
    loads: tuple[Load, ...]
    routes: tuple[Route, ...]
    unknown_objects: tuple[UnknownObject, ...]
    warnings: tuple[str, ...]

    def __post_init__(self) -> None:
        if self.schema_version != 1:
            raise ElectricalModelError("Electrical project model schema must be 1.")
        _require_identifier(self.project_id, "project ID")
        _require_identifier(self.document_id, "model document ID")
        if re.fullmatch(r"[0-9a-f]{32}", self.context_id) is None:
            raise ElectricalModelError("Model context ID is invalid.")
        if not isinstance(self.unit_system, UnitSystem):
            raise ElectricalModelError("Model unit system is invalid.")
        if not isinstance(self.provenance, Provenance):
            raise ElectricalModelError("Model provenance is invalid.")
        identifiers: list[str] = []
        for collection, record_type, attribute, name in (
            (self.drawings, Drawing, "drawing_id", "model drawings"),
            (self.layers, DrawingLayer, "layer_id", "model layers"),
            (self.devices, Device, "device_id", "model devices"),
            (self.panels, Panel, "panel_id", "model panels"),
            (self.circuits, Circuit, "circuit_id", "model circuits"),
            (self.protection_devices, ProtectionDevice, "protection_id", "model protection devices"),
            (self.cables, Cable, "cable_id", "model cables"),
            (self.conductors, Conductor, "conductor_id", "model conductors"),
            (self.loads, Load, "load_id", "model loads"),
            (self.routes, Route, "route_id", "model routes"),
            (self.unknown_objects, UnknownObject, "unknown_id", "model unknown objects"),
        ):
            _require_records(collection, record_type, name)
            identifiers.extend(getattr(item, attribute) for item in collection)
        if len(self.drawings) != 1:
            raise ElectricalModelError(
                "A context-derived electrical model must contain exactly one drawing."
            )
        if len(identifiers) != len(set(identifiers)):
            raise ElectricalModelError("Electrical model object IDs must be unique.")
        if not isinstance(self.warnings, tuple):
            raise ElectricalModelError("Model warnings must be an immutable tuple.")
        for warning in self.warnings:
            _require_text(warning, "model warning", 512)


def unit_system_from_insunits(value: int) -> UnitSystem:
    """Map the bounded AutoCAD INSUNITS code without inventing unsupported units."""
    return {
        0: UnitSystem.UNITLESS,
        1: UnitSystem.INCH,
        2: UnitSystem.FOOT,
        4: UnitSystem.MILLIMETER,
        5: UnitSystem.CENTIMETER,
        6: UnitSystem.METER,
    }.get(value, UnitSystem.UNKNOWN)


def _json_value(value: Any) -> Any:
    if isinstance(value, Enum):
        return value.value
    if is_dataclass(value):
        return {item.name: _json_value(getattr(value, item.name)) for item in fields(value)}
    if isinstance(value, tuple):
        return [_json_value(item) for item in value]
    if isinstance(value, list):
        return [_json_value(item) for item in value]
    if isinstance(value, dict):
        return {str(key): _json_value(item) for key, item in value.items()}
    return value


def _require_identifier(value: str, name: str) -> None:
    if not isinstance(value, str) or _IDENTIFIER.fullmatch(value) is None:
        raise ElectricalModelError(f"{name} must be a bounded lowercase identifier.")


def _require_text(
    value: str, name: str, maximum: int, *, allow_empty: bool = False
) -> None:
    if (
        not isinstance(value, str)
        or (not value and not allow_empty)
        or len(value.encode("utf-8")) > maximum
    ):
        qualifier = "" if allow_empty else "non-empty and "
        raise ElectricalModelError(
            f"{name} must be {qualifier}at most {maximum} bytes."
        )
    if any(ord(character) < 32 and character not in "\t" for character in value):
        raise ElectricalModelError(f"{name} contains unsupported control characters.")


def _require_finite(value: float, name: str) -> None:
    if not isinstance(value, (int, float)) or isinstance(value, bool) or not math.isfinite(value):
        raise ElectricalModelError(f"{name} must be finite.")
    if abs(float(value)) > 1_000_000_000:
        raise ElectricalModelError(f"{name} exceeds its numeric bound.")


def _require_point(value: Point3D, name: str) -> None:
    if not isinstance(value, tuple) or len(value) != 3:
        raise ElectricalModelError(f"{name} must contain exactly three coordinates.")
    for coordinate in value:
        _require_finite(coordinate, name)


def _require_identifiers(values: tuple[str, ...], name: str) -> None:
    if not isinstance(values, tuple):
        raise ElectricalModelError(f"{name} must be an immutable tuple.")
    for value in values:
        _require_identifier(value, name)
    if len(values) != len(set(values)):
        raise ElectricalModelError(f"{name} must be unique.")


def _require_pairs(values: tuple[tuple[str, str], ...], name: str) -> None:
    if not isinstance(values, tuple):
        raise ElectricalModelError(f"{name} must be an immutable tuple.")
    keys: list[str] = []
    for pair in values:
        if not isinstance(pair, tuple) or len(pair) != 2:
            raise ElectricalModelError(f"{name} must contain immutable key/value pairs.")
        key, value = pair
        _require_text(key, name, 128)
        _require_text(value, name, 4096, allow_empty=True)
        keys.append(key.casefold())
    if len(keys) != len(set(keys)):
        raise ElectricalModelError(f"{name} contain duplicate keys.")


def _require_records(values: object, record_type: type, name: str) -> None:
    if not isinstance(values, tuple) or any(
        not isinstance(value, record_type) for value in values
    ):
        raise ElectricalModelError(f"{name} must be an immutable typed tuple.")


def _require_optional_record(value: object, record_type: type, name: str) -> None:
    if value is not None and not isinstance(value, record_type):
        raise ElectricalModelError(f"{name} is invalid.")


def _is_utc_timestamp(value: str) -> bool:
    if not isinstance(value, str) or not value.endswith("Z") or len(value) > 40:
        return False
    try:
        from datetime import datetime

        datetime.fromisoformat(value[:-1] + "+00:00")
    except ValueError:
        return False
    return True
