"""Deterministic, source-linked quantity takeoff from the shared electrical model."""

from __future__ import annotations

import hashlib
import json
import math
import re
from dataclasses import dataclass
from enum import Enum
from typing import Any

from agent.exceptions import QuantityTakeoffError
from core.electrical_drafting import canonical_model_sha256
from core.electrical_model import (
    Device,
    ElectricalProjectModel,
    JsonRecord,
    Route,
    SourceReference,
    UnitSystem,
)


CALCULATION_VERSION = "m19.takeoff.v1"
_IDENTIFIER = re.compile(r"[a-z][a-z0-9_.:-]{0,127}")
_SHA256 = re.compile(r"[0-9a-f]{64}")
_METERS_PER_UNIT = {
    UnitSystem.INCH: 0.0254,
    UnitSystem.FOOT: 0.3048,
    UnitSystem.MILLIMETER: 0.001,
    UnitSystem.CENTIMETER: 0.01,
    UnitSystem.METER: 1.0,
}


class QuantityUnit(str, Enum):
    """Units emitted by the first takeoff calculation version."""

    EACH = "each"
    METER = "meter"


@dataclass(frozen=True, slots=True)
class RouteLengthOverride(JsonRecord):
    """A deliberate reviewed replacement for one calculated route total."""

    route_id: str
    final_total_meters: float
    reason: str
    reviewed_by: str

    def __post_init__(self) -> None:
        _identifier(self.route_id, "override route ID")
        _bounded_number(self.final_total_meters, "override total", minimum=0.0)
        _text(self.reason, "override reason", 512)
        _text(self.reviewed_by, "override reviewer", 128)


@dataclass(frozen=True, slots=True)
class TakeoffPolicy(JsonRecord):
    """Explicit calculation inputs that cannot be inferred from drawing geometry."""

    calculation_version: str = CALCULATION_VERSION
    vertical_allowance_per_route_meters: float = 0.0
    termination_allowance_per_end_meters: float = 0.0
    waste_percent: float = 0.0
    route_overrides: tuple[RouteLengthOverride, ...] = ()

    def __post_init__(self) -> None:
        if self.calculation_version != CALCULATION_VERSION:
            raise QuantityTakeoffError("Unsupported quantity calculation version.")
        _bounded_number(
            self.vertical_allowance_per_route_meters,
            "vertical allowance",
            minimum=0.0,
        )
        _bounded_number(
            self.termination_allowance_per_end_meters,
            "termination allowance",
            minimum=0.0,
        )
        _bounded_number(self.waste_percent, "waste percent", minimum=0.0, maximum=100.0)
        if not isinstance(self.route_overrides, tuple) or any(
            not isinstance(item, RouteLengthOverride) for item in self.route_overrides
        ):
            raise QuantityTakeoffError("Route overrides must be an immutable typed tuple.")
        route_ids = tuple(item.route_id for item in self.route_overrides)
        if len(route_ids) != len(set(route_ids)):
            raise QuantityTakeoffError("A route may have only one reviewed override.")


@dataclass(frozen=True, slots=True)
class DeviceQuantity(JsonRecord):
    """Count of recognized devices under one exact symbol classification."""

    symbol_id: str
    block_name: str
    quantity: int
    unit: QuantityUnit
    device_ids: tuple[str, ...]
    sources: tuple[SourceReference, ...]
    classification_rule: str

    def __post_init__(self) -> None:
        _identifier(self.symbol_id, "quantity symbol ID")
        _text(self.block_name, "quantity block name", 255)
        if not isinstance(self.quantity, int) or isinstance(self.quantity, bool) or self.quantity < 1:
            raise QuantityTakeoffError("Device quantity must be a positive integer.")
        if self.unit is not QuantityUnit.EACH:
            raise QuantityTakeoffError("Device quantity unit must be each.")
        if not isinstance(self.device_ids, tuple) or any(
            not isinstance(item, str) for item in self.device_ids
        ):
            raise QuantityTakeoffError("Quantity device IDs must be an immutable tuple.")
        for device_id in self.device_ids:
            _identifier(device_id, "quantity device ID")
        if len(self.device_ids) != self.quantity or len(set(self.device_ids)) != self.quantity:
            raise QuantityTakeoffError("Device quantity must match unique source device IDs.")
        if not isinstance(self.sources, tuple) or any(
            not isinstance(item, SourceReference) for item in self.sources
        ) or len(self.sources) != self.quantity:
            raise QuantityTakeoffError("Device quantity must retain one source per device.")
        if self.classification_rule != "exact_symbol_id_and_block_name":
            raise QuantityTakeoffError("Device classification rule is unsupported.")


@dataclass(frozen=True, slots=True)
class RouteLengthQuantity(JsonRecord):
    """Auditable length components for one exact model route."""

    route_id: str
    source: SourceReference
    source_unit: UnitSystem
    output_unit: QuantityUnit
    geometric_length_meters: float
    vertical_allowance_meters: float
    termination_allowance_meters: float
    waste_meters: float
    calculated_total_meters: float
    reviewed_override_meters: float | None
    final_total_meters: float
    calculation_rule: str

    def __post_init__(self) -> None:
        _identifier(self.route_id, "quantity route ID")
        if not isinstance(self.source, SourceReference):
            raise QuantityTakeoffError("Route quantity source reference is invalid.")
        if self.source_unit not in _METERS_PER_UNIT:
            raise QuantityTakeoffError("Route quantity source unit is unsupported.")
        if self.output_unit is not QuantityUnit.METER:
            raise QuantityTakeoffError("Route quantity output unit must be meter.")
        for value, name in (
            (self.geometric_length_meters, "geometric length"),
            (self.vertical_allowance_meters, "vertical allowance"),
            (self.termination_allowance_meters, "termination allowance"),
            (self.waste_meters, "waste length"),
            (self.calculated_total_meters, "calculated total"),
            (self.final_total_meters, "final total"),
        ):
            _bounded_number(value, name, minimum=0.0)
        if self.reviewed_override_meters is not None:
            _bounded_number(self.reviewed_override_meters, "reviewed override", minimum=0.0)
        expected_final = (
            self.reviewed_override_meters
            if self.reviewed_override_meters is not None
            else self.calculated_total_meters
        )
        if self.final_total_meters != expected_final:
            raise QuantityTakeoffError("Route final total does not match its reviewed state.")
        if self.calculation_rule != "three_dimensional_polyline_plus_allowances_then_waste":
            raise QuantityTakeoffError("Route calculation rule is unsupported.")


@dataclass(frozen=True, slots=True)
class TakeoffReport(JsonRecord):
    """Self-hashed read-only takeoff result bound to one context snapshot."""

    schema_version: int
    project_id: str
    document_id: str
    context_id: str
    context_evidence_sha256: str
    model_sha256: str
    calculation_version: str
    policy: TakeoffPolicy
    device_quantities: tuple[DeviceQuantity, ...]
    route_quantities: tuple[RouteLengthQuantity, ...]
    unknown_object_ids: tuple[str, ...]
    is_complete: bool
    warnings: tuple[str, ...]
    drawing_mutation_performed: bool
    report_sha256: str

    def __post_init__(self) -> None:
        if self.schema_version != 1:
            raise QuantityTakeoffError("Quantity report schema must be 1.")
        _identifier(self.project_id, "quantity project ID")
        _identifier(self.document_id, "quantity document ID")
        if re.fullmatch(r"[0-9a-f]{32}", self.context_id) is None:
            raise QuantityTakeoffError("Quantity context ID is invalid.")
        for value, name in (
            (self.context_evidence_sha256, "context evidence hash"),
            (self.model_sha256, "model hash"),
            (self.report_sha256, "report hash"),
        ):
            if _SHA256.fullmatch(value) is None:
                raise QuantityTakeoffError(f"Quantity {name} must be SHA-256.")
        if self.calculation_version != CALCULATION_VERSION:
            raise QuantityTakeoffError("Quantity report calculation version is unsupported.")
        if not isinstance(self.policy, TakeoffPolicy):
            raise QuantityTakeoffError("Quantity report policy is invalid.")
        _typed_tuple(self.device_quantities, DeviceQuantity, "device quantities")
        _typed_tuple(self.route_quantities, RouteLengthQuantity, "route quantities")
        if not isinstance(self.unknown_object_ids, tuple):
            raise QuantityTakeoffError("Unknown object IDs must be an immutable tuple.")
        for unknown_id in self.unknown_object_ids:
            _identifier(unknown_id, "unknown object ID")
        if len(self.unknown_object_ids) != len(set(self.unknown_object_ids)):
            raise QuantityTakeoffError("Unknown object IDs must be unique.")
        if not isinstance(self.is_complete, bool) or self.is_complete == bool(
            self.unknown_object_ids
        ):
            raise QuantityTakeoffError("Quantity completeness does not match unknown objects.")
        if not isinstance(self.warnings, tuple):
            raise QuantityTakeoffError("Quantity warnings must be an immutable tuple.")
        for warning in self.warnings:
            _text(warning, "quantity warning", 512)
        if self.drawing_mutation_performed is not False:
            raise QuantityTakeoffError("A quantity report cannot claim drawing mutation.")
        value = self.to_dict()
        del value["report_sha256"]
        if self.report_sha256 != _canonical_sha256(value):
            raise QuantityTakeoffError("Quantity report SHA-256 does not match its content.")


def create_takeoff_report(
    model: ElectricalProjectModel,
    *,
    context_evidence_sha256: str,
    policy: TakeoffPolicy | None = None,
) -> TakeoffReport:
    """Calculate one deterministic report without modifying or persisting drawing data."""
    if not isinstance(model, ElectricalProjectModel):
        raise QuantityTakeoffError("Quantity takeoff requires a typed electrical model.")
    if _SHA256.fullmatch(context_evidence_sha256) is None:
        raise QuantityTakeoffError("Context evidence hash must be SHA-256.")
    if model.provenance.evidence_sha256 != context_evidence_sha256:
        raise QuantityTakeoffError("Model provenance does not match context evidence.")
    drawing = model.drawings[0]
    if drawing.unit_system is not model.unit_system:
        raise QuantityTakeoffError("Drawing and model unit systems do not match.")
    try:
        meters_per_unit = _METERS_PER_UNIT[model.unit_system]
    except KeyError as exc:
        raise QuantityTakeoffError(
            "Quantity takeoff requires an explicit supported drawing length unit."
        ) from exc

    if policy is not None and not isinstance(policy, TakeoffPolicy):
        raise QuantityTakeoffError("Quantity takeoff policy must be typed.")
    active_policy = policy or TakeoffPolicy()
    overrides = {item.route_id: item for item in active_policy.route_overrides}
    route_ids = {route.route_id for route in model.routes}
    unknown_overrides = sorted(overrides.keys() - route_ids)
    if unknown_overrides:
        raise QuantityTakeoffError(
            f"Reviewed override references unknown route: {unknown_overrides[0]}."
        )

    grouped: dict[tuple[str, str], list[Device]] = {}
    for device in model.devices:
        grouped.setdefault((device.symbol_id, device.block_name), []).append(device)
    device_quantities = tuple(
        DeviceQuantity(
            symbol_id=symbol_id,
            block_name=block_name,
            quantity=len(devices),
            unit=QuantityUnit.EACH,
            device_ids=tuple(sorted(device.device_id for device in devices)),
            sources=tuple(
                device.source
                for device in sorted(devices, key=lambda item: item.device_id)
            ),
            classification_rule="exact_symbol_id_and_block_name",
        )
        for (symbol_id, block_name), devices in sorted(grouped.items())
    )

    route_quantities = tuple(
        _route_quantity(
            route,
            model.unit_system,
            meters_per_unit,
            active_policy,
            overrides.get(route.route_id),
        )
        for route in sorted(model.routes, key=lambda item: item.route_id)
    )
    unknown_ids = tuple(sorted(item.unknown_id for item in model.unknown_objects))
    warnings = tuple(model.warnings) + (
        (
            f"{len(unknown_ids)} source object(s) remain unclassified; report is incomplete.",
        )
        if unknown_ids
        else ()
    )
    payload: dict[str, Any] = {
        "schema_version": 1,
        "project_id": model.project_id,
        "document_id": model.document_id,
        "context_id": model.context_id,
        "context_evidence_sha256": context_evidence_sha256,
        "model_sha256": canonical_model_sha256(model),
        "calculation_version": active_policy.calculation_version,
        "policy": active_policy,
        "device_quantities": device_quantities,
        "route_quantities": route_quantities,
        "unknown_object_ids": unknown_ids,
        "is_complete": not unknown_ids,
        "warnings": warnings,
        "drawing_mutation_performed": False,
    }
    return TakeoffReport(**payload, report_sha256=_canonical_sha256(payload))


def _route_quantity(
    route: Route,
    source_unit: UnitSystem,
    factor: float,
    policy: TakeoffPolicy,
    override: RouteLengthOverride | None,
) -> RouteLengthQuantity:
    lengths: list[float] = []
    for start, end in zip(route.points, route.points[1:]):
        segment = math.dist(start, end) * factor
        if segment <= 0.0:
            raise QuantityTakeoffError(
                f"Route {route.route_id} contains an incomplete zero-length segment."
            )
        lengths.append(segment)
    geometric = _normalized(math.fsum(lengths))
    vertical = _normalized(policy.vertical_allowance_per_route_meters)
    termination = _normalized(policy.termination_allowance_per_end_meters * 2.0)
    subtotal = math.fsum((geometric, vertical, termination))
    waste = _normalized(subtotal * policy.waste_percent / 100.0)
    calculated = _normalized(subtotal + waste)
    reviewed = _normalized(override.final_total_meters) if override is not None else None
    return RouteLengthQuantity(
        route_id=route.route_id,
        source=route.source,
        source_unit=source_unit,
        output_unit=QuantityUnit.METER,
        geometric_length_meters=geometric,
        vertical_allowance_meters=vertical,
        termination_allowance_meters=termination,
        waste_meters=waste,
        calculated_total_meters=calculated,
        reviewed_override_meters=reviewed,
        final_total_meters=reviewed if reviewed is not None else calculated,
        calculation_rule="three_dimensional_polyline_plus_allowances_then_waste",
    )


def _canonical_sha256(value: object) -> str:
    def convert(item: object) -> object:
        if isinstance(item, Enum):
            return item.value
        if isinstance(item, JsonRecord):
            return item.to_dict()
        if isinstance(item, tuple):
            return [convert(child) for child in item]
        if isinstance(item, dict):
            return {str(key): convert(child) for key, child in item.items()}
        return item

    encoded = json.dumps(
        convert(value), ensure_ascii=False, sort_keys=True, separators=(",", ":")
    ).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest()


def _normalized(value: float) -> float:
    normalized = round(float(value), 12)
    return 0.0 if normalized == 0.0 else normalized


def _identifier(value: str, name: str) -> None:
    if not isinstance(value, str) or _IDENTIFIER.fullmatch(value) is None:
        raise QuantityTakeoffError(f"{name} must be a bounded lowercase identifier.")


def _text(value: str, name: str, maximum: int) -> None:
    if not isinstance(value, str) or not value or len(value.encode("utf-8")) > maximum:
        raise QuantityTakeoffError(f"{name} must be non-empty and at most {maximum} bytes.")
    if any(ord(character) < 32 and character != "\t" for character in value):
        raise QuantityTakeoffError(f"{name} contains unsupported control characters.")


def _bounded_number(
    value: float, name: str, *, minimum: float, maximum: float = 1_000_000_000.0
) -> None:
    if (
        not isinstance(value, (int, float))
        or isinstance(value, bool)
        or not math.isfinite(value)
        or not minimum <= float(value) <= maximum
    ):
        raise QuantityTakeoffError(f"{name} is outside its supported numeric range.")


def _typed_tuple(value: object, record_type: type, name: str) -> None:
    if not isinstance(value, tuple) or any(
        not isinstance(item, record_type) for item in value
    ):
        raise QuantityTakeoffError(f"Quantity {name} must be an immutable typed tuple.")
