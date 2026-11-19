"""Bounded AutoCAD 2021/2023 inspection and scratch-only drawing operations."""

from __future__ import annotations

import hashlib
import json
import logging
import math
import os
import re
import subprocess
import tempfile
import threading
import time
import uuid
from dataclasses import asdict, dataclass
from datetime import UTC, datetime
from pathlib import Path
from typing import BinaryIO, Callable

import psutil

from agent.exceptions import AutoCADIntegrationError, ConfirmationRequiredError
from core.file_manager import FileManager
from integrations.electrical_symbols import (
    CircleGeometry,
    ElectricalSymbol,
    ElectricalSymbolCatalog,
    LineGeometry,
    calculate_definition_sha256,
    catalog_block_name_is_valid,
    register_catalog_definition,
)
from integrations.drawing_handoff import (
    DrawingHandoffResult,
    handoff_verified_output,
    persist_drawing_output_evidence,
)
from integrations.autocad_context import AutoCADContextService, ContextImportResult
from integrations.panel_sld_autocad import (
    BOUNDS as _PANEL_SLD_DRAFT_BOUNDS,
    LAYERS as _PANEL_SLD_DRAFT_LAYERS,
    PROFILE as _PANEL_SLD_DRAFT_PROFILE,
    build_panel_sld_autocad_plan,
)
from integrations.db3_ga2_right_group_autocad import (
    EXPECTED_BLOCK_COUNT as _RIGHT_GROUP_BLOCK_COUNT,
    EXPECTED_ENTITY_COUNT as _RIGHT_GROUP_ENTITY_COUNT,
    EXPECTED_ROUTE_COUNT as _RIGHT_GROUP_ROUTE_COUNT,
    NOTE_LAYER as _RIGHT_GROUP_NOTE_LAYER,
    OVERLAY_LAYER as _RIGHT_GROUP_OVERLAY_LAYER,
    PROFILE as _RIGHT_GROUP_PROFILE,
    build_right_group_autocad_plan,
)


AUTOCAD_2021_PROG_ID = "AutoCAD.Application.24"
AUTOCAD_2023_PROG_ID = "AutoCAD.Application.24.2"
_SUPPORTED_SUFFIXES = frozenset({".dwg", ".dwt"})
_ELECTRICAL_WIRE_LAYER = "E-WIRE"
_ELECTRICAL_WIRE_COLOR = 3
_MAX_ROUTE_POINTS = 16
_MAX_SYMBOL_SCALE = 1_000.0
_MIN_CIRCUIT_REFERENCES = 2
_MAX_CIRCUIT_REFERENCES = 8
_MAXIMUM_PERFORMANCE_FIXTURE_OBJECTS = 1_792
_PERFORMANCE_FIXTURE_LAYER = "AIENG-PERF-FIXTURE"
_PERFORMANCE_FIXTURE_LAYER_COLOR = 8
_PERFORMANCE_FIXTURE_PROFILE = "AIENG_MAX_EXECUTION_V1"
_SYMBOL_CATALOG_PATH = (
    Path(__file__).resolve().parents[1]
    / "plugins"
    / "electrical_symbols"
    / "catalog.json"
)
_SAFE_ENVIRONMENT_KEYS = frozenset(
    {
        "APPDATA",
        "LOCALAPPDATA",
        "PATH",
        "PATHEXT",
        "PROGRAMDATA",
        "SYSTEMDRIVE",
        "SYSTEMROOT",
        "TEMP",
        "TMP",
        "USERPROFILE",
        "WINDIR",
    }
)
_INSPECTION_SCRIPT = """(progn
(princ (strcat "\\nAIENG_ACADVER=" (getvar "ACADVER")))
(princ (strcat "\\nAIENG_INSUNITS=" (itoa (getvar "INSUNITS"))))
(princ (strcat "\\nAIENG_MEASUREMENT=" (itoa (getvar "MEASUREMENT"))))
(princ (strcat "\\nAIENG_TILEMODE=" (itoa (getvar "TILEMODE"))))
(princ (strcat "\\nAIENG_DBMOD=" (itoa (getvar "DBMOD"))))
(princ "\\nAIENG_DONE=1\\n")
(princ)
)
"""
_INVENTORY_SCRIPT = """(progn
(setq aieng-layer-count 0)
(setq aieng-layer-record (tblnext "LAYER" T))
(while aieng-layer-record
  (setq aieng-layer-count (+ aieng-layer-count 1))
  (setq aieng-layer-record (tblnext "LAYER"))
)
(setq aieng-selection (ssget "_X"))
(setq aieng-entity-count
  (if aieng-selection (sslength aieng-selection) 0)
)
(princ (strcat "\\nAIENG_ACADVER=" (getvar "ACADVER")))
(princ (strcat "\\nAIENG_LAYERCOUNT=" (itoa aieng-layer-count)))
(princ (strcat "\\nAIENG_ENTITYCOUNT=" (itoa aieng-entity-count)))
(princ (strcat "\\nAIENG_DBMOD=" (itoa (getvar "DBMOD"))))
(princ "\\nAIENG_DONE=1\\n")
(setq aieng-selection nil)
(princ)
)
"""
InstallationLocator = Callable[[], "AutoCADInstallation | None"]


@dataclass(frozen=True, slots=True)
class AutoCADInstallation:
    """Validated internal supported AutoCAD installation paths."""

    product_name: str
    release: str
    language: str
    install_root: Path
    executable: Path
    core_console: Path | None
    activex_registered: bool


@dataclass(frozen=True, slots=True)
class AutoCADStatus:
    """Non-secret supported-method availability."""

    installed: bool
    product_name: str | None
    release: str | None
    language: str | None
    executable_available: bool
    core_console_available: bool
    activex_available: bool
    prog_id: str
    supported_methods: tuple[str, ...]

    def to_dict(self) -> dict[str, object]:
        value = asdict(self)
        value["supported_methods"] = list(self.supported_methods)
        return value


@dataclass(frozen=True, slots=True)
class ScratchInspection:
    """Structured read-only metadata from one isolated scratch drawing."""

    run_id: str
    source: Path
    scratch_drawing: Path
    acad_version: str
    insertion_units: int
    measurement: int
    tile_mode: int
    database_modified: int
    read_only: bool
    scratch_copy: bool
    source_unchanged: bool
    scratch_unchanged: bool
    duration_seconds: float
    stdout_truncated: bool
    stderr_truncated: bool

    def to_dict(self) -> dict[str, object]:
        value = asdict(self)
        value["source"] = str(self.source)
        value["scratch_drawing"] = str(self.scratch_drawing)
        return value


@dataclass(frozen=True, slots=True)
class DrawingInventory:
    """Bounded aggregate layer/entity counts from one isolated drawing copy."""

    run_id: str
    source: Path
    scratch_drawing: Path
    acad_version: str
    layer_count: int
    entity_count: int
    database_modified: int
    read_only: bool
    scratch_copy: bool
    source_unchanged: bool
    scratch_unchanged: bool
    duration_seconds: float
    stdout_truncated: bool
    stderr_truncated: bool

    def to_dict(self) -> dict[str, object]:
        value = asdict(self)
        value["source"] = str(self.source)
        value["scratch_drawing"] = str(self.scratch_drawing)
        return value


@dataclass(frozen=True, slots=True)
class CreatedLineArtifact:
    """Verified line created and saved only inside an isolated drawing copy."""

    run_id: str
    source: Path
    output_drawing: Path
    acad_version: str
    start: tuple[float, float, float]
    end: tuple[float, float, float]
    entity_count_before: int
    entity_count_after: int
    created_type: str
    database_modified: int
    source_unchanged: bool
    scratch_changed: bool
    duration_seconds: float
    stdout_truncated: bool
    stderr_truncated: bool

    def to_dict(self) -> dict[str, object]:
        value = asdict(self)
        value["source"] = str(self.source)
        value["output_drawing"] = str(self.output_drawing)
        value["start"] = list(self.start)
        value["end"] = list(self.end)
        return value


@dataclass(frozen=True, slots=True)
class WireRouteArtifact:
    """Verified multi-segment electrical route saved in an isolated copy."""

    run_id: str
    source: Path
    output_drawing: Path
    acad_version: str
    layer: str
    layer_color: int
    linetype: str
    points: tuple[tuple[float, float, float], ...]
    entity_count_before: int
    entity_count_after: int
    segment_count: int
    database_modified: int
    source_unchanged: bool
    scratch_changed: bool
    duration_seconds: float
    stdout_truncated: bool
    stderr_truncated: bool

    def to_dict(self) -> dict[str, object]:
        value = asdict(self)
        value["source"] = str(self.source)
        value["output_drawing"] = str(self.output_drawing)
        value["points"] = [list(point) for point in self.points]
        return value


@dataclass(frozen=True, slots=True)
class PlacedSymbolArtifact:
    """Verified electrical block reference saved in an isolated copy."""

    run_id: str
    source: Path
    output_drawing: Path
    acad_version: str
    symbol_id: str
    block_name: str
    symbol_version: int
    definition_sha256: str
    insertion_layer: str
    insertion_point: tuple[float, float, float]
    rotation_degrees: float
    scale: float
    block_reused: bool
    attribute_count: int
    entity_count_before: int
    entity_count_after: int
    database_modified: int
    source_unchanged: bool
    scratch_changed: bool
    duration_seconds: float
    stdout_truncated: bool
    stderr_truncated: bool

    def to_dict(self) -> dict[str, object]:
        value = asdict(self)
        value["source"] = str(self.source)
        value["output_drawing"] = str(self.output_drawing)
        value["insertion_point"] = list(self.insertion_point)
        return value


@dataclass(frozen=True, slots=True)
class CircuitPlacement:
    """One validated block-reference transform in a bounded circuit fragment."""

    point: tuple[float, float, float]
    rotation_degrees: float = 0.0
    scale: float = 1.0

    def to_dict(self) -> dict[str, object]:
        return {
            "point": list(self.point),
            "rotation_degrees": self.rotation_degrees,
            "scale": self.scale,
        }


@dataclass(frozen=True, slots=True)
class CircuitFragmentArtifact:
    """Verified symbol references and connected wires saved in one scratch copy."""

    run_id: str
    source: Path
    output_drawing: Path
    source_sha256: str
    output_sha256: str
    acad_version: str
    symbol_id: str
    block_name: str
    symbol_version: int
    definition_sha256: str
    insertion_layer: str
    wire_layer: str
    placements: tuple[CircuitPlacement, ...]
    wire_points: tuple[tuple[float, float, float], ...]
    block_reused: bool
    reference_count: int
    segment_count: int
    entity_count_before: int
    entity_count_after: int
    database_modified: int
    source_unchanged: bool
    scratch_changed: bool
    duration_seconds: float
    stdout_truncated: bool
    stderr_truncated: bool

    def to_dict(self) -> dict[str, object]:
        value = asdict(self)
        value["source"] = str(self.source)
        value["output_drawing"] = str(self.output_drawing)
        value["placements"] = [item.to_dict() for item in self.placements]
        value["wire_points"] = [list(point) for point in self.wire_points]
        return value


@dataclass(frozen=True, slots=True)
class PanelSldFixtureArtifact:
    """Verified fixed M21 marker fixture saved only in one isolated copy."""

    run_id: str
    source: Path
    output_drawing: Path
    source_sha256: str
    output_sha256: str
    acad_version: str
    marker_profile: str
    marker_block_count: int
    marker_reference_count: int
    recognized_load_reference_count: int
    attribute_count: int
    entity_count_before: int
    entity_count_after: int
    database_modified: int
    source_unchanged: bool
    scratch_changed: bool
    duration_seconds: float
    stdout_truncated: bool
    stderr_truncated: bool

    def to_dict(self) -> dict[str, object]:
        value = asdict(self)
        value["source"] = str(self.source)
        value["output_drawing"] = str(self.output_drawing)
        return value


@dataclass(frozen=True, slots=True)
class PanelSldDraftArtifact:
    """Verified reviewed DB3-GA2 SLD saved only in one isolated drawing copy."""

    run_id: str
    source: Path
    output_drawing: Path
    source_sha256: str
    output_sha256: str
    preview_sha256: str
    acad_version: str
    profile: str
    layer_names: tuple[str, ...]
    placement_bounds: tuple[float, float, float, float]
    created_entity_count: int
    entity_count_before: int
    entity_count_after: int
    region_entity_count_before: int
    database_modified: int
    source_unchanged: bool
    scratch_changed: bool
    duration_seconds: float
    stdout_truncated: bool
    stderr_truncated: bool

    def to_dict(self) -> dict[str, object]:
        value = asdict(self)
        value["source"] = str(self.source)
        value["output_drawing"] = str(self.output_drawing)
        value["layer_names"] = list(self.layer_names)
        value["placement_bounds"] = list(self.placement_bounds)
        return value


@dataclass(frozen=True, slots=True)
class RightGroupReviewArtifact:
    """Verified removable overlay for the reviewed DB3-GA2 right occurrence."""

    run_id: str
    source: Path
    output_drawing: Path
    source_sha256: str
    output_sha256: str
    preview_sha256: str
    acad_version: str
    profile: str
    overlay_layer: str
    note_layer: str
    route_count: int
    block_halo_count: int
    created_entity_count: int
    entity_count_before: int
    entity_count_after: int
    database_modified: int
    source_unchanged: bool
    scratch_changed: bool
    duration_seconds: float
    stdout_truncated: bool
    stderr_truncated: bool

    def to_dict(self) -> dict[str, object]:
        value = asdict(self)
        value["source"] = str(self.source)
        value["output_drawing"] = str(self.output_drawing)
        return value


@dataclass(frozen=True, slots=True)
class MaximumPerformanceFixtureArtifact:
    """Verified fixed maximum-size performance fixture and recovery evidence."""

    run_id: str
    source: Path
    output_drawing: Path
    source_sha256: str
    output_sha256: str
    acad_version: str
    fixture_profile: str
    object_count: int
    entity_count_before: int
    entity_count_after: int
    database_modified: int
    source_unchanged: bool
    scratch_changed: bool
    execution_duration_seconds: float
    recovery_duration_seconds: float
    total_duration_seconds: float
    recovery_evidence: Path
    recovery_evidence_sha256: str
    stdout_truncated: bool
    stderr_truncated: bool

    def to_dict(self) -> dict[str, object]:
        value = asdict(self)
        value["source"] = str(self.source)
        value["output_drawing"] = str(self.output_drawing)
        value["recovery_evidence"] = str(self.recovery_evidence)
        return value


@dataclass(frozen=True, slots=True)
class DraftingRecoveryEvidence:
    """Timestamped proof that execution changed only a verified scratch copy."""

    path: Path
    evidence_sha256: str
    recorded_at: str
    source: Path
    source_sha256: str
    output_drawing: Path
    output_sha256: str
    autocad_run_id: str

    def to_dict(self) -> dict[str, object]:
        value = asdict(self)
        value["path"] = str(self.path)
        value["source"] = str(self.source)
        value["output_drawing"] = str(self.output_drawing)
        return value


@dataclass(frozen=True, slots=True)
class SymbolSourceReview:
    """Read-only review of one user-authored block definition in a scratch copy."""

    run_id: str
    source: Path
    scratch_drawing: Path
    source_sha256: str
    acad_version: str
    drawing_units: int
    block_name: str
    block_found: bool
    block_flags: int
    base_point: tuple[float, float, float]
    entity_count: int
    line_count: int
    circle_count: int
    unsupported_count: int
    layer_zero_count: int
    attribute_definition_count: int
    nested_insert_count: int
    truncated: bool
    eligible: bool
    reasons: tuple[str, ...]
    proposed_definition: dict[str, object] | None
    source_unchanged: bool
    scratch_unchanged: bool
    database_modified: int
    duration_seconds: float
    stdout_truncated: bool
    stderr_truncated: bool

    def to_dict(self) -> dict[str, object]:
        value = asdict(self)
        value["source"] = str(self.source)
        value["scratch_drawing"] = str(self.scratch_drawing)
        value["base_point"] = list(self.base_point)
        value["reasons"] = list(self.reasons)
        return value


@dataclass(frozen=True, slots=True)
class _ScratchRun:
    run_id: str
    source: Path
    scratch: Path
    duration_seconds: float
    result: "_ProcessResult"
    source_unchanged: bool
    scratch_unchanged: bool
    source_sha256: str
    scratch_sha256: str


@dataclass(frozen=True, slots=True)
class _ParsedSymbolReview:
    acad_version: str
    drawing_units: int
    block_found: bool
    block_flags: int
    base_point: tuple[float, float, float]
    entity_count: int
    line_count: int
    circle_count: int
    unsupported_count: int
    layer_zero_count: int
    attribute_definition_count: int
    nested_insert_count: int
    truncated: bool
    database_modified: int
    geometry: tuple[dict[str, object], ...]


class AutoCADManager:
    """Use supported AutoCAD releases through fixed operations on isolated copies."""

    def __init__(
        self,
        files: FileManager,
        *,
        locator: InstallationLocator | None = None,
        timeout_seconds: float = 120.0,
        maximum_drawing_bytes: int = 268_435_456,
        maximum_output_bytes: int = 1_048_576,
        maximum_inventory_count: int = 10_000_000,
        symbol_catalog_path: Path | None = None,
        symbol_catalog_backup_dir: Path | None = None,
        drawing_handoff_backup_dir: Path | None = None,
        logger: logging.Logger | None = None,
    ) -> None:
        if (
            not isinstance(timeout_seconds, (int, float))
            or isinstance(timeout_seconds, bool)
            or not 10 <= timeout_seconds <= 300
        ):
            raise AutoCADIntegrationError("AutoCAD timeout must be from 10 through 300 seconds.")
        for name, value in (
            ("drawing bytes", maximum_drawing_bytes),
            ("output bytes", maximum_output_bytes),
            ("inventory count", maximum_inventory_count),
        ):
            if not isinstance(value, int) or isinstance(value, bool) or value <= 0:
                raise AutoCADIntegrationError(
                    f"AutoCAD maximum {name} must be a positive integer."
                )
        self._files = files
        self._locator = locator or _find_supported_autocad
        self.timeout_seconds = float(timeout_seconds)
        self.maximum_drawing_bytes = maximum_drawing_bytes
        self.maximum_output_bytes = maximum_output_bytes
        self.maximum_inventory_count = maximum_inventory_count
        self.symbol_catalog_path = symbol_catalog_path or _SYMBOL_CATALOG_PATH
        self.symbol_catalog_backup_dir = symbol_catalog_backup_dir or (
            self.symbol_catalog_path.parents[2] / "backup" / "symbol-catalog"
        )
        self.drawing_handoff_backup_dir = drawing_handoff_backup_dir or (
            self.symbol_catalog_path.parents[2] / "backup" / "autocad-handoff"
        )
        self._logger = logger or logging.getLogger("ai_engineer.integrations.autocad")

    def list_electrical_symbols(self) -> dict[str, object]:
        """Return bounded metadata for the currently approved symbol catalog."""
        catalog = ElectricalSymbolCatalog.load(self.symbol_catalog_path)
        result = catalog.to_public_dict()
        self._logger.info(
            "autocad_symbol_catalog_listed",
            extra={
                "catalog_version": catalog.catalog_version,
                "symbol_count": len(catalog.symbols),
            },
        )
        return result

    def detect(self) -> AutoCADStatus:
        """Return non-secret supported AutoCAD and integration-method availability."""
        installation = self._locator()
        if installation is None:
            return AutoCADStatus(
                False,
                None,
                None,
                None,
                False,
                False,
                False,
                AUTOCAD_2021_PROG_ID,
                (),
            )
        _validate_installation(installation)
        prog_id = _installation_prog_id(installation)
        methods = ["autolisp_secureload"]
        if installation.activex_registered:
            methods.append("activex_com")
        if installation.core_console is not None:
            methods.append("core_console_readonly")
        self._logger.info(
            "autocad_detected",
            extra={"release": installation.release, "method_count": len(methods)},
        )
        return AutoCADStatus(
            True,
            installation.product_name,
            installation.release,
            installation.language,
            True,
            installation.core_console is not None,
            installation.activex_registered,
            prog_id,
            tuple(methods),
        )

    def inspect_scratch(
        self, source: str | Path, *, confirmed: bool = False
    ) -> ScratchInspection:
        """Copy and inspect one workspace drawing with read-only Core Console."""
        run = self._execute_scratch(
            source,
            script_name="inspect.scr",
            script_content=_INSPECTION_SCRIPT,
            confirmed=confirmed,
            confirmation_message=(
                "AutoCAD scratch inspection requires explicit confirmation."
            ),
        )
        metadata = _parse_markers(run.result.stdout)
        self._logger.info(
            "autocad_scratch_inspected",
            extra={
                "run_id": run.run_id,
                "duration_seconds": round(run.duration_seconds, 6),
            },
        )
        return ScratchInspection(
            run_id=run.run_id,
            source=run.source,
            scratch_drawing=run.scratch,
            acad_version=metadata["ACADVER"],
            insertion_units=int(metadata["INSUNITS"]),
            measurement=int(metadata["MEASUREMENT"]),
            tile_mode=int(metadata["TILEMODE"]),
            database_modified=int(metadata["DBMOD"]),
            read_only=True,
            scratch_copy=True,
            source_unchanged=run.source_unchanged,
            scratch_unchanged=run.scratch_unchanged,
            duration_seconds=run.duration_seconds,
            stdout_truncated=run.result.stdout_truncated,
            stderr_truncated=run.result.stderr_truncated,
        )

    def inventory_scratch(
        self, source: str | Path, *, confirmed: bool = False
    ) -> DrawingInventory:
        """Count layers and database entities in one read-only scratch copy."""
        run = self._execute_scratch(
            source,
            script_name="inventory.scr",
            script_content=_INVENTORY_SCRIPT,
            confirmed=confirmed,
            confirmation_message=(
                "AutoCAD drawing inventory requires explicit confirmation."
            ),
        )
        metadata = _parse_inventory_markers(
            run.result.stdout,
            maximum_count=self.maximum_inventory_count,
        )
        self._logger.info(
            "autocad_scratch_inventoried",
            extra={
                "run_id": run.run_id,
                "duration_seconds": round(run.duration_seconds, 6),
            },
        )
        return DrawingInventory(
            run_id=run.run_id,
            source=run.source,
            scratch_drawing=run.scratch,
            acad_version=metadata["ACADVER"],
            layer_count=int(metadata["LAYERCOUNT"]),
            entity_count=int(metadata["ENTITYCOUNT"]),
            database_modified=int(metadata["DBMOD"]),
            read_only=True,
            scratch_copy=True,
            source_unchanged=run.source_unchanged,
            scratch_unchanged=run.scratch_unchanged,
            duration_seconds=run.duration_seconds,
            stdout_truncated=run.result.stdout_truncated,
            stderr_truncated=run.result.stderr_truncated,
        )

    def create_line_scratch(
        self,
        source: str | Path,
        *,
        start: tuple[float, float, float],
        end: tuple[float, float, float],
        confirmed: bool = False,
    ) -> CreatedLineArtifact:
        """Create and save one bounded line only in an isolated drawing copy."""
        start_point = _validate_point(start, "start")
        end_point = _validate_point(end, "end")
        if start_point == end_point:
            raise AutoCADIntegrationError("AutoCAD line endpoints must be different.")
        run = self._execute_scratch(
            source,
            script_name="create-line.scr",
            script_content=_create_line_script(start_point, end_point),
            confirmed=confirmed,
            confirmation_message=(
                "AutoCAD scratch line creation requires explicit confirmation."
            ),
            read_only=False,
            expect_scratch_change=True,
        )
        metadata = _parse_line_markers(
            run.result.stdout,
            maximum_count=self.maximum_inventory_count,
        )
        before = int(metadata["BEFORECOUNT"])
        after = int(metadata["AFTERCOUNT"])
        if after != before + 1:
            raise AutoCADIntegrationError(
                "AutoCAD scratch line did not increase the entity count by one."
            )
        self._record_output(run, "autocad.create_line_scratch")
        self._logger.info(
            "autocad_scratch_line_created",
            extra={
                "run_id": run.run_id,
                "duration_seconds": round(run.duration_seconds, 6),
            },
        )
        return CreatedLineArtifact(
            run_id=run.run_id,
            source=run.source,
            output_drawing=run.scratch,
            acad_version=metadata["ACADVER"],
            start=start_point,
            end=end_point,
            entity_count_before=before,
            entity_count_after=after,
            created_type=metadata["CREATEDTYPE"],
            database_modified=int(metadata["DBMOD"]),
            source_unchanged=run.source_unchanged,
            scratch_changed=not run.scratch_unchanged,
            duration_seconds=run.duration_seconds,
            stdout_truncated=run.result.stdout_truncated,
            stderr_truncated=run.result.stderr_truncated,
        )

    def create_wire_route_scratch(
        self,
        source: str | Path,
        *,
        points: tuple[tuple[float, float, float], ...],
        confirmed: bool = False,
    ) -> WireRouteArtifact:
        """Create a bounded E-WIRE route only in an isolated drawing copy."""
        route = _validate_route_points(points)
        run = self._execute_scratch(
            source,
            script_name="create-wire-route.scr",
            script_content=_create_wire_route_script(route),
            confirmed=confirmed,
            confirmation_message=(
                "AutoCAD scratch wire-route creation requires explicit confirmation."
            ),
            read_only=False,
            expect_scratch_change=True,
        )
        metadata = _parse_wire_route_markers(
            run.result.stdout,
            maximum_count=self.maximum_inventory_count,
        )
        before = int(metadata["BEFORECOUNT"])
        after = int(metadata["AFTERCOUNT"])
        segments = len(route) - 1
        if (
            int(metadata["SEGMENTCOUNT"]) != segments
            or int(metadata["VALIDCOUNT"]) != segments
            or after != before + segments
        ):
            raise AutoCADIntegrationError(
                "AutoCAD wire route did not create exactly the requested segments."
            )
        self._record_output(run, "autocad.create_wire_route_scratch")
        self._logger.info(
            "autocad_scratch_wire_route_created",
            extra={
                "run_id": run.run_id,
                "segment_count": segments,
                "duration_seconds": round(run.duration_seconds, 6),
            },
        )
        return WireRouteArtifact(
            run_id=run.run_id,
            source=run.source,
            output_drawing=run.scratch,
            acad_version=metadata["ACADVER"],
            layer=_ELECTRICAL_WIRE_LAYER,
            layer_color=_ELECTRICAL_WIRE_COLOR,
            linetype="CONTINUOUS",
            points=route,
            entity_count_before=before,
            entity_count_after=after,
            segment_count=segments,
            database_modified=int(metadata["DBMOD"]),
            source_unchanged=run.source_unchanged,
            scratch_changed=not run.scratch_unchanged,
            duration_seconds=run.duration_seconds,
            stdout_truncated=run.result.stdout_truncated,
            stderr_truncated=run.result.stderr_truncated,
        )

    def place_electrical_symbol_scratch(
        self,
        source: str | Path,
        *,
        symbol_id: str,
        definition_sha256: str,
        insertion_point: tuple[float, float, float],
        rotation_degrees: float = 0.0,
        scale: float = 1.0,
        confirmed: bool = False,
    ) -> PlacedSymbolArtifact:
        """Create and place one approved symbol only in an isolated drawing copy."""
        catalog = ElectricalSymbolCatalog.load(self.symbol_catalog_path)
        symbol = catalog.get(symbol_id)
        if definition_sha256 != symbol.definition_sha256:
            raise AutoCADIntegrationError(
                "Electrical symbol approval hash does not match the current catalog."
            )
        point = _validate_point(insertion_point, "insertion")
        rotation = _validate_rotation(rotation_degrees)
        normalized_scale = _validate_symbol_scale(scale)
        run = self._execute_scratch(
            source,
            script_name="place-electrical-symbol.scr",
            script_content=_create_symbol_script(
                symbol,
                insertion_point=point,
                rotation_degrees=rotation,
                scale=normalized_scale,
            ),
            confirmed=confirmed,
            confirmation_message=(
                "AutoCAD scratch electrical-symbol placement requires explicit confirmation."
            ),
            read_only=False,
            expect_scratch_change=True,
        )
        metadata = _parse_symbol_markers(
            run.result.stdout,
            symbol=symbol,
            maximum_count=self.maximum_inventory_count,
        )
        before = int(metadata["BEFORECOUNT"])
        after = int(metadata["AFTERCOUNT"])
        if after != before + 1:
            raise AutoCADIntegrationError(
                "AutoCAD symbol placement did not add exactly one block reference."
            )
        self._record_output(run, "autocad.place_electrical_symbol_scratch")
        self._logger.info(
            "autocad_scratch_symbol_placed",
            extra={
                "run_id": run.run_id,
                "symbol_id": symbol.symbol_id,
                "symbol_version": symbol.version,
                "duration_seconds": round(run.duration_seconds, 6),
            },
        )
        return PlacedSymbolArtifact(
            run_id=run.run_id,
            source=run.source,
            output_drawing=run.scratch,
            acad_version=metadata["ACADVER"],
            symbol_id=symbol.symbol_id,
            block_name=symbol.block_name,
            symbol_version=symbol.version,
            definition_sha256=symbol.definition_sha256,
            insertion_layer=symbol.insertion_layer,
            insertion_point=point,
            rotation_degrees=rotation,
            scale=normalized_scale,
            block_reused=metadata["BLOCKNEW"] == "0",
            attribute_count=int(metadata["ATTRIBUTECOUNT"]),
            entity_count_before=before,
            entity_count_after=after,
            database_modified=int(metadata["DBMOD"]),
            source_unchanged=run.source_unchanged,
            scratch_changed=not run.scratch_unchanged,
            duration_seconds=run.duration_seconds,
            stdout_truncated=run.result.stdout_truncated,
            stderr_truncated=run.result.stderr_truncated,
        )

    def create_electrical_circuit_fragment_scratch(
        self,
        source: str | Path,
        *,
        symbol_id: str,
        definition_sha256: str,
        placements: tuple[CircuitPlacement, ...],
        wire_points: tuple[tuple[float, float, float], ...],
        expected_source_sha256: str | None = None,
        confirmed: bool = False,
    ) -> CircuitFragmentArtifact:
        """Create a bounded, catalog-verified circuit only in one scratch copy."""
        catalog = ElectricalSymbolCatalog.load(self.symbol_catalog_path)
        symbol = catalog.get(symbol_id)
        if definition_sha256 != symbol.definition_sha256:
            raise AutoCADIntegrationError(
                "Electrical circuit approval hash does not match the current catalog."
            )
        validated_placements = _validate_circuit_placements(placements)
        route = _validate_route_points(wire_points)
        run = self._execute_scratch(
            source,
            script_name="create-electrical-circuit-fragment.scr",
            script_content=_create_circuit_fragment_script(
                symbol,
                placements=validated_placements,
                wire_points=route,
            ),
            confirmed=confirmed,
            confirmation_message=(
                "AutoCAD scratch circuit-fragment creation requires explicit confirmation."
            ),
            read_only=False,
            expect_scratch_change=True,
            expected_source_sha256=expected_source_sha256,
        )
        metadata = _parse_circuit_fragment_markers(
            run.result.stdout,
            symbol=symbol,
            maximum_count=self.maximum_inventory_count,
        )
        reference_count = len(validated_placements)
        segment_count = len(route) - 1
        before = int(metadata["BEFORECOUNT"])
        after = int(metadata["AFTERCOUNT"])
        if (
            int(metadata["REFERENCECOUNT"]) != reference_count
            or int(metadata["VALIDREFERENCECOUNT"]) != reference_count
            or int(metadata["SEGMENTCOUNT"]) != segment_count
            or int(metadata["VALIDSEGMENTCOUNT"]) != segment_count
            or after != before + reference_count + segment_count
        ):
            raise AutoCADIntegrationError(
                "AutoCAD circuit fragment did not create exactly the approved entities."
            )
        self._record_output(
            run, "autocad.create_electrical_circuit_fragment_scratch"
        )
        self._logger.info(
            "autocad_scratch_circuit_fragment_created",
            extra={
                "run_id": run.run_id,
                "symbol_id": symbol.symbol_id,
                "reference_count": reference_count,
                "segment_count": segment_count,
                "block_reused": metadata["BLOCKNEW"] == "0",
                "duration_seconds": round(run.duration_seconds, 6),
            },
        )
        return CircuitFragmentArtifact(
            run_id=run.run_id,
            source=run.source,
            output_drawing=run.scratch,
            source_sha256=run.source_sha256,
            output_sha256=run.scratch_sha256,
            acad_version=metadata["ACADVER"],
            symbol_id=symbol.symbol_id,
            block_name=symbol.block_name,
            symbol_version=symbol.version,
            definition_sha256=symbol.definition_sha256,
            insertion_layer=symbol.insertion_layer,
            wire_layer=_ELECTRICAL_WIRE_LAYER,
            placements=validated_placements,
            wire_points=route,
            block_reused=metadata["BLOCKNEW"] == "0",
            reference_count=reference_count,
            segment_count=segment_count,
            entity_count_before=before,
            entity_count_after=after,
            database_modified=int(metadata["DBMOD"]),
            source_unchanged=run.source_unchanged,
            scratch_changed=not run.scratch_unchanged,
            duration_seconds=run.duration_seconds,
            stdout_truncated=run.result.stdout_truncated,
            stderr_truncated=run.result.stderr_truncated,
        )

    def create_panel_sld_fixture_scratch(
        self,
        source: str | Path,
        *,
        expected_source_sha256: str,
        confirmed: bool = False,
    ) -> PanelSldFixtureArtifact:
        """Create the one fixed M21 acceptance fixture only in a scratch copy."""
        catalog = ElectricalSymbolCatalog.load(self.symbol_catalog_path)
        symbol = catalog.get("socket_single")
        run = self._execute_scratch(
            source,
            script_name="create-panel-sld-fixture.scr",
            script_content=_create_panel_sld_fixture_script(symbol),
            confirmed=confirmed,
            confirmation_message=(
                "AutoCAD scratch panel/SLD fixture creation requires explicit confirmation."
            ),
            read_only=False,
            expect_scratch_change=True,
            expected_source_sha256=expected_source_sha256,
            scratch_suffix=".dwg",
        )
        metadata = _parse_panel_sld_fixture_markers(
            run.result.stdout, maximum_count=self.maximum_inventory_count
        )
        before = int(metadata["BEFORECOUNT"])
        after = int(metadata["AFTERCOUNT"])
        if after != before + 11:
            raise AutoCADIntegrationError(
                "AutoCAD panel/SLD fixture did not create exactly 11 references."
            )
        self._record_output(run, "autocad.create_panel_sld_fixture_scratch")
        self._logger.info(
            "autocad_scratch_panel_sld_fixture_created",
            extra={"run_id": run.run_id, "duration_seconds": round(run.duration_seconds, 6)},
        )
        return PanelSldFixtureArtifact(
            run_id=run.run_id,
            source=run.source,
            output_drawing=run.scratch,
            source_sha256=run.source_sha256,
            output_sha256=run.scratch_sha256,
            acad_version=metadata["ACADVER"],
            marker_profile="AIENG_M21_FIXED_V6",
            marker_block_count=4,
            marker_reference_count=10,
            recognized_load_reference_count=1,
            attribute_count=58,
            entity_count_before=before,
            entity_count_after=after,
            database_modified=int(metadata["DBMOD"]),
            source_unchanged=run.source_unchanged,
            scratch_changed=not run.scratch_unchanged,
            duration_seconds=run.duration_seconds,
            stdout_truncated=run.result.stdout_truncated,
            stderr_truncated=run.result.stderr_truncated,
        )

    def create_panel_sld_draft_scratch(
        self,
        source: str | Path,
        *,
        preview_path: str | Path,
        expected_source_sha256: str,
        expected_preview_sha256: str,
        confirmed: bool = False,
    ) -> PanelSldDraftArtifact:
        """Insert the exact reviewed DB3-GA2 SLD only in a verified scratch copy."""
        resolved_preview = self._files.resolve_path(preview_path)
        if not resolved_preview.is_file():
            raise AutoCADIntegrationError("Reviewed SLD preview JSON does not exist.")
        try:
            preview = json.loads(self._files.read_text(resolved_preview))
            plan = build_panel_sld_autocad_plan(
                preview, expected_preview_sha256=expected_preview_sha256
            )
        except (OSError, TypeError, ValueError, json.JSONDecodeError) as exc:
            raise AutoCADIntegrationError(f"Reviewed SLD preview is invalid: {exc}") from exc
        if plan.source_drawing_sha256 != expected_source_sha256:
            raise AutoCADIntegrationError(
                "Reviewed SLD preview is bound to a different source drawing."
            )
        run = self._execute_scratch(
            source,
            script_name="create-db3-ga2-sld-draft.scr",
            script_content=plan.script,
            confirmed=confirmed,
            confirmation_message=(
                "AutoCAD DB3-GA2 SLD scratch insertion requires exact preview approval."
            ),
            read_only=False,
            expect_scratch_change=True,
            expected_source_sha256=expected_source_sha256,
            scratch_suffix=".dwg",
        )
        metadata = _parse_panel_sld_draft_markers(
            run.result.stdout,
            expected_preview_sha256=plan.preview_sha256,
            expected_source_sha256=plan.source_drawing_sha256,
            expected_count=plan.entity_count,
            maximum_count=self.maximum_inventory_count,
        )
        before = int(metadata["BEFORECOUNT"])
        after = int(metadata["AFTERCOUNT"])
        if after != before + plan.entity_count:
            raise AutoCADIntegrationError(
                "AutoCAD DB3-GA2 SLD did not create exactly the reviewed entities."
            )
        self._record_output(run, "autocad.create_panel_sld_draft_scratch")
        self._logger.info(
            "autocad_scratch_panel_sld_draft_created",
            extra={
                "run_id": run.run_id,
                "preview_sha256": plan.preview_sha256,
                "entity_count": plan.entity_count,
                "duration_seconds": round(run.duration_seconds, 6),
            },
        )
        return PanelSldDraftArtifact(
            run_id=run.run_id,
            source=run.source,
            output_drawing=run.scratch,
            source_sha256=run.source_sha256,
            output_sha256=run.scratch_sha256,
            preview_sha256=plan.preview_sha256,
            acad_version=metadata["ACADVER"],
            profile=_PANEL_SLD_DRAFT_PROFILE,
            layer_names=tuple(name for name, _ in _PANEL_SLD_DRAFT_LAYERS),
            placement_bounds=_PANEL_SLD_DRAFT_BOUNDS,
            created_entity_count=plan.entity_count,
            entity_count_before=before,
            entity_count_after=after,
            region_entity_count_before=int(metadata["REGIONBEFORE"]),
            database_modified=int(metadata["DBMOD"]),
            source_unchanged=run.source_unchanged,
            scratch_changed=not run.scratch_unchanged,
            duration_seconds=run.duration_seconds,
            stdout_truncated=run.result.stdout_truncated,
            stderr_truncated=run.result.stderr_truncated,
        )

    def create_db3_ga2_right_group_review_scratch(
        self,
        source: str | Path,
        *,
        preview_path: str | Path,
        expected_source_sha256: str,
        expected_preview_sha256: str,
        confirmed: bool = False,
    ) -> RightGroupReviewArtifact:
        """Create only the exact reviewed right-group overlay in a scratch copy."""
        resolved_preview = self._files.resolve_path(preview_path)
        if not resolved_preview.is_file():
            raise AutoCADIntegrationError("Reviewed right-group preview JSON does not exist.")
        try:
            preview = json.loads(self._files.read_text(resolved_preview))
            plan = build_right_group_autocad_plan(
                preview, expected_preview_sha256=expected_preview_sha256
            )
        except (OSError, TypeError, ValueError, json.JSONDecodeError) as exc:
            raise AutoCADIntegrationError(f"Reviewed right-group preview is invalid: {exc}") from exc
        if plan.source_drawing_sha256 != expected_source_sha256:
            raise AutoCADIntegrationError(
                "Reviewed right-group preview is bound to a different source drawing."
            )
        run = self._execute_scratch(
            source,
            script_name="create-db3-ga2-right-group-review.scr",
            script_content=plan.script,
            confirmed=confirmed,
            confirmation_message=(
                "AutoCAD DB3-GA2 right-group overlay requires exact preview approval."
            ),
            read_only=False,
            expect_scratch_change=True,
            expected_source_sha256=expected_source_sha256,
            scratch_suffix=".dwg",
        )
        metadata = _parse_right_group_review_markers(
            run.result.stdout,
            expected_preview_sha256=plan.preview_sha256,
            expected_source_sha256=plan.source_drawing_sha256,
            maximum_count=self.maximum_inventory_count,
        )
        before = int(metadata["BEFORECOUNT"])
        after = int(metadata["AFTERCOUNT"])
        self._record_output(run, "autocad.create_db3_ga2_right_group_review_scratch")
        return RightGroupReviewArtifact(
            run.run_id, run.source, run.scratch, run.source_sha256, run.scratch_sha256,
            plan.preview_sha256, metadata["ACADVER"], _RIGHT_GROUP_PROFILE,
            _RIGHT_GROUP_OVERLAY_LAYER, _RIGHT_GROUP_NOTE_LAYER,
            plan.route_count, plan.block_count, plan.entity_count, before, after,
            int(metadata["DBMOD"]), run.source_unchanged, not run.scratch_unchanged,
            run.duration_seconds, run.result.stdout_truncated, run.result.stderr_truncated,
        )

    def create_maximum_performance_fixture_scratch(
        self,
        source: str | Path,
        *,
        expected_source_sha256: str,
        confirmed: bool = False,
    ) -> MaximumPerformanceFixtureArtifact:
        """Create only the fixed 1,792-object release fixture in a scratch copy."""
        total_started = time.monotonic()
        run = self._execute_scratch(
            source,
            script_name="create-maximum-performance-fixture.scr",
            script_content=_create_maximum_performance_fixture_script(),
            confirmed=confirmed,
            confirmation_message=(
                "AutoCAD maximum performance fixture requires separate explicit "
                "confirmation for one hash-bound scratch execution."
            ),
            read_only=False,
            expect_scratch_change=True,
            expected_source_sha256=expected_source_sha256,
            scratch_suffix=".dwg",
        )
        metadata = _parse_maximum_performance_fixture_markers(
            run.result.stdout, maximum_count=self.maximum_inventory_count
        )
        before = int(metadata["BEFORECOUNT"])
        after = int(metadata["AFTERCOUNT"])
        if after != before + _MAXIMUM_PERFORMANCE_FIXTURE_OBJECTS:
            raise AutoCADIntegrationError(
                "AutoCAD maximum performance fixture did not create exactly 1,792 objects."
            )
        recovery_started = time.monotonic()
        recovery_payload = {
            "schema_version": 1,
            "fixture_profile": _PERFORMANCE_FIXTURE_PROFILE,
            "autocad_run_id": run.run_id,
            "recorded_at": datetime.now(UTC).isoformat().replace("+00:00", "Z"),
            "source": str(run.source),
            "source_sha256": run.source_sha256,
            "source_unchanged": True,
            "output_drawing": str(run.scratch),
            "output_sha256": run.scratch_sha256,
            "scratch_changed": True,
            "object_count": _MAXIMUM_PERFORMANCE_FIXTURE_OBJECTS,
            "database_modified": int(metadata["DBMOD"]),
            "recovery_strategy": "retain_unchanged_source_and_verified_scratch_output",
            "handoff_allowed": False,
            "provider_egress_performed": False,
            "drawing_mutation_performed": True,
        }
        encoded = (
            json.dumps(
                recovery_payload,
                ensure_ascii=True,
                separators=(",", ":"),
                sort_keys=True,
            )
            + "\n"
        )
        recovery_path = run.scratch.parent / "performance-recovery.json"
        self._files.write_text(recovery_path, encoded)
        recovery_sha256 = hashlib.sha256(encoded.encode("utf-8")).hexdigest()
        if (
            _bounded_sha256(recovery_path, self.maximum_drawing_bytes)
            != recovery_sha256
        ):
            raise AutoCADIntegrationError(
                "Maximum performance recovery evidence failed post-write verification."
            )
        recovery_duration = time.monotonic() - recovery_started
        total_duration = time.monotonic() - total_started
        self._logger.info(
            "autocad_maximum_performance_fixture_created",
            extra={
                "run_id": run.run_id,
                "object_count": _MAXIMUM_PERFORMANCE_FIXTURE_OBJECTS,
                "execution_duration_seconds": round(run.duration_seconds, 6),
                "total_duration_seconds": round(total_duration, 6),
                "handoff_allowed": False,
            },
        )
        return MaximumPerformanceFixtureArtifact(
            run_id=run.run_id,
            source=run.source,
            output_drawing=run.scratch,
            source_sha256=run.source_sha256,
            output_sha256=run.scratch_sha256,
            acad_version=metadata["ACADVER"],
            fixture_profile=_PERFORMANCE_FIXTURE_PROFILE,
            object_count=_MAXIMUM_PERFORMANCE_FIXTURE_OBJECTS,
            entity_count_before=before,
            entity_count_after=after,
            database_modified=int(metadata["DBMOD"]),
            source_unchanged=run.source_unchanged,
            scratch_changed=not run.scratch_unchanged,
            execution_duration_seconds=run.duration_seconds,
            recovery_duration_seconds=recovery_duration,
            total_duration_seconds=total_duration,
            recovery_evidence=recovery_path,
            recovery_evidence_sha256=recovery_sha256,
            stdout_truncated=run.result.stdout_truncated,
            stderr_truncated=run.result.stderr_truncated,
        )

    def verify_drawing_source(
        self, source: str | Path, *, expected_sha256: str
    ) -> Path:
        """Resolve one workspace drawing and verify its exact bounded identity."""
        _validate_sha256(expected_sha256, "expected source SHA-256")
        source_path = self._files.resolve_path(source)
        if (
            not source_path.is_file()
            or source_path.suffix.casefold() not in _SUPPORTED_SUFFIXES
        ):
            raise AutoCADIntegrationError(
                "Drafting execution source must be an existing workspace DWG or DWT."
            )
        if _bounded_sha256(source_path, self.maximum_drawing_bytes) != expected_sha256:
            raise AutoCADIntegrationError(
                "Drafting execution source differs from the reviewed SHA-256."
            )
        return source_path

    def record_drafting_recovery_evidence(
        self,
        artifact: CircuitFragmentArtifact,
        *,
        drafting_run_id: str,
        execution_id: str,
        execution_request_sha256: str,
        preview_sha256: str,
        clock: Callable[[], datetime] | None = None,
    ) -> DraftingRecoveryEvidence:
        """Persist exact source/output identities for scratch recovery and handoff."""
        for value, name, length in (
            (drafting_run_id, "drafting run ID", 32),
            (execution_id, "drafting execution ID", 32),
            (execution_request_sha256, "execution request SHA-256", 64),
            (preview_sha256, "preview SHA-256", 64),
        ):
            if not isinstance(value, str) or not re.fullmatch(
                rf"[0-9a-f]{{{length}}}", value
            ):
                raise AutoCADIntegrationError(f"{name} is invalid.")
        if not isinstance(artifact, CircuitFragmentArtifact):
            raise AutoCADIntegrationError(
                "Drafting recovery requires a verified circuit artifact."
            )
        if not artifact.source_unchanged or not artifact.scratch_changed:
            raise AutoCADIntegrationError(
                "Drafting recovery cannot trust the AutoCAD hash state."
            )
        source = self.verify_drawing_source(
            artifact.source, expected_sha256=artifact.source_sha256
        )
        if (
            _bounded_sha256(artifact.output_drawing, self.maximum_drawing_bytes)
            != artifact.output_sha256
        ):
            raise AutoCADIntegrationError(
                "Drafting output changed before recovery evidence was recorded."
            )
        now = (clock or (lambda: datetime.now(UTC)))()
        if not isinstance(now, datetime) or now.tzinfo is None:
            raise AutoCADIntegrationError(
                "Drafting recovery clock must be timezone-aware."
            )
        recorded_at = now.astimezone(UTC).isoformat().replace("+00:00", "Z")
        payload = {
            "schema_version": 1,
            "drafting_run_id": drafting_run_id,
            "execution_id": execution_id,
            "execution_request_sha256": execution_request_sha256,
            "preview_sha256": preview_sha256,
            "recorded_at": recorded_at,
            "autocad_run_id": artifact.run_id,
            "source": str(source),
            "source_sha256": artifact.source_sha256,
            "source_unchanged": True,
            "output_drawing": str(artifact.output_drawing),
            "output_sha256": artifact.output_sha256,
            "scratch_changed": True,
            "recovery_strategy": "retain_unchanged_source_and_verified_scratch_output",
            "handoff_requires_separate_approval": True,
        }
        encoded = (
            json.dumps(payload, ensure_ascii=True, separators=(",", ":"), sort_keys=True)
            + "\n"
        )
        evidence_path = artifact.output_drawing.parent / "drafting-recovery.json"
        self._files.write_text(evidence_path, encoded)
        evidence_sha256 = hashlib.sha256(encoded.encode("utf-8")).hexdigest()
        if _bounded_sha256(evidence_path, self.maximum_drawing_bytes) != evidence_sha256:
            raise AutoCADIntegrationError(
                "Drafting recovery evidence failed post-write verification."
            )
        return DraftingRecoveryEvidence(
            evidence_path,
            evidence_sha256,
            recorded_at,
            source,
            artifact.source_sha256,
            artifact.output_drawing,
            artifact.output_sha256,
            artifact.run_id,
        )

    def review_electrical_symbol_source(
        self,
        source: str | Path,
        *,
        block_name: str,
        symbol_id: str,
        symbol_version: int,
        insertion_layer: str,
        layer_color: int,
        confirmed: bool = False,
    ) -> SymbolSourceReview:
        """Review one named block without changing either source or scratch bytes."""
        request = _validate_symbol_review_request(
            block_name=block_name,
            symbol_id=symbol_id,
            symbol_version=symbol_version,
            insertion_layer=insertion_layer,
            layer_color=layer_color,
        )
        run = self._execute_scratch(
            source,
            script_name="review-electrical-symbol.scr",
            script_content=_create_symbol_review_script(request["block_name"]),
            confirmed=confirmed,
            confirmation_message=(
                "AutoCAD electrical-symbol source review requires explicit confirmation."
            ),
        )
        parsed = _parse_symbol_review_markers(
            run.result.stdout,
            expected_block_name=request["block_name"],
        )
        reasons = _symbol_review_reasons(parsed, request["block_name"])
        proposed: dict[str, object] | None = None
        if not reasons:
            proposed = {
                "id": request["symbol_id"],
                "block_name": request["block_name"],
                "version": request["symbol_version"],
                "description": (
                    f"Reviewed user-authored electrical symbol {request['symbol_id']}."
                ),
                "units": "millimeters",
                "insertion_layer": request["insertion_layer"],
                "layer_color": request["layer_color"],
                "linetype": "CONTINUOUS",
                "base_point": list(parsed.base_point),
                "attributes": [],
                "entities": list(parsed.geometry),
            }
            proposed["definition_sha256"] = calculate_definition_sha256(proposed)
        self._logger.info(
            "autocad_symbol_source_reviewed",
            extra={
                "run_id": run.run_id,
                "block_found": parsed.block_found,
                "eligible": not reasons,
                "entity_count": parsed.entity_count,
                "duration_seconds": round(run.duration_seconds, 6),
            },
        )
        artifact = SymbolSourceReview(
            run_id=run.run_id,
            source=run.source,
            scratch_drawing=run.scratch,
            source_sha256=_bounded_sha256(run.source, self.maximum_drawing_bytes),
            acad_version=parsed.acad_version,
            drawing_units=parsed.drawing_units,
            block_name=request["block_name"],
            block_found=parsed.block_found,
            block_flags=parsed.block_flags,
            base_point=parsed.base_point,
            entity_count=parsed.entity_count,
            line_count=parsed.line_count,
            circle_count=parsed.circle_count,
            unsupported_count=parsed.unsupported_count,
            layer_zero_count=parsed.layer_zero_count,
            attribute_definition_count=parsed.attribute_definition_count,
            nested_insert_count=parsed.nested_insert_count,
            truncated=parsed.truncated,
            eligible=not reasons,
            reasons=reasons,
            proposed_definition=proposed,
            source_unchanged=run.source_unchanged,
            scratch_unchanged=run.scratch_unchanged,
            database_modified=parsed.database_modified,
            duration_seconds=run.duration_seconds,
            stdout_truncated=run.result.stdout_truncated,
            stderr_truncated=run.result.stderr_truncated,
        )
        evidence = {
            "schema_version": 1,
            "review_run_id": artifact.run_id,
            "source": str(artifact.source),
            "source_sha256": artifact.source_sha256,
            "eligible": artifact.eligible,
            "proposed_definition": artifact.proposed_definition,
        }
        self._files.write_text(
            run.scratch.parent / "symbol-review.json",
            json.dumps(evidence, ensure_ascii=False, indent=2) + "\n",
        )
        return artifact

    def register_electrical_symbol_review(
        self,
        *,
        review_run_id: str,
        proposal_sha256: str,
        source_sha256: str,
        confirmed: bool = False,
    ) -> dict[str, object]:
        """Register one exact eligible review proposal with backup and rollback."""
        if re.fullmatch(r"[0-9a-f]{32}", review_run_id) is None:
            raise AutoCADIntegrationError("Electrical symbol review run ID is invalid.")
        for label, value in (
            ("proposal", proposal_sha256),
            ("source", source_sha256),
        ):
            if re.fullmatch(r"[0-9a-f]{64}", value) is None:
                raise AutoCADIntegrationError(
                    f"Electrical symbol {label} SHA-256 is invalid."
                )
        evidence_paths = [
            root
            / ".ai-engineer"
            / "autocad"
            / "runs"
            / review_run_id
            / "symbol-review.json"
            for root in self._files.workspace_roots
        ]
        existing = [path for path in evidence_paths if path.is_file()]
        if len(existing) != 1:
            raise AutoCADIntegrationError(
                "Electrical symbol review evidence was not found uniquely."
            )
        try:
            evidence = json.loads(self._files.read_text(existing[0]))
        except (UnicodeError, json.JSONDecodeError) as exc:
            raise AutoCADIntegrationError(
                "Electrical symbol review evidence is invalid JSON."
            ) from exc
        expected_keys = {
            "schema_version",
            "review_run_id",
            "source",
            "source_sha256",
            "eligible",
            "proposed_definition",
        }
        if (
            not isinstance(evidence, dict)
            or set(evidence) != expected_keys
            or evidence["schema_version"] != 1
            or evidence["review_run_id"] != review_run_id
            or evidence["source_sha256"] != source_sha256
            or evidence["eligible"] is not True
            or not isinstance(evidence["proposed_definition"], dict)
        ):
            raise AutoCADIntegrationError(
                "Electrical symbol review evidence is incomplete or does not match."
            )
        proposal = evidence["proposed_definition"]
        if proposal.get("definition_sha256") != proposal_sha256:
            raise AutoCADIntegrationError(
                "Electrical symbol proposal hash does not match review evidence."
            )
        source = evidence["source"]
        if not isinstance(source, str):
            raise AutoCADIntegrationError("Electrical symbol reviewed source is invalid.")
        source_path = self._files.resolve_path(source)
        if _bounded_sha256(source_path, self.maximum_drawing_bytes) != source_sha256:
            raise AutoCADIntegrationError(
                "Electrical symbol reviewed source changed after review."
            )
        result = register_catalog_definition(
            self.symbol_catalog_path,
            self.symbol_catalog_backup_dir,
            proposal,
            confirmed=confirmed,
        )
        self._logger.info(
            "autocad_symbol_catalog_registered",
            extra={
                "review_run_id": review_run_id,
                "symbol_id": result.symbol.symbol_id,
                "catalog_version": result.catalog_version,
            },
        )
        value = result.to_dict()
        value.update(
            {
                "review_run_id": review_run_id,
                "source_sha256": source_sha256,
                "proposal_sha256": proposal_sha256,
            }
        )
        return value

    def handoff_verified_output_new(
        self,
        *,
        run_id: str,
        output_sha256: str,
        destination: str | Path,
        confirmed: bool = False,
    ) -> DrawingHandoffResult:
        """Create a new workspace drawing from one exact producer output."""
        return handoff_verified_output(
            self._files,
            self.drawing_handoff_backup_dir,
            run_id=run_id,
            output_sha256=output_sha256,
            destination=destination,
            replace_existing=False,
            confirmed=confirmed,
            maximum_bytes=self.maximum_drawing_bytes,
            logger=self._logger,
        )

    def import_drawing_context(
        self,
        *,
        context_id: str,
        evidence_sha256: str,
        confirmed: bool = False,
    ) -> ContextImportResult:
        """Import one exact local read-only context snapshot without provider egress."""
        result = AutoCADContextService(
            self._files,
            self.symbol_catalog_path,
        ).import_context(
            context_id=context_id,
            evidence_sha256=evidence_sha256,
            confirmed=confirmed,
        )
        self._logger.info(
            "autocad_context_imported",
            extra={
                "context_id": context_id,
                "source_mode": result.context.source_mode,
                "entity_count": len(result.context.entities),
                "unknown_count": len(result.model.unknown_objects),
                "provider_egress_performed": False,
            },
        )
        return result

    def replace_with_verified_output(
        self,
        *,
        run_id: str,
        output_sha256: str,
        destination: str | Path,
        expected_destination_sha256: str,
        confirmed: bool = False,
    ) -> DrawingHandoffResult:
        """Replace one workspace drawing with backup and concurrent-change checks."""
        return handoff_verified_output(
            self._files,
            self.drawing_handoff_backup_dir,
            run_id=run_id,
            output_sha256=output_sha256,
            destination=destination,
            replace_existing=True,
            expected_destination_sha256=expected_destination_sha256,
            confirmed=confirmed,
            maximum_bytes=self.maximum_drawing_bytes,
            logger=self._logger,
        )

    def _record_output(self, run: _ScratchRun, operation: str) -> None:
        persist_drawing_output_evidence(
            self._files,
            run_id=run.run_id,
            operation=operation,
            source=run.source,
            source_sha256=run.source_sha256,
            output=run.scratch,
            output_sha256=run.scratch_sha256,
            maximum_bytes=self.maximum_drawing_bytes,
        )

    def _execute_scratch(
        self,
        source: str | Path,
        *,
        script_name: str,
        script_content: str,
        confirmed: bool,
        confirmation_message: str,
        read_only: bool = True,
        expect_scratch_change: bool = False,
        expected_source_sha256: str | None = None,
        scratch_suffix: str | None = None,
    ) -> _ScratchRun:
        if not confirmed:
            raise ConfirmationRequiredError(confirmation_message)
        installation = self._locator()
        if installation is None:
            raise AutoCADIntegrationError("Supported AutoCAD 2021/2023 is not installed.")
        _validate_installation(installation)
        if installation.core_console is None:
            raise AutoCADIntegrationError("Supported AutoCAD Core Console is not available.")
        source_path = self._files.resolve_path(source)
        if not source_path.is_file():
            raise AutoCADIntegrationError("AutoCAD source must be an existing regular file.")
        if source_path.suffix.casefold() not in _SUPPORTED_SUFFIXES:
            raise AutoCADIntegrationError("AutoCAD source must use .dwg or .dwt.")
        source_size = source_path.stat().st_size
        if source_size <= 0 or source_size > self.maximum_drawing_bytes:
            raise AutoCADIntegrationError("AutoCAD source is empty or exceeds its byte limit.")
        if expected_source_sha256 is not None:
            _validate_sha256(expected_source_sha256, "expected source SHA-256")
            if _bounded_sha256(source_path, self.maximum_drawing_bytes) != expected_source_sha256:
                raise AutoCADIntegrationError(
                    "AutoCAD source differs from the reviewed SHA-256."
                )
        run_id = uuid.uuid4().hex
        workspace_root = _containing_root(source_path, self._files.workspace_roots)
        run_directory = self._files.resolve_path(
            workspace_root / ".ai-engineer" / "autocad" / "runs" / run_id
        )
        run_directory.mkdir(parents=True, exist_ok=False)
        if self._files.resolve_path(run_directory) != run_directory.resolve(strict=True):
            raise AutoCADIntegrationError(
                "AutoCAD scratch directory failed containment validation."
            )
        output_suffix = (
            source_path.suffix.casefold() if scratch_suffix is None else scratch_suffix
        )
        if output_suffix not in _SUPPORTED_SUFFIXES:
            raise AutoCADIntegrationError("AutoCAD scratch output suffix is invalid.")
        scratch = run_directory / f"source{output_suffix}"
        script = run_directory / script_name
        copied_digest = _atomic_bounded_copy(
            source_path,
            scratch,
            self.maximum_drawing_bytes,
        )
        if expected_source_sha256 is not None and copied_digest != expected_source_sha256:
            raise AutoCADIntegrationError(
                "AutoCAD source changed while the scratch copy was created."
            )
        self._files.write_text(script, script_content)
        command_parts = [
            str(installation.core_console),
            "/i",
            str(scratch),
            "/s",
            str(script),
            "/l",
            "en-US",
        ]
        if read_only:
            command_parts.append("/readonly")
        command = tuple(command_parts)
        started = time.monotonic()
        result = self._run_core_console(command, cwd=run_directory)
        duration = time.monotonic() - started
        self._files.write_text(run_directory / "console.stdout.txt", result.stdout)
        self._files.write_text(run_directory / "console.stderr.txt", result.stderr)
        if result.return_code != 0:
            raise AutoCADIntegrationError(
                f"AutoCAD Core Console returned exit code {result.return_code}. "
                f"Scratch artifacts were retained under run {run_id}."
            )
        source_digest = _bounded_sha256(source_path, self.maximum_drawing_bytes)
        scratch_digest = _bounded_sha256(scratch, self.maximum_drawing_bytes)
        source_unchanged = source_digest == copied_digest
        scratch_unchanged = scratch_digest == copied_digest
        scratch_state_is_valid = (
            not scratch_unchanged if expect_scratch_change else scratch_unchanged
        )
        if not source_unchanged:
            raise AutoCADIntegrationError(
                "AutoCAD source or scratch drawing hash state is invalid. "
                f"Scratch artifacts were retained under run {run_id}."
            )
        if not scratch_state_is_valid:
            rejected = _scratch_validation_failures(result.stdout)
            if expect_scratch_change and scratch_unchanged and rejected:
                raise AutoCADIntegrationError(
                    "AutoCAD scratch drawing did not change because fixed validation "
                    f"rejected: {', '.join(rejected)}. Scratch artifacts were retained "
                    f"under run {run_id}."
                )
            raise AutoCADIntegrationError(
                "AutoCAD source or scratch drawing hash state is invalid. "
                f"Scratch artifacts were retained under run {run_id}."
            )
        return _ScratchRun(
            run_id,
            source_path,
            scratch,
            duration,
            result,
            source_unchanged,
            scratch_unchanged,
            source_digest,
            scratch_digest,
        )

    def _run_core_console(
        self, command: tuple[str, ...], *, cwd: Path
    ) -> "_ProcessResult":
        environment = {
            key: value
            for key, value in os.environ.items()
            if key.upper() in _SAFE_ENVIRONMENT_KEYS
        }
        creation_flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
        try:
            process = subprocess.Popen(
                command,
                cwd=cwd,
                env=environment,
                stdin=subprocess.DEVNULL,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                shell=False,
                creationflags=creation_flags,
                start_new_session=os.name != "nt",
            )
        except OSError as exc:
            raise AutoCADIntegrationError(
                f"Cannot start AutoCAD Core Console: {type(exc).__name__}."
            ) from exc
        stdout = _BoundedBinaryCapture(self.maximum_output_bytes)
        stderr = _BoundedBinaryCapture(self.maximum_output_bytes)
        stdout_thread = threading.Thread(target=stdout.drain, args=(process.stdout,), daemon=True)
        stderr_thread = threading.Thread(target=stderr.drain, args=(process.stderr,), daemon=True)
        stdout_thread.start()
        stderr_thread.start()
        try:
            return_code = process.wait(timeout=self.timeout_seconds)
        except subprocess.TimeoutExpired as exc:
            _terminate_process_tree(process.pid)
            raise AutoCADIntegrationError("AutoCAD Core Console timed out.") from exc
        finally:
            stdout_thread.join(timeout=10)
            stderr_thread.join(timeout=10)
        return _ProcessResult(
            return_code,
            stdout.text,
            stderr.text,
            stdout.truncated,
            stderr.truncated,
        )


@dataclass(frozen=True, slots=True)
class _ProcessResult:
    return_code: int
    stdout: str
    stderr: str
    stdout_truncated: bool
    stderr_truncated: bool


class _BoundedBinaryCapture:
    def __init__(self, maximum_bytes: int) -> None:
        self.maximum_bytes = maximum_bytes
        self.data = bytearray()
        self.truncated = False

    def drain(self, stream: BinaryIO | None) -> None:
        if stream is None:
            return
        while True:
            chunk = stream.read(8192)
            if not chunk:
                return
            remaining = self.maximum_bytes - len(self.data)
            if remaining > 0:
                self.data.extend(chunk[:remaining])
            if len(chunk) > remaining:
                self.truncated = True

    @property
    def text(self) -> str:
        raw = bytes(self.data)
        if raw and raw.count(b"\x00") > len(raw) // 4:
            decoded = raw.decode("utf-16-le", errors="replace")
        else:
            decoded = raw.decode("utf-8", errors="replace")
        encoded = decoded.encode("utf-8")
        if len(encoded) > self.maximum_bytes:
            self.truncated = True
            return encoded[: self.maximum_bytes].decode("utf-8", errors="ignore")
        return decoded


def _find_supported_autocad() -> AutoCADInstallation | None:
    if os.name != "nt":
        return None
    try:
        import winreg
    except ImportError:
        return None
    releases = (
        (r"SOFTWARE\Autodesk\AutoCAD\R24.2", "2023", AUTOCAD_2023_PROG_ID),
        (r"SOFTWARE\Autodesk\AutoCAD\R24.0", "2021", AUTOCAD_2021_PROG_ID),
    )
    for base_path, product_year, prog_id in releases:
        try:
            with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, base_path) as base:
                count = winreg.QueryInfoKey(base)[0]
                subkeys = [winreg.EnumKey(base, index) for index in range(count)]
        except OSError:
            continue
        for subkey in sorted(subkeys):
            try:
                with winreg.OpenKey(
                    winreg.HKEY_LOCAL_MACHINE, f"{base_path}\\{subkey}"
                ) as product:
                    location = Path(winreg.QueryValueEx(product, "Location")[0])
                    product_name = str(winreg.QueryValueEx(product, "ProductNameGlob")[0])
                    release = str(winreg.QueryValueEx(product, "Release")[0])
                    language = str(winreg.QueryValueEx(product, "Language")[0])
            except OSError:
                continue
            executable = location / "acad.exe"
            core_console = location / "accoreconsole.exe"
            if product_year not in product_name or not executable.is_file():
                continue
            return AutoCADInstallation(
                product_name=product_name,
                release=release,
                language=language,
                install_root=location,
                executable=executable,
                core_console=core_console if core_console.is_file() else None,
                activex_registered=_activex_is_registered(winreg, prog_id),
            )
    return None


def _activex_is_registered(
    winreg_module: object, prog_id: str = AUTOCAD_2021_PROG_ID
) -> bool:
    try:
        open_key = getattr(winreg_module, "OpenKey")
        root = getattr(winreg_module, "HKEY_CLASSES_ROOT")
        with open_key(root, rf"{prog_id}\CLSID") as key:
            value = getattr(winreg_module, "QueryValue")(key, None)
    except (AttributeError, OSError):
        return False
    return isinstance(value, str) and bool(value.strip())


def _validate_installation(installation: AutoCADInstallation) -> None:
    if not isinstance(installation, AutoCADInstallation):
        raise AutoCADIntegrationError("AutoCAD locator returned invalid installation data.")
    root = installation.install_root.resolve(strict=True)
    executable = installation.executable.resolve(strict=True)
    try:
        executable.relative_to(root)
    except ValueError as exc:
        raise AutoCADIntegrationError("AutoCAD executable escapes its installation root.") from exc
    if executable.name.casefold() != "acad.exe":
        raise AutoCADIntegrationError("AutoCAD executable name is invalid.")
    if installation.core_console is not None:
        core = installation.core_console.resolve(strict=True)
        try:
            core.relative_to(root)
        except ValueError as exc:
            raise AutoCADIntegrationError("Core Console escapes the installation root.") from exc
        if core.name.casefold() != "accoreconsole.exe":
            raise AutoCADIntegrationError("AutoCAD Core Console name is invalid.")
    _installation_prog_id(installation)


def _installation_prog_id(installation: AutoCADInstallation) -> str:
    if installation.release.startswith("24.2") and "2023" in installation.product_name:
        return AUTOCAD_2023_PROG_ID
    if installation.release.startswith("24.0") and "2021" in installation.product_name:
        return AUTOCAD_2021_PROG_ID
    raise AutoCADIntegrationError(
        "Detected Autodesk product is not supported AutoCAD 2021/2023."
    )


def _containing_root(path: Path, roots: tuple[Path, ...]) -> Path:
    for root in roots:
        try:
            path.relative_to(root)
            return root
        except ValueError:
            continue
    raise AutoCADIntegrationError("AutoCAD source has no configured workspace root.")


def _atomic_bounded_copy(source: Path, target: Path, maximum_bytes: int) -> str:
    descriptor, temporary_name = tempfile.mkstemp(
        dir=target.parent, prefix=f".{target.name}.", suffix=".tmp"
    )
    temporary = Path(temporary_name)
    written = 0
    digest = hashlib.sha256()
    try:
        with source.open("rb") as reader, os.fdopen(descriptor, "wb") as writer:
            while True:
                chunk = reader.read(1_048_576)
                if not chunk:
                    break
                written += len(chunk)
                if written > maximum_bytes:
                    raise AutoCADIntegrationError("AutoCAD source grew beyond its byte limit.")
                writer.write(chunk)
                digest.update(chunk)
            writer.flush()
            os.fsync(writer.fileno())
        if written == 0:
            raise AutoCADIntegrationError("AutoCAD source copy is empty.")
        os.replace(temporary, target)
        return digest.hexdigest()
    except OSError as exc:
        raise AutoCADIntegrationError(
            f"Cannot create atomic AutoCAD scratch copy: {type(exc).__name__}."
        ) from exc
    finally:
        if temporary.exists():
            temporary.unlink()


def _bounded_sha256(path: Path, maximum_bytes: int) -> str:
    digest = hashlib.sha256()
    read = 0
    try:
        with path.open("rb") as handle:
            while True:
                chunk = handle.read(1_048_576)
                if not chunk:
                    break
                read += len(chunk)
                if read > maximum_bytes:
                    raise AutoCADIntegrationError(
                        "AutoCAD drawing grew beyond its hash byte limit."
                    )
                digest.update(chunk)
    except OSError as exc:
        raise AutoCADIntegrationError(
            f"Cannot hash AutoCAD drawing: {type(exc).__name__}."
        ) from exc
    if read == 0:
        raise AutoCADIntegrationError("AutoCAD drawing is empty during hash validation.")
    return digest.hexdigest()


def _validate_sha256(value: str, name: str) -> None:
    if not isinstance(value, str) or re.fullmatch(r"[0-9a-f]{64}", value) is None:
        raise AutoCADIntegrationError(f"{name} must be a lowercase SHA-256 digest.")


def _validate_point(
    point: tuple[float, float, float], name: str
) -> tuple[float, float, float]:
    if not isinstance(point, tuple) or len(point) != 3:
        raise AutoCADIntegrationError(f"AutoCAD {name} point must have three values.")
    validated: list[float] = []
    for coordinate in point:
        if (
            not isinstance(coordinate, (int, float))
            or isinstance(coordinate, bool)
            or not math.isfinite(coordinate)
            or abs(coordinate) > 1_000_000_000
        ):
            raise AutoCADIntegrationError(
                f"AutoCAD {name} coordinates must be finite and within bounds."
            )
        normalized = round(float(coordinate), 9)
        validated.append(0.0 if normalized == 0 else normalized)
    return validated[0], validated[1], validated[2]


def _create_line_script(
    start: tuple[float, float, float],
    end: tuple[float, float, float],
) -> str:
    start_lisp = " ".join(_format_coordinate(value) for value in start)
    end_lisp = " ".join(_format_coordinate(value) for value in end)
    return f"""(progn
(setq aieng-before-selection (ssget "_X"))
(setq aieng-before-count
  (if aieng-before-selection (sslength aieng-before-selection) 0)
)
(setq aieng-before-selection nil)
(command-s "_.LINE" (list {start_lisp}) (list {end_lisp}) "")
(setq aieng-created (entlast))
(setq aieng-created-type
  (if aieng-created (cdr (assoc 0 (entget aieng-created))) "")
)
(setq aieng-after-selection (ssget "_X"))
(setq aieng-after-count
  (if aieng-after-selection (sslength aieng-after-selection) 0)
)
(setq aieng-after-selection nil)
(command-s "_.QSAVE")
(princ (strcat "\\nAIENG_ACADVER=" (getvar "ACADVER")))
(princ (strcat "\\nAIENG_BEFORECOUNT=" (itoa aieng-before-count)))
(princ (strcat "\\nAIENG_AFTERCOUNT=" (itoa aieng-after-count)))
(princ (strcat "\\nAIENG_CREATEDTYPE=" aieng-created-type))
(princ (strcat "\\nAIENG_DBMOD=" (itoa (getvar "DBMOD"))))
(princ "\\nAIENG_DONE=1\\n")
(princ)
)
"""


def _validate_route_points(
    points: tuple[tuple[float, float, float], ...],
) -> tuple[tuple[float, float, float], ...]:
    if not isinstance(points, tuple) or not 2 <= len(points) <= _MAX_ROUTE_POINTS:
        raise AutoCADIntegrationError(
            f"AutoCAD wire route must contain 2 through {_MAX_ROUTE_POINTS} points."
        )
    route = tuple(_validate_point(point, "route") for point in points)
    if any(first == second for first, second in zip(route, route[1:])):
        raise AutoCADIntegrationError(
            "AutoCAD wire route contains identical adjacent points."
        )
    return route


def _create_wire_route_script(
    points: tuple[tuple[float, float, float], ...],
) -> str:
    segment_commands: list[str] = []
    for start, end in zip(points, points[1:]):
        start_lisp = " ".join(_format_coordinate(value) for value in start)
        end_lisp = " ".join(_format_coordinate(value) for value in end)
        segment_commands.extend(
            (
                f'(command-s "_.LINE" (list {start_lisp}) (list {end_lisp}) "")',
                "(setq aieng-created-data (entget (entlast)))",
                "(if (and",
                '  (= (cdr (assoc 0 aieng-created-data)) "LINE")',
                f'  (= (cdr (assoc 8 aieng-created-data)) "{_ELECTRICAL_WIRE_LAYER}")',
                ") (setq aieng-valid-count (+ aieng-valid-count 1)))",
            )
        )
    commands = "\n".join(segment_commands)
    return f"""(progn
(setq aieng-before-selection (ssget "_X"))
(setq aieng-before-count
  (if aieng-before-selection (sslength aieng-before-selection) 0)
)
(setq aieng-before-selection nil)
(if (not (tblsearch "LAYER" "{_ELECTRICAL_WIRE_LAYER}"))
  (entmake (list
    '(0 . "LAYER")
    '(100 . "AcDbSymbolTableRecord")
    '(100 . "AcDbLayerTableRecord")
    '(2 . "{_ELECTRICAL_WIRE_LAYER}")
    '(70 . 0)
    '(62 . {_ELECTRICAL_WIRE_COLOR})
    '(6 . "CONTINUOUS")
  ))
)
(setq aieng-layer-record (tblsearch "LAYER" "{_ELECTRICAL_WIRE_LAYER}"))
(setq aieng-layer-valid
  (if (and
    aieng-layer-record
    (= (cdr (assoc 62 aieng-layer-record)) {_ELECTRICAL_WIRE_COLOR})
    (= (strcase (cdr (assoc 6 aieng-layer-record))) "CONTINUOUS")
  ) 1 0)
)
(setq aieng-valid-count 0)
(if (= aieng-layer-valid 1)
  (progn
    (setq aieng-old-layer (getvar "CLAYER"))
    (setvar "CLAYER" "{_ELECTRICAL_WIRE_LAYER}")
{commands}
    (setvar "CLAYER" aieng-old-layer)
    (command-s "_.QSAVE")
  )
)
(setq aieng-after-selection (ssget "_X"))
(setq aieng-after-count
  (if aieng-after-selection (sslength aieng-after-selection) 0)
)
(setq aieng-after-selection nil)
(princ (strcat "\\nAIENG_ACADVER=" (getvar "ACADVER")))
(princ "\\nAIENG_LAYERNAME={_ELECTRICAL_WIRE_LAYER}")
(princ "\\nAIENG_LAYERCOLOR={_ELECTRICAL_WIRE_COLOR}")
(princ "\\nAIENG_LINETYPE=CONTINUOUS")
(princ (strcat "\\nAIENG_LAYERVALID=" (itoa aieng-layer-valid)))
(princ (strcat "\\nAIENG_BEFORECOUNT=" (itoa aieng-before-count)))
(princ (strcat "\\nAIENG_AFTERCOUNT=" (itoa aieng-after-count)))
(princ "\\nAIENG_SEGMENTCOUNT={len(points) - 1}")
(princ (strcat "\\nAIENG_VALIDCOUNT=" (itoa aieng-valid-count)))
(princ (strcat "\\nAIENG_DBMOD=" (itoa (getvar "DBMOD"))))
(princ "\\nAIENG_DONE=1\\n")
(princ)
)
"""


def _validate_rotation(value: float) -> float:
    if (
        not isinstance(value, (int, float))
        or isinstance(value, bool)
        or not math.isfinite(value)
        or not -360.0 <= value <= 360.0
    ):
        raise AutoCADIntegrationError(
            "AutoCAD symbol rotation must be finite and within -360 through 360 degrees."
        )
    normalized = round(float(value), 9)
    return 0.0 if normalized == 0 else normalized


def _validate_symbol_scale(value: float) -> float:
    if (
        not isinstance(value, (int, float))
        or isinstance(value, bool)
        or not math.isfinite(value)
        or not 0.001 <= value <= _MAX_SYMBOL_SCALE
    ):
        raise AutoCADIntegrationError(
            "AutoCAD symbol scale must be finite and within 0.001 through 1000."
        )
    return round(float(value), 9)


def _validate_circuit_placements(
    placements: tuple[CircuitPlacement, ...],
) -> tuple[CircuitPlacement, ...]:
    if (
        not isinstance(placements, tuple)
        or not _MIN_CIRCUIT_REFERENCES
        <= len(placements)
        <= _MAX_CIRCUIT_REFERENCES
        or any(not isinstance(item, CircuitPlacement) for item in placements)
    ):
        raise AutoCADIntegrationError(
            "AutoCAD circuit fragment must contain 2 through 8 symbol placements."
        )
    return tuple(
        CircuitPlacement(
            point=_validate_point(item.point, "circuit placement"),
            rotation_degrees=_validate_rotation(item.rotation_degrees),
            scale=_validate_symbol_scale(item.scale),
        )
        for item in placements
    )


def _symbol_geometry_lisp(symbol: ElectricalSymbol) -> tuple[str, str]:
    geometry_expressions: list[str] = []
    for entity in symbol.geometry:
        if isinstance(entity, LineGeometry):
            expression = (
                "(entmake (list '(0 . \"LINE\") '(8 . \"0\") "
                f"(cons 10 (list {_lisp_point(entity.start)})) "
                f"(cons 11 (list {_lisp_point(entity.end)}))))"
            )
        elif isinstance(entity, CircleGeometry):
            expression = (
                "(entmake (list '(0 . \"CIRCLE\") '(8 . \"0\") "
                f"(cons 10 (list {_lisp_point(entity.center)})) "
                f"(cons 40 {_format_coordinate(entity.radius)})))"
            )
        else:
            raise AutoCADIntegrationError(
                "Electrical symbol contains unsupported validated geometry."
            )
        geometry_expressions.extend(
            (
                f"(setq aieng-geometry-result {expression})",
                "(if aieng-geometry-result",
                "  (setq aieng-geometry-count (+ aieng-geometry-count 1))",
                ")",
            )
        )
    geometry = "\n".join(geometry_expressions)
    validation_expressions: list[str] = []
    for entity in symbol.geometry:
        if isinstance(entity, LineGeometry):
            conditions = (
                '(= (cdr (assoc 0 aieng-current-data)) "LINE")',
                '(= (cdr (assoc 8 aieng-current-data)) "0")',
                "(equal (cdr (assoc 10 aieng-current-data)) "
                f"(list {_lisp_point(entity.start)}) 0.000000001)",
                "(equal (cdr (assoc 11 aieng-current-data)) "
                f"(list {_lisp_point(entity.end)}) 0.000000001)",
            )
        elif isinstance(entity, CircleGeometry):
            conditions = (
                '(= (cdr (assoc 0 aieng-current-data)) "CIRCLE")',
                '(= (cdr (assoc 8 aieng-current-data)) "0")',
                "(equal (cdr (assoc 10 aieng-current-data)) "
                f"(list {_lisp_point(entity.center)}) 0.000000001)",
                "(equal (cdr (assoc 40 aieng-current-data)) "
                f"{_format_coordinate(entity.radius)} 0.000000001)",
            )
        else:
            raise AutoCADIntegrationError(
                "Electrical symbol contains unsupported validated geometry."
            )
        joined_conditions = "\n        ".join(conditions)
        validation_expressions.append(
            f"""(if aieng-current
      (progn
        (setq aieng-current-data (entget aieng-current))
        (if (and
        {joined_conditions}
        )
          (setq aieng-validated-geometry-count
            (+ aieng-validated-geometry-count 1)
          )
        )
        (setq aieng-current (entnext aieng-current))
      )
    )"""
        )
    validation = "\n    ".join(validation_expressions)
    return geometry, validation


def _symbol_block_setup_lisp(symbol: ElectricalSymbol) -> str:
    base = _lisp_point(symbol.base_point)
    geometry, validation = _symbol_geometry_lisp(symbol)
    return f"""(setq aieng-block-new (if (tblsearch "BLOCK" "{symbol.block_name}") 0 1))
(setq aieng-geometry-count 0)
(setq aieng-block-start nil)
(setq aieng-block-end nil)
(setq aieng-block-header-valid 0)
(setq aieng-block-end-valid 0)
(setq aieng-validated-geometry-count 0)
(if (and
  (= aieng-units-valid 1)
  (= aieng-layer-valid 1)
  (= aieng-block-new 1)
)
  (progn
    (setq aieng-block-start
      (entmake (list
        '(0 . "BLOCK")
        '(100 . "AcDbEntity")
        '(8 . "0")
        '(100 . "AcDbBlockBegin")
        '(2 . "{symbol.block_name}")
        '(70 . 0)
        (cons 10 (list {base}))
      ))
    )
{geometry}
    (setq aieng-block-end
      (entmake (list
        '(0 . "ENDBLK")
        '(100 . "AcDbEntity")
        '(8 . "0")
        '(100 . "AcDbBlockEnd")
      ))
    )
  )
)
(setq aieng-block-defined
  (if (tblsearch "BLOCK" "{symbol.block_name}") 1 0)
)
(setq aieng-block-valid 0)
(if (= aieng-block-defined 1)
  (progn
    (setq aieng-block-record (tblsearch "BLOCK" "{symbol.block_name}"))
    (setq aieng-current (entnext (tblobjname "BLOCK" "{symbol.block_name}")))
    (if (and
      (= (cdr (assoc 70 aieng-block-record)) 0)
      (equal (cdr (assoc 10 aieng-block-record)) (list {base}) 0.000000001)
    )
      (setq aieng-block-header-valid 1)
    )
    {validation}
    (if (or
      (not aieng-current)
      (= (cdr (assoc 0 (entget aieng-current))) "ENDBLK")
    ) (setq aieng-block-end-valid 1))
    (if (and
      (= aieng-block-header-valid 1)
      (= aieng-validated-geometry-count {len(symbol.geometry)})
      (= aieng-block-end-valid 1)
    ) (setq aieng-block-valid 1))
  )
)
(if (and (= aieng-block-new 0) (= aieng-block-valid 1))
  (setq aieng-geometry-count {len(symbol.geometry)})
)"""


def _create_symbol_script(
    symbol: ElectricalSymbol,
    *,
    insertion_point: tuple[float, float, float],
    rotation_degrees: float,
    scale: float,
) -> str:
    insertion = _lisp_point(insertion_point)
    rotation_radians = math.radians(rotation_degrees)
    rotation = format(rotation_radians, ".12f").rstrip("0").rstrip(".") or "0"
    scale_value = _format_coordinate(scale)
    block_setup = _symbol_block_setup_lisp(symbol)
    return f"""(progn
(setq aieng-units-valid (if (= (getvar "INSUNITS") 4) 1 0))
(setq aieng-before-selection (ssget "_X"))
(setq aieng-before-count
  (if aieng-before-selection (sslength aieng-before-selection) 0)
)
(setq aieng-before-selection nil)
(if (not (tblsearch "LAYER" "{symbol.insertion_layer}"))
  (entmake (list
    '(0 . "LAYER")
    '(100 . "AcDbSymbolTableRecord")
    '(100 . "AcDbLayerTableRecord")
    '(2 . "{symbol.insertion_layer}")
    '(70 . 0)
    '(62 . {symbol.layer_color})
    '(6 . "{symbol.linetype}")
  ))
)
(setq aieng-layer-record (tblsearch "LAYER" "{symbol.insertion_layer}"))
(setq aieng-layer-valid
  (if (and
    aieng-layer-record
    (= (cdr (assoc 62 aieng-layer-record)) {symbol.layer_color})
    (= (strcase (cdr (assoc 6 aieng-layer-record))) "{symbol.linetype}")
  ) 1 0)
)
{block_setup}
(setq aieng-insert-valid 0)
(setq aieng-attribute-count 0)
(if (and
  (= aieng-units-valid 1)
  (= aieng-layer-valid 1)
  (= aieng-block-defined 1)
  (= aieng-block-valid 1)
  (= aieng-geometry-count {len(symbol.geometry)})
  (or (= aieng-block-new 0) (and aieng-block-start aieng-block-end))
)
  (progn
    (setq aieng-insert-result
      (entmake (list
        '(0 . "INSERT")
        '(100 . "AcDbEntity")
        '(8 . "{symbol.insertion_layer}")
        '(100 . "AcDbBlockReference")
        '(2 . "{symbol.block_name}")
        (cons 10 (list {insertion}))
        (cons 41 {scale_value})
        (cons 42 {scale_value})
        (cons 43 {scale_value})
        (cons 50 {rotation})
      ))
    )
    (setq aieng-created (if aieng-insert-result (entlast) nil))
    (setq aieng-created-data (if aieng-created (entget aieng-created) nil))
    (if (and
      aieng-created-data
      (= (cdr (assoc 0 aieng-created-data)) "INSERT")
      (= (cdr (assoc 2 aieng-created-data)) "{symbol.block_name}")
      (= (cdr (assoc 8 aieng-created-data)) "{symbol.insertion_layer}")
      (equal (cdr (assoc 10 aieng-created-data)) (list {insertion}) 0.000000001)
      (equal (cdr (assoc 41 aieng-created-data)) {scale_value} 0.000000001)
      (equal (cdr (assoc 42 aieng-created-data)) {scale_value} 0.000000001)
      (equal (cdr (assoc 43 aieng-created-data)) {scale_value} 0.000000001)
      (equal (cdr (assoc 50 aieng-created-data)) {rotation} 0.000000001)
      (not (assoc 66 aieng-created-data))
    ) (setq aieng-insert-valid 1))
    (if (= aieng-insert-valid 1) (command-s "_.QSAVE"))
  )
)
(setq aieng-after-selection (ssget "_X"))
(setq aieng-after-count
  (if aieng-after-selection (sslength aieng-after-selection) 0)
)
(setq aieng-after-selection nil)
(princ (strcat "\\nAIENG_ACADVER=" (getvar "ACADVER")))
(princ (strcat "\\nAIENG_INSUNITS=" (itoa (getvar "INSUNITS"))))
(princ (strcat "\\nAIENG_UNITSVALID=" (itoa aieng-units-valid)))
(princ "\\nAIENG_SYMBOLID={symbol.symbol_id}")
(princ "\\nAIENG_BLOCKNAME={symbol.block_name}")
(princ "\\nAIENG_DEFINITIONHASH={symbol.definition_sha256}")
(princ "\\nAIENG_LAYERNAME={symbol.insertion_layer}")
(princ "\\nAIENG_LAYERCOLOR={symbol.layer_color}")
(princ "\\nAIENG_LINETYPE={symbol.linetype}")
(princ (strcat "\\nAIENG_LAYERVALID=" (itoa aieng-layer-valid)))
(princ (strcat "\\nAIENG_BLOCKNEW=" (itoa aieng-block-new)))
(princ (strcat "\\nAIENG_BLOCKDEFINED=" (itoa aieng-block-defined)))
(princ (strcat "\\nAIENG_BLOCKVALID=" (itoa aieng-block-valid)))
(princ (strcat "\\nAIENG_BLOCKHEADERVALID=" (itoa aieng-block-header-valid)))
(princ (strcat "\\nAIENG_BLOCKENDVALID=" (itoa aieng-block-end-valid)))
(princ (strcat "\\nAIENG_VALIDATEDGEOMETRYCOUNT=" (itoa aieng-validated-geometry-count)))
(princ (strcat "\\nAIENG_GEOMETRYCOUNT=" (itoa aieng-geometry-count)))
(princ (strcat "\\nAIENG_INSERTVALID=" (itoa aieng-insert-valid)))
(princ (strcat "\\nAIENG_ATTRIBUTECOUNT=" (itoa aieng-attribute-count)))
(princ (strcat "\\nAIENG_BEFORECOUNT=" (itoa aieng-before-count)))
(princ (strcat "\\nAIENG_AFTERCOUNT=" (itoa aieng-after-count)))
(princ (strcat "\\nAIENG_DBMOD=" (itoa (getvar "DBMOD"))))
(princ "\\nAIENG_DONE=1\\n")
(princ)
)
"""


def _create_circuit_fragment_script(
    symbol: ElectricalSymbol,
    *,
    placements: tuple[CircuitPlacement, ...],
    wire_points: tuple[tuple[float, float, float], ...],
) -> str:
    block_setup = _symbol_block_setup_lisp(symbol)
    reference_expressions: list[str] = []
    for placement in placements:
        point = _lisp_point(placement.point)
        rotation_radians = math.radians(placement.rotation_degrees)
        rotation = format(rotation_radians, ".12f").rstrip("0").rstrip(".") or "0"
        scale = _format_coordinate(placement.scale)
        reference_expressions.append(
            f"""(setq aieng-insert-result
      (entmake (list
        '(0 . "INSERT")
        '(100 . "AcDbEntity")
        '(8 . "{symbol.insertion_layer}")
        '(100 . "AcDbBlockReference")
        '(2 . "{symbol.block_name}")
        (cons 10 (list {point}))
        (cons 41 {scale})
        (cons 42 {scale})
        (cons 43 {scale})
        (cons 50 {rotation})
      ))
    )
    (setq aieng-created (if aieng-insert-result (entlast) nil))
    (setq aieng-created-data (if aieng-created (entget aieng-created) nil))
    (if (and
      aieng-created-data
      (= (cdr (assoc 0 aieng-created-data)) "INSERT")
      (= (cdr (assoc 2 aieng-created-data)) "{symbol.block_name}")
      (= (cdr (assoc 8 aieng-created-data)) "{symbol.insertion_layer}")
      (equal (cdr (assoc 10 aieng-created-data)) (list {point}) 0.000000001)
      (equal (cdr (assoc 41 aieng-created-data)) {scale} 0.000000001)
      (equal (cdr (assoc 42 aieng-created-data)) {scale} 0.000000001)
      (equal (cdr (assoc 43 aieng-created-data)) {scale} 0.000000001)
      (equal (cdr (assoc 50 aieng-created-data)) {rotation} 0.000000001)
      (not (assoc 66 aieng-created-data))
    ) (setq aieng-valid-reference-count (+ aieng-valid-reference-count 1)))"""
        )
    segment_expressions: list[str] = []
    for start, end in zip(wire_points, wire_points[1:]):
        start_lisp = _lisp_point(start)
        end_lisp = _lisp_point(end)
        segment_expressions.append(
            f"""(setq aieng-line-result
      (entmake (list
        '(0 . "LINE")
        '(8 . "{_ELECTRICAL_WIRE_LAYER}")
        (cons 10 (list {start_lisp}))
        (cons 11 (list {end_lisp}))
      ))
    )
    (setq aieng-created (if aieng-line-result (entlast) nil))
    (setq aieng-created-data (if aieng-created (entget aieng-created) nil))
    (if (and
      aieng-created-data
      (= (cdr (assoc 0 aieng-created-data)) "LINE")
      (= (cdr (assoc 8 aieng-created-data)) "{_ELECTRICAL_WIRE_LAYER}")
      (equal (cdr (assoc 10 aieng-created-data)) (list {start_lisp}) 0.000000001)
      (equal (cdr (assoc 11 aieng-created-data)) (list {end_lisp}) 0.000000001)
    ) (setq aieng-valid-segment-count (+ aieng-valid-segment-count 1)))"""
        )
    references = "\n    ".join(reference_expressions)
    segments = "\n    ".join(segment_expressions)
    reference_count = len(placements)
    segment_count = len(wire_points) - 1
    return f"""(progn
(setq aieng-units-valid (if (= (getvar "INSUNITS") 4) 1 0))
(setq aieng-before-selection (ssget "_X"))
(setq aieng-before-count
  (if aieng-before-selection (sslength aieng-before-selection) 0)
)
(setq aieng-before-selection nil)
(if (not (tblsearch "LAYER" "{symbol.insertion_layer}"))
  (entmake (list
    '(0 . "LAYER")
    '(100 . "AcDbSymbolTableRecord")
    '(100 . "AcDbLayerTableRecord")
    '(2 . "{symbol.insertion_layer}")
    '(70 . 0)
    '(62 . {symbol.layer_color})
    '(6 . "{symbol.linetype}")
  ))
)
(setq aieng-layer-record (tblsearch "LAYER" "{symbol.insertion_layer}"))
(setq aieng-layer-valid
  (if (and
    aieng-layer-record
    (= (cdr (assoc 62 aieng-layer-record)) {symbol.layer_color})
    (= (strcase (cdr (assoc 6 aieng-layer-record))) "{symbol.linetype}")
  ) 1 0)
)
(if (not (tblsearch "LAYER" "{_ELECTRICAL_WIRE_LAYER}"))
  (entmake (list
    '(0 . "LAYER")
    '(100 . "AcDbSymbolTableRecord")
    '(100 . "AcDbLayerTableRecord")
    '(2 . "{_ELECTRICAL_WIRE_LAYER}")
    '(70 . 0)
    '(62 . {_ELECTRICAL_WIRE_COLOR})
    '(6 . "CONTINUOUS")
  ))
)
(setq aieng-wire-layer-record (tblsearch "LAYER" "{_ELECTRICAL_WIRE_LAYER}"))
(setq aieng-wire-layer-valid
  (if (and
    aieng-wire-layer-record
    (= (cdr (assoc 62 aieng-wire-layer-record)) {_ELECTRICAL_WIRE_COLOR})
    (= (strcase (cdr (assoc 6 aieng-wire-layer-record))) "CONTINUOUS")
  ) 1 0)
)
{block_setup}
(setq aieng-valid-reference-count 0)
(setq aieng-valid-segment-count 0)
(if (and
  (= aieng-units-valid 1)
  (= aieng-layer-valid 1)
  (= aieng-wire-layer-valid 1)
  (= aieng-block-defined 1)
  (= aieng-block-valid 1)
  (= aieng-geometry-count {len(symbol.geometry)})
  (or (= aieng-block-new 0) (and aieng-block-start aieng-block-end))
)
  (progn
    {references}
    {segments}
  )
)
(setq aieng-after-selection (ssget "_X"))
(setq aieng-after-count
  (if aieng-after-selection (sslength aieng-after-selection) 0)
)
(setq aieng-after-selection nil)
(if (and
  (= aieng-valid-reference-count {reference_count})
  (= aieng-valid-segment-count {segment_count})
  (= aieng-after-count (+ aieng-before-count {reference_count + segment_count}))
) (command-s "_.QSAVE"))
(princ (strcat "\\nAIENG_ACADVER=" (getvar "ACADVER")))
(princ (strcat "\\nAIENG_INSUNITS=" (itoa (getvar "INSUNITS"))))
(princ (strcat "\\nAIENG_UNITSVALID=" (itoa aieng-units-valid)))
(princ "\\nAIENG_SYMBOLID={symbol.symbol_id}")
(princ "\\nAIENG_BLOCKNAME={symbol.block_name}")
(princ "\\nAIENG_DEFINITIONHASH={symbol.definition_sha256}")
(princ "\\nAIENG_INSERTLAYERNAME={symbol.insertion_layer}")
(princ "\\nAIENG_INSERTLAYERCOLOR={symbol.layer_color}")
(princ "\\nAIENG_INSERTLINETYPE={symbol.linetype}")
(princ (strcat "\\nAIENG_INSERTLAYERVALID=" (itoa aieng-layer-valid)))
(princ "\\nAIENG_WIRELAYERNAME={_ELECTRICAL_WIRE_LAYER}")
(princ "\\nAIENG_WIRELAYERCOLOR={_ELECTRICAL_WIRE_COLOR}")
(princ "\\nAIENG_WIRELINETYPE=CONTINUOUS")
(princ (strcat "\\nAIENG_WIRELAYERVALID=" (itoa aieng-wire-layer-valid)))
(princ (strcat "\\nAIENG_BLOCKNEW=" (itoa aieng-block-new)))
(princ (strcat "\\nAIENG_BLOCKDEFINED=" (itoa aieng-block-defined)))
(princ (strcat "\\nAIENG_BLOCKVALID=" (itoa aieng-block-valid)))
(princ (strcat "\\nAIENG_BLOCKHEADERVALID=" (itoa aieng-block-header-valid)))
(princ (strcat "\\nAIENG_BLOCKENDVALID=" (itoa aieng-block-end-valid)))
(princ (strcat "\\nAIENG_VALIDATEDGEOMETRYCOUNT=" (itoa aieng-validated-geometry-count)))
(princ "\\nAIENG_REFERENCECOUNT={reference_count}")
(princ (strcat "\\nAIENG_VALIDREFERENCECOUNT=" (itoa aieng-valid-reference-count)))
(princ "\\nAIENG_SEGMENTCOUNT={segment_count}")
(princ (strcat "\\nAIENG_VALIDSEGMENTCOUNT=" (itoa aieng-valid-segment-count)))
(princ (strcat "\\nAIENG_BEFORECOUNT=" (itoa aieng-before-count)))
(princ (strcat "\\nAIENG_AFTERCOUNT=" (itoa aieng-after-count)))
(princ (strcat "\\nAIENG_DBMOD=" (itoa (getvar "DBMOD"))))
(princ "\\nAIENG_DONE=1\\n")
(princ)
)
"""


def _create_panel_sld_fixture_script(symbol: ElectricalSymbol) -> str:
    """Return the fixed, non-parameterized M21 scratch acceptance producer."""
    panel_tags = ("PANEL_ID", "PANEL_NAME")
    way_tags = (
        "WAY_ID", "PANEL_ID", "CIRCUIT_ID", "CIRCUIT_NAME", "WAY_NUMBER",
        "WAY_KIND", "PHASE", "POLE_COUNT", "PROTECTION_ID", "PROTECTION_KIND",
        "PROTECTION_DESIGNATION", "RATED_CURRENT_A", "BREAKING_CAPACITY_A",
        "LOAD_BINDINGS", "LOAD_NAMES",
    )
    node_tags = ("NODE_ID", "NODE_KIND", "REF_ID", "CIRCUIT_ID")
    edge_tags = ("EDGE_ID", "FROM_NODE_ID", "TO_NODE_ID", "CIRCUIT_ID")
    definitions = "\n".join(
        _fixture_marker_block_lisp(name, tags)
        for name, tags in (
            ("AIENG_M21_PANEL_V1", panel_tags),
            ("AIENG_M21_WAY_V1", way_tags),
            ("AIENG_M21_SLD_NODE_V1", node_tags),
            ("AIENG_M21_SLD_EDGE_V1", edge_tags),
        )
    )
    panel = _fixture_marker_insert_lisp(
        "AIENG_M21_PANEL_V1", (0.0, 1000.0, 0.0),
        (("PANEL_ID", '"panel:db1"'), ("PANEL_NAME", '"DB-1"')),
        result_variable="aieng-panel-reference",
    )
    way_one_values = (
        ("WAY_ID", '"way:1"'), ("PANEL_ID", '"panel:db1"'),
        ("CIRCUIT_ID", '"circuit:1"'), ("CIRCUIT_NAME", '"Socket circuit"'),
        ("WAY_NUMBER", '"1"'), ("WAY_KIND", '"load"'), ("PHASE", '"l1"'),
        ("POLE_COUNT", '"1"'), ("PROTECTION_ID", '"protection:1"'),
        ("PROTECTION_KIND", '"mcb"'),
        ("PROTECTION_DESIGNATION", '"16 A / 6 kA"'),
        ("RATED_CURRENT_A", '"16"'), ("BREAKING_CAPACITY_A", '"6000"'),
        ("LOAD_BINDINGS", "aieng-load-binding"),
        ("LOAD_NAMES", '"Socket load"'),
    )
    way_one = _fixture_marker_insert_lisp(
        "AIENG_M21_WAY_V1", (0.0, 700.0, 0.0), way_one_values,
        result_variable="aieng-way-one",
    )
    way_two = _fixture_marker_insert_lisp(
        "AIENG_M21_WAY_V1", (1000.0, 700.0, 0.0),
        (
            ("WAY_ID", '"way:2"'), ("PANEL_ID", '"panel:db1"'),
            ("CIRCUIT_ID", '"circuit:2"'), ("CIRCUIT_NAME", '"Spare"'),
            ("WAY_NUMBER", '"2"'), ("WAY_KIND", '"spare"'), ("PHASE", '"l2"'),
            ("POLE_COUNT", '"1"'), ("PROTECTION_ID", '"protection:2"'),
            ("PROTECTION_KIND", '"mcb"'),
            ("PROTECTION_DESIGNATION", '"16 A / 6 kA spare"'),
            ("RATED_CURRENT_A", '"16"'), ("BREAKING_CAPACITY_A", '"6000"'),
        ), result_variable="aieng-way-two",
    )
    node_values = (
        ((0.0, 400.0, 0.0), (("NODE_ID", '"node:panel"'), ("NODE_KIND", '"panel"'),
         ("REF_ID", '"panel:db1"'), ("CIRCUIT_ID", '""'))),
        ((500.0, 400.0, 0.0), (("NODE_ID", '"node:protection-1"'),
         ("NODE_KIND", '"protective_device"'), ("REF_ID", '"protection:1"'),
         ("CIRCUIT_ID", '"circuit:1"'))),
        ((1000.0, 400.0, 0.0), (("NODE_ID", '"node:load-1"'),
         ("NODE_KIND", '"load"'), ("REF_ID", '"load:1"'),
         ("CIRCUIT_ID", '"circuit:1"'))),
        ((1500.0, 400.0, 0.0), (("NODE_ID", '"node:protection-2"'),
         ("NODE_KIND", '"protective_device"'), ("REF_ID", '"protection:2"'),
         ("CIRCUIT_ID", '"circuit:2"'))),
    )
    nodes = "\n".join(
        _fixture_marker_insert_lisp(
            "AIENG_M21_SLD_NODE_V1", point, values,
            result_variable=f"aieng-node-{index}",
        )
        for index, (point, values) in enumerate(node_values, 1)
    )
    edge_values = (
        ((0.0, 100.0, 0.0), (("EDGE_ID", '"edge:1"'),
         ("FROM_NODE_ID", '"node:panel"'), ("TO_NODE_ID", '"node:protection-1"'),
         ("CIRCUIT_ID", '"circuit:1"'))),
        ((500.0, 100.0, 0.0), (("EDGE_ID", '"edge:2"'),
         ("FROM_NODE_ID", '"node:protection-1"'), ("TO_NODE_ID", '"node:load-1"'),
         ("CIRCUIT_ID", '"circuit:1"'))),
        ((1000.0, 100.0, 0.0), (("EDGE_ID", '"edge:3"'),
         ("FROM_NODE_ID", '"node:panel"'), ("TO_NODE_ID", '"node:protection-2"'),
         ("CIRCUIT_ID", '"circuit:2"'))),
    )
    edges = "\n".join(
        _fixture_marker_insert_lisp(
            "AIENG_M21_SLD_EDGE_V1", point, values,
            result_variable=f"aieng-edge-{index}",
        )
        for index, (point, values) in enumerate(edge_values, 1)
    )
    validations = "\n       ".join(
        _fixture_reference_validation_lisp(variable, values)
        for variable, values in (
            ("aieng-panel-reference", (("PANEL_ID", '"panel:db1"'), ("PANEL_NAME", '"DB-1"'))),
            ("aieng-way-one", way_one_values),
            ("aieng-way-two", (
                ("WAY_ID", '"way:2"'), ("PANEL_ID", '"panel:db1"'),
                ("CIRCUIT_ID", '"circuit:2"'), ("CIRCUIT_NAME", '"Spare"'),
                ("WAY_NUMBER", '"2"'), ("WAY_KIND", '"spare"'), ("PHASE", '"l2"'),
                ("POLE_COUNT", '"1"'), ("PROTECTION_ID", '"protection:2"'),
                ("PROTECTION_KIND", '"mcb"'),
                ("PROTECTION_DESIGNATION", '"16 A / 6 kA spare"'),
                ("RATED_CURRENT_A", '"16"'), ("BREAKING_CAPACITY_A", '"6000"'),
            )),
            *((f"aieng-node-{index}", values) for index, (_, values) in enumerate(node_values, 1)),
            *((f"aieng-edge-{index}", values) for index, (_, values) in enumerate(edge_values, 1)),
        )
    )
    block_setup = _symbol_block_setup_lisp(symbol)
    return f"""(progn
(vl-load-com)
(defun aieng-attribute-count (reference / current data total)
  (setq current (entnext reference) total 0)
  (while current
    (setq data (entget current))
    (cond
      ((= (cdr (assoc 0 data)) "ATTRIB") (setq total (+ total 1)))
      ((= (cdr (assoc 0 data)) "SEQEND") (setq current nil))
    )
    (if current (setq current (entnext current)))
  )
  total
)
(defun aieng-attribute-value (reference tag / current data answer)
  (setq current (entnext reference) answer nil)
  (while current
    (setq data (entget current))
    (cond
      ((and (= (cdr (assoc 0 data)) "ATTRIB")
            (= (strcase (cdr (assoc 2 data))) tag))
       (setq answer (cdr (assoc 1 data)) current nil))
      ((= (cdr (assoc 0 data)) "SEQEND") (setq current nil))
    )
    (if current (setq current (entnext current)))
  )
  answer
)
(setq aieng-before-selection (ssget "_X"))
(setq aieng-before-count (if aieng-before-selection (sslength aieng-before-selection) 0))
(setq aieng-before-selection nil)
(setq aieng-units-valid (if (= (getvar "INSUNITS") 4) 1 0))
(if (not (tblsearch "LAYER" "{symbol.insertion_layer}"))
  (entmake (list '(0 . "LAYER") '(100 . "AcDbSymbolTableRecord")
    '(100 . "AcDbLayerTableRecord") '(2 . "{symbol.insertion_layer}")
    '(70 . 0) '(62 . {symbol.layer_color}) '(6 . "{symbol.linetype}")))
)
(setq aieng-layer-record (tblsearch "LAYER" "{symbol.insertion_layer}"))
(setq aieng-layer-valid
  (if (and aieng-layer-record
           (= (cdr (assoc 62 aieng-layer-record)) {symbol.layer_color})
           (= (strcase (cdr (assoc 6 aieng-layer-record))) "{symbol.linetype}")) 1 0))
{block_setup}
(setq aieng-marker-names-free
  (if (and (not (tblsearch "BLOCK" "AIENG_M21_PANEL_V1"))
           (not (tblsearch "BLOCK" "AIENG_M21_WAY_V1"))
           (not (tblsearch "BLOCK" "AIENG_M21_SLD_NODE_V1"))
           (not (tblsearch "BLOCK" "AIENG_M21_SLD_EDGE_V1"))) 1 0))
(setq aieng-created-ok 0)
(if (and (= aieng-units-valid 1) (= aieng-marker-names-free 1)
         (= aieng-block-defined 1) (= aieng-block-valid 1))
  (progn
{definitions}
    (entmake (list '(0 . "INSERT") '(100 . "AcDbEntity")
      '(8 . "{symbol.insertion_layer}") '(100 . "AcDbBlockReference")
      '(2 . "{symbol.block_name}") '(10 2000.0 400.0 0.0)))
    (setq aieng-load-selection
      (ssget "_X" '((0 . "INSERT") (2 . "{symbol.block_name}"))))
    (setq aieng-load-reference
      (if (and aieng-load-selection (= (sslength aieng-load-selection) 1))
        (ssname aieng-load-selection 0) nil))
    (setq aieng-load-handle
      (if aieng-load-reference (cdr (assoc 5 (entget aieng-load-reference))) nil))
    (setq aieng-load-handle-valid
      (if (and aieng-load-reference
               (= (type aieng-load-handle) 'STR)
               (> (strlen aieng-load-handle) 0)
               (handent aieng-load-handle)) 1 0))
    (setq aieng-load-binding
      (if (= aieng-load-handle-valid 1)
        (strcat "load:1@" aieng-load-handle) ""))
{panel}
{way_one}
{way_two}
{nodes}
{edges}
    (setq aieng-created-ok 1)
  )
)
(setq aieng-panel-selection (ssget "_X" '((0 . "INSERT") (2 . "AIENG_M21_PANEL_V1"))))
(setq aieng-way-selection (ssget "_X" '((0 . "INSERT") (2 . "AIENG_M21_WAY_V1"))))
(setq aieng-node-selection (ssget "_X" '((0 . "INSERT") (2 . "AIENG_M21_SLD_NODE_V1"))))
(setq aieng-edge-selection (ssget "_X" '((0 . "INSERT") (2 . "AIENG_M21_SLD_EDGE_V1"))))
(setq aieng-marker-count (+ (if aieng-panel-selection (sslength aieng-panel-selection) 0)
  (if aieng-way-selection (sslength aieng-way-selection) 0)
  (if aieng-node-selection (sslength aieng-node-selection) 0)
  (if aieng-edge-selection (sslength aieng-edge-selection) 0)))
(setq aieng-attribute-count-total
  (if (= aieng-created-ok 1)
    (+ (aieng-attribute-count aieng-panel-reference)
       (aieng-attribute-count aieng-way-one) (aieng-attribute-count aieng-way-two)
       (aieng-attribute-count aieng-node-1) (aieng-attribute-count aieng-node-2)
       (aieng-attribute-count aieng-node-3) (aieng-attribute-count aieng-node-4)
       (aieng-attribute-count aieng-edge-1) (aieng-attribute-count aieng-edge-2)
       (aieng-attribute-count aieng-edge-3)) 0))
(setq aieng-way-one-attribute-count
  (if (= aieng-created-ok 1) (aieng-attribute-count aieng-way-one) 0))
(setq aieng-way-two-attribute-count
  (if (= aieng-created-ok 1) (aieng-attribute-count aieng-way-two) 0))
(setq aieng-load-binding-present
  (if (equal (aieng-attribute-value aieng-way-one "LOAD_BINDINGS")
             aieng-load-binding) 1 0))
(setq aieng-load-name-present
  (if (equal (aieng-attribute-value aieng-way-one "LOAD_NAMES")
             "Socket load") 1 0))
(setq aieng-load-link-valid
  (if (and (= aieng-created-ok 1)
           (equal (aieng-attribute-value aieng-way-one "LOAD_BINDINGS")
              (strcat "load:1@" aieng-load-handle))) 1 0))
(setq aieng-content-valid
  (if (and (= aieng-created-ok 1)
       {validations}) 1 0))
(setq aieng-after-selection (ssget "_X"))
(setq aieng-after-count (if aieng-after-selection (sslength aieng-after-selection) 0))
(if (and (= aieng-created-ok 1) (= aieng-marker-count 10)
         (= aieng-load-handle-valid 1)
         (= aieng-attribute-count-total 58) (= aieng-load-link-valid 1)
         (= aieng-content-valid 1)
         (= aieng-after-count (+ aieng-before-count 11))) (command-s "_.QSAVE"))
(princ (strcat "\nAIENG_ACADVER=" (getvar "ACADVER")))
(princ (strcat "\nAIENG_INSUNITS=" (itoa (getvar "INSUNITS"))))
(princ (strcat "\nAIENG_UNITSVALID=" (itoa aieng-units-valid)))
(princ (strcat "\nAIENG_LAYERVALID=" (itoa aieng-layer-valid)))
(princ "\nAIENG_MARKERPROFILE=AIENG_M21_FIXED_V6")
(princ (strcat "\nAIENG_MARKERBLOCKCOUNT=" (if (= aieng-marker-names-free 1) "4" "0")))
(princ (strcat "\nAIENG_MARKERREFERENCECOUNT=" (itoa aieng-marker-count)))
(princ "\nAIENG_RECOGNIZEDLOADREFERENCECOUNT=1")
(princ (strcat "\nAIENG_LOADHANDLEVALID=" (itoa aieng-load-handle-valid)))
(princ (strcat "\nAIENG_ATTRIBUTECOUNT=" (itoa aieng-attribute-count-total)))
(princ (strcat "\nAIENG_WAY1ATTRIBUTECOUNT=" (itoa aieng-way-one-attribute-count)))
(princ (strcat "\nAIENG_WAY2ATTRIBUTECOUNT=" (itoa aieng-way-two-attribute-count)))
(princ (strcat "\nAIENG_LOADBINDINGPRESENT=" (itoa aieng-load-binding-present)))
(princ (strcat "\nAIENG_LOADNAMEPRESENT=" (itoa aieng-load-name-present)))
(princ (strcat "\nAIENG_LOADLINKVALID=" (itoa aieng-load-link-valid)))
(princ (strcat "\nAIENG_CONTENTVALID=" (itoa aieng-content-valid)))
(princ (strcat "\nAIENG_BEFORECOUNT=" (itoa aieng-before-count)))
(princ (strcat "\nAIENG_AFTERCOUNT=" (itoa aieng-after-count)))
(princ (strcat "\nAIENG_DBMOD=" (itoa (getvar "DBMOD"))))
(princ "\nAIENG_DONE=1\n")
(princ)
)
"""


def _create_maximum_performance_fixture_script() -> str:
    """Return one fixed, non-production 1,792-LINE scratch performance producer."""
    return f"""(progn
(setq aieng-before-selection (ssget "_X"))
(setq aieng-before-count
  (if aieng-before-selection (sslength aieng-before-selection) 0))
(setq aieng-before-selection nil)
(setq aieng-units-valid (if (= (getvar "INSUNITS") 4) 1 0))
(setq aieng-layer-preexisting
  (if (tblsearch "LAYER" "{_PERFORMANCE_FIXTURE_LAYER}") 1 0))
(setq aieng-layer-valid 0)
(setq aieng-valid-count 0)
(if (and (= aieng-units-valid 1) (= aieng-layer-preexisting 0))
  (progn
    (entmake (list '(0 . "LAYER") '(100 . "AcDbSymbolTableRecord")
      '(100 . "AcDbLayerTableRecord") '(2 . "{_PERFORMANCE_FIXTURE_LAYER}")
      '(70 . 0) '(62 . {_PERFORMANCE_FIXTURE_LAYER_COLOR}) '(6 . "CONTINUOUS")))
    (setq aieng-layer-record (tblsearch "LAYER" "{_PERFORMANCE_FIXTURE_LAYER}"))
    (setq aieng-layer-valid
      (if (and aieng-layer-record
               (= (cdr (assoc 62 aieng-layer-record)) {_PERFORMANCE_FIXTURE_LAYER_COLOR})
               (= (strcase (cdr (assoc 6 aieng-layer-record))) "CONTINUOUS")) 1 0))
    (if (= aieng-layer-valid 1)
      (progn
        (setq aieng-index 0)
        (repeat {_MAXIMUM_PERFORMANCE_FIXTURE_OBJECTS}
          (setq aieng-column (rem aieng-index 64))
          (setq aieng-row (fix (/ aieng-index 64)))
          (setq aieng-x (* 20.0 aieng-column))
          (setq aieng-y (* 20.0 aieng-row))
          (if (entmakex (list '(0 . "LINE")
                (cons 8 "{_PERFORMANCE_FIXTURE_LAYER}")
                (cons 10 (list aieng-x aieng-y 0.0))
                (cons 11 (list (+ aieng-x 10.0) aieng-y 0.0))))
            (setq aieng-valid-count (+ aieng-valid-count 1)))
          (setq aieng-index (+ aieng-index 1))
        )
      )
    )
  )
)
(setq aieng-layer-selection
  (ssget "_X" '((0 . "LINE") (8 . "{_PERFORMANCE_FIXTURE_LAYER}"))))
(setq aieng-layer-object-count
  (if aieng-layer-selection (sslength aieng-layer-selection) 0))
(setq aieng-after-selection (ssget "_X"))
(setq aieng-after-count
  (if aieng-after-selection (sslength aieng-after-selection) 0))
(if (and (= aieng-layer-preexisting 0) (= aieng-layer-valid 1)
         (= aieng-valid-count {_MAXIMUM_PERFORMANCE_FIXTURE_OBJECTS})
         (= aieng-layer-object-count {_MAXIMUM_PERFORMANCE_FIXTURE_OBJECTS})
         (= aieng-after-count
            (+ aieng-before-count {_MAXIMUM_PERFORMANCE_FIXTURE_OBJECTS})))
  (command-s "_.QSAVE"))
(princ (strcat "\nAIENG_ACADVER=" (getvar "ACADVER")))
(princ (strcat "\nAIENG_INSUNITS=" (itoa (getvar "INSUNITS"))))
(princ (strcat "\nAIENG_UNITSVALID=" (itoa aieng-units-valid)))
(princ "\nAIENG_FIXTUREPROFILE={_PERFORMANCE_FIXTURE_PROFILE}")
(princ "\nAIENG_LAYERNAME={_PERFORMANCE_FIXTURE_LAYER}")
(princ "\nAIENG_LAYERCOLOR={_PERFORMANCE_FIXTURE_LAYER_COLOR}")
(princ (strcat "\nAIENG_LAYERPREEXISTING=" (itoa aieng-layer-preexisting)))
(princ (strcat "\nAIENG_LAYERVALID=" (itoa aieng-layer-valid)))
(princ "\nAIENG_OBJECTCOUNT={_MAXIMUM_PERFORMANCE_FIXTURE_OBJECTS}")
(princ (strcat "\nAIENG_VALIDCOUNT=" (itoa aieng-valid-count)))
(princ (strcat "\nAIENG_LAYEROBJECTCOUNT=" (itoa aieng-layer-object-count)))
(princ (strcat "\nAIENG_BEFORECOUNT=" (itoa aieng-before-count)))
(princ (strcat "\nAIENG_AFTERCOUNT=" (itoa aieng-after-count)))
(princ (strcat "\nAIENG_DBMOD=" (itoa (getvar "DBMOD"))))
(princ "\nAIENG_DONE=1\n")
(princ)
)
"""


def _fixture_marker_block_lisp(name: str, tags: tuple[str, ...]) -> str:
    attributes = []
    for index, tag in enumerate(tags):
        y = -float(index) * 4.0
        attributes.append(
            f"""    (entmake (list '(0 . \"ATTDEF\") '(8 . \"0\")
      (cons 10 (list 0.0 {y:.1f} 0.0)) '(40 . 2.5) '(1 . \"\")
      '(3 . \"{tag}\") '(2 . \"{tag}\") '(70 . 1)))"""
        )
    return f"""    (entmake (list '(0 . "BLOCK") '(100 . "AcDbEntity") '(8 . "0")
      '(100 . "AcDbBlockBegin") '(2 . "{name}") '(70 . 2) '(10 0.0 0.0 0.0)))
    (entmake '((0 . "LINE") (8 . "0") (10 0.0 0.0 0.0) (11 100.0 0.0 0.0)))
{chr(10).join(attributes)}
    (entmake '((0 . "ENDBLK") (100 . "AcDbEntity") (8 . "0")
      (100 . "AcDbBlockEnd")))"""


def _fixture_marker_insert_lisp(
    block_name: str,
    point: tuple[float, float, float],
    values: tuple[tuple[str, str], ...],
    *,
    result_variable: str,
) -> str:
    location = _lisp_point(point)
    attributes = "\n".join(
        f"""    (entmake (list '(0 . "ATTRIB") '(8 . "E-POWR-DEVC")
      (cons 10 (list {location})) '(40 . 2.5) (cons 1 {value_expression})
      '(2 . "{tag}") '(70 . 1)))"""
        for tag, value_expression in values
    )
    return f"""    (entmake (list '(0 . "INSERT") '(100 . "AcDbEntity")
      '(8 . "E-POWR-DEVC") '(100 . "AcDbBlockReference") '(2 . "{block_name}")
      (cons 10 (list {location})) '(66 . 1)))
{attributes}
    (entmake '((0 . "SEQEND")))
    (setq {result_variable} (entlast))"""


def _fixture_reference_validation_lisp(
    variable: str, values: tuple[tuple[str, str], ...]
) -> str:
    checks = "\n            ".join(
        f'(equal (aieng-attribute-value {variable} "{tag}") {value_expression})'
        for tag, value_expression in values
    )
    return (
        f"(and (= (aieng-attribute-count {variable}) {len(values)})\n"
        f"            {checks})"
    )


def _lisp_point(point: tuple[float, float, float]) -> str:
    return " ".join(_format_coordinate(value) for value in point)


def _validate_symbol_review_request(
    *,
    block_name: str,
    symbol_id: str,
    symbol_version: int,
    insertion_layer: str,
    layer_color: int,
) -> dict[str, object]:
    if (
        not isinstance(block_name, str)
        or re.fullmatch(r"[A-Z][A-Z0-9_-]{0,63}", block_name) is None
    ):
        raise AutoCADIntegrationError(
            "Reviewed AutoCAD block name must be a bounded uppercase safe name."
        )
    if (
        not isinstance(symbol_id, str)
        or re.fullmatch(r"[a-z][a-z0-9_]{0,47}", symbol_id) is None
    ):
        raise AutoCADIntegrationError("Reviewed electrical symbol ID is invalid.")
    if (
        not isinstance(symbol_version, int)
        or isinstance(symbol_version, bool)
        or not 1 <= symbol_version <= 9999
    ):
        raise AutoCADIntegrationError(
            "Reviewed electrical symbol version must be from 1 through 9999."
        )
    if (
        not isinstance(insertion_layer, str)
        or re.fullmatch(r"E-[A-Z0-9]{4}(?:-[A-Z0-9]{4})?", insertion_layer) is None
    ):
        raise AutoCADIntegrationError("Reviewed electrical insertion layer is invalid.")
    if (
        not isinstance(layer_color, int)
        or isinstance(layer_color, bool)
        or not 1 <= layer_color <= 255
    ):
        raise AutoCADIntegrationError(
            "Reviewed electrical layer color must be ACI 1 through 255."
        )
    return {
        "block_name": block_name,
        "symbol_id": symbol_id,
        "symbol_version": symbol_version,
        "insertion_layer": insertion_layer,
        "layer_color": layer_color,
    }


def _create_symbol_review_script(block_name: str) -> str:
    return f"""(progn
(defun aieng-num (value) (rtos value 2 9))
(defun aieng-point (value)
  (strcat
    (aieng-num (car value)) "|"
    (aieng-num (cadr value)) "|"
    (aieng-num (caddr value))
  )
)
(defun aieng-index (value)
  (cond
    ((< value 10) (strcat "000" (itoa value)))
    ((< value 100) (strcat "00" (itoa value)))
    ((< value 1000) (strcat "0" (itoa value)))
    (T (itoa value))
  )
)
(defun aieng-emit (index value)
  (princ (strcat "\\nAIENG_ENTITY" (aieng-index index) "=" value))
)
(setq aieng-record (tblsearch "BLOCK" "{block_name}"))
(setq aieng-found (if aieng-record 1 0))
(setq aieng-flags (if aieng-record (cdr (assoc 70 aieng-record)) 0))
(setq aieng-base (if aieng-record (cdr (assoc 10 aieng-record)) '(0.0 0.0 0.0)))
(setq aieng-count 0)
(setq aieng-line-count 0)
(setq aieng-circle-count 0)
(setq aieng-unsupported-count 0)
(setq aieng-layer-zero-count 0)
(setq aieng-attdef-count 0)
(setq aieng-nested-count 0)
(setq aieng-truncated 0)
(setq aieng-current
  (if aieng-record (entnext (tblobjname "BLOCK" "{block_name}")) nil)
)
(while (and
  aieng-current
  (/= (cdr (assoc 0 (entget aieng-current))) "ENDBLK")
  (< aieng-count 32)
)
  (setq aieng-data (entget aieng-current))
  (setq aieng-type (cdr (assoc 0 aieng-data)))
  (setq aieng-layer-zero (if (= (cdr (assoc 8 aieng-data)) "0") 1 0))
  (setq aieng-count (+ aieng-count 1))
  (setq aieng-layer-zero-count (+ aieng-layer-zero-count aieng-layer-zero))
  (cond
    ((= aieng-type "LINE")
      (setq aieng-line-count (+ aieng-line-count 1))
      (aieng-emit aieng-count
        (strcat
          "LINE|" (aieng-point (cdr (assoc 10 aieng-data))) "|"
          (aieng-point (cdr (assoc 11 aieng-data))) "|"
          (itoa aieng-layer-zero)
        )
      )
    )
    ((= aieng-type "CIRCLE")
      (setq aieng-circle-count (+ aieng-circle-count 1))
      (aieng-emit aieng-count
        (strcat
          "CIRCLE|" (aieng-point (cdr (assoc 10 aieng-data))) "|"
          (aieng-num (cdr (assoc 40 aieng-data))) "|"
          (itoa aieng-layer-zero)
        )
      )
    )
    (T
      (setq aieng-unsupported-count (+ aieng-unsupported-count 1))
      (if (= aieng-type "ATTDEF")
        (setq aieng-attdef-count (+ aieng-attdef-count 1))
      )
      (if (= aieng-type "INSERT")
        (setq aieng-nested-count (+ aieng-nested-count 1))
      )
      (aieng-emit aieng-count
        (strcat "OTHER|" aieng-type "|" (itoa aieng-layer-zero))
      )
    )
  )
  (setq aieng-current (entnext aieng-current))
)
(if (and
  aieng-current
  (/= (cdr (assoc 0 (entget aieng-current))) "ENDBLK")
) (setq aieng-truncated 1))
(princ (strcat "\\nAIENG_ACADVER=" (getvar "ACADVER")))
(princ (strcat "\\nAIENG_INSUNITS=" (itoa (getvar "INSUNITS"))))
(princ "\\nAIENG_BLOCKNAME={block_name}")
(princ (strcat "\\nAIENG_BLOCKFOUND=" (itoa aieng-found)))
(princ (strcat "\\nAIENG_BLOCKFLAGS=" (itoa aieng-flags)))
(princ (strcat "\\nAIENG_BASEPOINT=" (aieng-point aieng-base)))
(princ (strcat "\\nAIENG_ENTITYCOUNT=" (itoa aieng-count)))
(princ (strcat "\\nAIENG_LINECOUNT=" (itoa aieng-line-count)))
(princ (strcat "\\nAIENG_CIRCLECOUNT=" (itoa aieng-circle-count)))
(princ (strcat "\\nAIENG_UNSUPPORTEDCOUNT=" (itoa aieng-unsupported-count)))
(princ (strcat "\\nAIENG_LAYERZEROCOUNT=" (itoa aieng-layer-zero-count)))
(princ (strcat "\\nAIENG_ATTDEFCOUNT=" (itoa aieng-attdef-count)))
(princ (strcat "\\nAIENG_NESTEDINSERTCOUNT=" (itoa aieng-nested-count)))
(princ (strcat "\\nAIENG_TRUNCATED=" (itoa aieng-truncated)))
(princ (strcat "\\nAIENG_DBMOD=" (itoa (getvar "DBMOD"))))
(princ "\\nAIENG_DONE=1\\n")
(princ)
)
"""


def _format_coordinate(value: float) -> str:
    return format(value, ".9f").rstrip("0").rstrip(".") or "0"


def _parse_markers(output: str) -> dict[str, str]:
    required = {"ACADVER", "INSUNITS", "MEASUREMENT", "TILEMODE", "DBMOD", "DONE"}
    values = _marker_lines(output)
    if set(values) != required or values["DONE"] != "1":
        raise AutoCADIntegrationError("AutoCAD inspection output is incomplete or invalid.")
    if (
        not values["ACADVER"].startswith("24.0")
        or re.fullmatch(r"[0-9A-Za-z .()_-]+", values["ACADVER"]) is None
    ):
        raise AutoCADIntegrationError("AutoCAD inspection version marker is invalid.")
    for name in ("INSUNITS", "MEASUREMENT", "TILEMODE", "DBMOD"):
        if re.fullmatch(r"\d+", values[name]) is None:
            raise AutoCADIntegrationError(f"AutoCAD inspection {name} marker is invalid.")
    return values


def _parse_inventory_markers(
    output: str, *, maximum_count: int
) -> dict[str, str]:
    required = {"ACADVER", "LAYERCOUNT", "ENTITYCOUNT", "DBMOD", "DONE"}
    values = _marker_lines(output)
    if set(values) != required or values["DONE"] != "1":
        raise AutoCADIntegrationError("AutoCAD inventory output is incomplete or invalid.")
    if (
        not values["ACADVER"].startswith("24.0")
        or re.fullmatch(r"[0-9A-Za-z .()_-]+", values["ACADVER"]) is None
    ):
        raise AutoCADIntegrationError("AutoCAD inventory version marker is invalid.")
    for name in ("LAYERCOUNT", "ENTITYCOUNT", "DBMOD"):
        if re.fullmatch(r"\d+", values[name]) is None:
            raise AutoCADIntegrationError(
                f"AutoCAD inventory {name} marker is invalid."
            )
    for name in ("LAYERCOUNT", "ENTITYCOUNT"):
        if int(values[name]) > maximum_count:
            raise AutoCADIntegrationError(
                f"AutoCAD inventory {name} exceeds its configured limit."
            )
    if values["DBMOD"] != "0":
        raise AutoCADIntegrationError("AutoCAD inventory unexpectedly modified the database.")
    return values


def _parse_line_markers(output: str, *, maximum_count: int) -> dict[str, str]:
    required = {
        "ACADVER",
        "BEFORECOUNT",
        "AFTERCOUNT",
        "CREATEDTYPE",
        "DBMOD",
        "DONE",
    }
    values = _marker_lines(output)
    if set(values) != required or values["DONE"] != "1":
        raise AutoCADIntegrationError("AutoCAD line output is incomplete or invalid.")
    if (
        not values["ACADVER"].startswith("24.0")
        or re.fullmatch(r"[0-9A-Za-z .()_-]+", values["ACADVER"]) is None
    ):
        raise AutoCADIntegrationError("AutoCAD line version marker is invalid.")
    if values["CREATEDTYPE"] != "LINE":
        raise AutoCADIntegrationError("AutoCAD created entity is not a LINE.")
    for name in ("BEFORECOUNT", "AFTERCOUNT", "DBMOD"):
        if re.fullmatch(r"\d+", values[name]) is None:
            raise AutoCADIntegrationError(f"AutoCAD line {name} marker is invalid.")
    for name in ("BEFORECOUNT", "AFTERCOUNT"):
        if int(values[name]) > maximum_count:
            raise AutoCADIntegrationError(
                f"AutoCAD line {name} exceeds its configured limit."
            )
    if values["DBMOD"] != "0":
        raise AutoCADIntegrationError("AutoCAD scratch output was not saved cleanly.")
    return values


def _parse_wire_route_markers(
    output: str, *, maximum_count: int
) -> dict[str, str]:
    required = {
        "ACADVER",
        "LAYERNAME",
        "LAYERCOLOR",
        "LINETYPE",
        "LAYERVALID",
        "BEFORECOUNT",
        "AFTERCOUNT",
        "SEGMENTCOUNT",
        "VALIDCOUNT",
        "DBMOD",
        "DONE",
    }
    values = _marker_lines(output)
    if set(values) != required or values["DONE"] != "1":
        raise AutoCADIntegrationError(
            "AutoCAD wire-route output is incomplete or invalid."
        )
    if (
        not values["ACADVER"].startswith("24.0")
        or re.fullmatch(r"[0-9A-Za-z .()_-]+", values["ACADVER"]) is None
    ):
        raise AutoCADIntegrationError("AutoCAD wire-route version is invalid.")
    if (
        values["LAYERNAME"] != _ELECTRICAL_WIRE_LAYER
        or values["LAYERCOLOR"] != str(_ELECTRICAL_WIRE_COLOR)
        or values["LINETYPE"] != "CONTINUOUS"
        or values["LAYERVALID"] != "1"
    ):
        raise AutoCADIntegrationError("AutoCAD wire layer does not match the schema.")
    numeric = (
        "BEFORECOUNT",
        "AFTERCOUNT",
        "SEGMENTCOUNT",
        "VALIDCOUNT",
        "DBMOD",
    )
    for name in numeric:
        if re.fullmatch(r"\d+", values[name]) is None:
            raise AutoCADIntegrationError(
                f"AutoCAD wire-route {name} marker is invalid."
            )
    for name in numeric[:-1]:
        if int(values[name]) > maximum_count:
            raise AutoCADIntegrationError(
                f"AutoCAD wire-route {name} exceeds its configured limit."
            )
    if values["DBMOD"] != "0":
        raise AutoCADIntegrationError("AutoCAD wire-route output was not saved cleanly.")
    return values


def _parse_symbol_markers(
    output: str,
    *,
    symbol: ElectricalSymbol,
    maximum_count: int,
) -> dict[str, str]:
    required = {
        "ACADVER",
        "INSUNITS",
        "UNITSVALID",
        "SYMBOLID",
        "BLOCKNAME",
        "DEFINITIONHASH",
        "LAYERNAME",
        "LAYERCOLOR",
        "LINETYPE",
        "LAYERVALID",
        "BLOCKNEW",
        "BLOCKDEFINED",
        "BLOCKVALID",
        "BLOCKHEADERVALID",
        "BLOCKENDVALID",
        "VALIDATEDGEOMETRYCOUNT",
        "GEOMETRYCOUNT",
        "INSERTVALID",
        "ATTRIBUTECOUNT",
        "BEFORECOUNT",
        "AFTERCOUNT",
        "DBMOD",
        "DONE",
    }
    values = _marker_lines(output)
    if set(values) != required or values["DONE"] != "1":
        raise AutoCADIntegrationError(
            "AutoCAD electrical-symbol output is incomplete or invalid."
        )
    if (
        not values["ACADVER"].startswith("24.0")
        or re.fullmatch(r"[0-9A-Za-z .()_-]+", values["ACADVER"]) is None
    ):
        raise AutoCADIntegrationError("AutoCAD electrical-symbol version is invalid.")
    expected = {
        "SYMBOLID": symbol.symbol_id,
        "BLOCKNAME": symbol.block_name,
        "DEFINITIONHASH": symbol.definition_sha256,
        "LAYERNAME": symbol.insertion_layer,
        "LAYERCOLOR": str(symbol.layer_color),
        "LINETYPE": symbol.linetype,
        "LAYERVALID": "1",
        "BLOCKDEFINED": "1",
        "BLOCKVALID": "1",
        "BLOCKHEADERVALID": "1",
        "BLOCKENDVALID": "1",
        "VALIDATEDGEOMETRYCOUNT": str(len(symbol.geometry)),
        "GEOMETRYCOUNT": str(len(symbol.geometry)),
        "INSERTVALID": "1",
        "ATTRIBUTECOUNT": str(len(symbol.attribute_tags)),
        "INSUNITS": "4",
        "UNITSVALID": "1",
    }
    if any(values[name] != expected_value for name, expected_value in expected.items()):
        raise AutoCADIntegrationError(
            "AutoCAD electrical-symbol result does not match the approved definition."
        )
    if values["BLOCKNEW"] not in {"0", "1"}:
        raise AutoCADIntegrationError(
            "AutoCAD electrical-symbol block reuse marker is invalid."
        )
    for name in (
        "LAYERCOLOR",
        "INSUNITS",
        "UNITSVALID",
        "LAYERVALID",
        "BLOCKNEW",
        "BLOCKDEFINED",
        "BLOCKVALID",
        "BLOCKHEADERVALID",
        "BLOCKENDVALID",
        "VALIDATEDGEOMETRYCOUNT",
        "GEOMETRYCOUNT",
        "INSERTVALID",
        "ATTRIBUTECOUNT",
        "BEFORECOUNT",
        "AFTERCOUNT",
        "DBMOD",
    ):
        if re.fullmatch(r"\d+", values[name]) is None:
            raise AutoCADIntegrationError(
                f"AutoCAD electrical-symbol {name} marker is invalid."
            )
    for name in ("BEFORECOUNT", "AFTERCOUNT"):
        if int(values[name]) > maximum_count:
            raise AutoCADIntegrationError(
                f"AutoCAD electrical-symbol {name} exceeds its configured limit."
            )
    if values["DBMOD"] != "0":
        raise AutoCADIntegrationError(
            "AutoCAD electrical-symbol output was not saved cleanly."
        )
    return values


def _parse_panel_sld_fixture_markers(
    output: str, *, maximum_count: int
) -> dict[str, str]:
    required = {
        "ACADVER", "INSUNITS", "UNITSVALID", "LAYERVALID", "MARKERPROFILE",
        "MARKERBLOCKCOUNT", "MARKERREFERENCECOUNT",
        "RECOGNIZEDLOADREFERENCECOUNT", "LOADHANDLEVALID", "ATTRIBUTECOUNT",
        "WAY1ATTRIBUTECOUNT", "WAY2ATTRIBUTECOUNT", "LOADBINDINGPRESENT",
        "LOADNAMEPRESENT", "LOADLINKVALID", "CONTENTVALID",
        "BEFORECOUNT", "AFTERCOUNT", "DBMOD", "DONE",
    }
    values = _marker_lines(output)
    if set(values) != required or values["DONE"] != "1":
        raise AutoCADIntegrationError(
            "AutoCAD panel/SLD fixture output is incomplete or invalid."
        )
    if (
        not values["ACADVER"].startswith("24.0")
        or re.fullmatch(r"[0-9A-Za-z .()_-]+", values["ACADVER"]) is None
    ):
        raise AutoCADIntegrationError("AutoCAD panel/SLD fixture version is invalid.")
    expected = {
        "INSUNITS": "4", "UNITSVALID": "1", "LAYERVALID": "1",
        "MARKERPROFILE": "AIENG_M21_FIXED_V6", "MARKERBLOCKCOUNT": "4",
        "MARKERREFERENCECOUNT": "10", "RECOGNIZEDLOADREFERENCECOUNT": "1",
        "LOADHANDLEVALID": "1", "ATTRIBUTECOUNT": "58", "WAY1ATTRIBUTECOUNT": "15",
        "WAY2ATTRIBUTECOUNT": "13", "LOADBINDINGPRESENT": "1", "LOADNAMEPRESENT": "1",
        "LOADLINKVALID": "1", "CONTENTVALID": "1",
        "DBMOD": "0",
    }
    if any(values[name] != expected_value for name, expected_value in expected.items()):
        raise AutoCADIntegrationError(
            "AutoCAD panel/SLD fixture does not match the fixed marker profile."
        )
    for name in (
        "INSUNITS", "UNITSVALID", "LAYERVALID", "MARKERBLOCKCOUNT", "MARKERREFERENCECOUNT",
        "RECOGNIZEDLOADREFERENCECOUNT", "LOADHANDLEVALID", "ATTRIBUTECOUNT",
        "WAY1ATTRIBUTECOUNT", "WAY2ATTRIBUTECOUNT", "LOADBINDINGPRESENT",
        "LOADNAMEPRESENT", "LOADLINKVALID", "CONTENTVALID",
        "BEFORECOUNT", "AFTERCOUNT", "DBMOD",
    ):
        if re.fullmatch(r"\d+", values[name]) is None:
            raise AutoCADIntegrationError(
                f"AutoCAD panel/SLD fixture {name} marker is invalid."
            )
    for name in ("BEFORECOUNT", "AFTERCOUNT"):
        if int(values[name]) > maximum_count:
            raise AutoCADIntegrationError(
                f"AutoCAD panel/SLD fixture {name} exceeds its configured limit."
            )
    return values


def _parse_panel_sld_draft_markers(
    output: str,
    *,
    expected_preview_sha256: str,
    expected_source_sha256: str,
    expected_count: int,
    maximum_count: int,
) -> dict[str, str]:
    """Validate the exact markers emitted by the reviewed DB3-GA2 producer."""
    required = {
        "ACADVER", "INSUNITS", "TILEMODE", "PROFILE", "PREVIEWSHA256",
        "SOURCESHA256", "REGIONBEFORE", "LAYERPREEXISTING", "EXPECTEDCOUNT",
        "CREATEDCOUNT", "CONTENTVALID", "BEFORECOUNT", "AFTERCOUNT", "DBMOD",
        "DONE",
    }
    values = _marker_lines(output)
    if set(values) != required or values["DONE"] != "1":
        raise AutoCADIntegrationError(
            "AutoCAD DB3-GA2 SLD output is incomplete or invalid."
        )
    if (
        not values["ACADVER"].startswith("24.0")
        or re.fullmatch(r"[0-9A-Za-z .()_-]+", values["ACADVER"]) is None
    ):
        raise AutoCADIntegrationError("AutoCAD DB3-GA2 SLD version is invalid.")
    expected = {
        "INSUNITS": "4",
        "TILEMODE": "1",
        "PROFILE": _PANEL_SLD_DRAFT_PROFILE,
        "PREVIEWSHA256": expected_preview_sha256,
        "SOURCESHA256": expected_source_sha256,
        "REGIONBEFORE": "0",
        "LAYERPREEXISTING": "0",
        "EXPECTEDCOUNT": str(expected_count),
        "CREATEDCOUNT": str(expected_count),
        "CONTENTVALID": "1",
        "DBMOD": "0",
    }
    if any(values[name] != expected_value for name, expected_value in expected.items()):
        raise AutoCADIntegrationError(
            "AutoCAD DB3-GA2 SLD does not match the reviewed preview and placement."
        )
    for name in (
        "INSUNITS", "TILEMODE", "REGIONBEFORE", "LAYERPREEXISTING",
        "EXPECTEDCOUNT", "CREATEDCOUNT", "CONTENTVALID", "BEFORECOUNT",
        "AFTERCOUNT", "DBMOD",
    ):
        if re.fullmatch(r"\d+", values[name]) is None:
            raise AutoCADIntegrationError(
                f"AutoCAD DB3-GA2 SLD {name} marker is invalid."
            )
    for name in ("EXPECTEDCOUNT", "CREATEDCOUNT", "BEFORECOUNT", "AFTERCOUNT"):
        if int(values[name]) > maximum_count:
            raise AutoCADIntegrationError(
                f"AutoCAD DB3-GA2 SLD {name} exceeds its configured limit."
            )
    if int(values["AFTERCOUNT"]) != int(values["BEFORECOUNT"]) + expected_count:
        raise AutoCADIntegrationError("AutoCAD DB3-GA2 SLD entity total is invalid.")
    return values


def _parse_right_group_review_markers(
    output: str,
    *,
    expected_preview_sha256: str,
    expected_source_sha256: str,
    maximum_count: int,
) -> dict[str, str]:
    required = {
        "ACADVER", "INSUNITS", "TILEMODE", "PROFILE", "PREVIEWSHA256",
        "SOURCESHA256", "LAYERPREEXISTING", "VALIDROUTECOUNT", "VALIDBLOCKCOUNT",
        "VALIDTAGCOUNT", "EXPECTEDCOUNT", "CREATEDCOUNT", "LAYERCREATEDCOUNT",
        "CONTENTVALID", "BEFORECOUNT", "AFTERCOUNT", "DBMOD", "DONE",
    }
    values = _marker_lines(output)
    if set(values) != required or values["DONE"] != "1":
        raise AutoCADIntegrationError("AutoCAD right-group review output is incomplete or invalid.")
    expected = {
        "INSUNITS": "4", "TILEMODE": "1", "PROFILE": _RIGHT_GROUP_PROFILE,
        "PREVIEWSHA256": expected_preview_sha256, "SOURCESHA256": expected_source_sha256,
        "LAYERPREEXISTING": "0", "VALIDROUTECOUNT": str(_RIGHT_GROUP_ROUTE_COUNT),
        "VALIDBLOCKCOUNT": str(_RIGHT_GROUP_BLOCK_COUNT), "VALIDTAGCOUNT": "5",
        "EXPECTEDCOUNT": str(_RIGHT_GROUP_ENTITY_COUNT),
        "CREATEDCOUNT": str(_RIGHT_GROUP_ENTITY_COUNT),
        "LAYERCREATEDCOUNT": str(_RIGHT_GROUP_ENTITY_COUNT), "CONTENTVALID": "1", "DBMOD": "0",
    }
    if not values["ACADVER"].startswith("24.0") or any(
        values[name] != expected_value for name, expected_value in expected.items()
    ):
        raise AutoCADIntegrationError(
            "AutoCAD right-group review does not match the exact approved objects."
        )
    numeric = (
        "INSUNITS", "TILEMODE", "LAYERPREEXISTING", "VALIDROUTECOUNT",
        "VALIDBLOCKCOUNT", "VALIDTAGCOUNT", "EXPECTEDCOUNT", "CREATEDCOUNT",
        "LAYERCREATEDCOUNT", "CONTENTVALID", "BEFORECOUNT", "AFTERCOUNT", "DBMOD",
    )
    if any(re.fullmatch(r"\d+", values[name]) is None for name in numeric):
        raise AutoCADIntegrationError("AutoCAD right-group numeric marker is invalid.")
    if any(int(values[name]) > maximum_count for name in ("BEFORECOUNT", "AFTERCOUNT")):
        raise AutoCADIntegrationError("AutoCAD right-group entity count exceeds its limit.")
    if int(values["AFTERCOUNT"]) != int(values["BEFORECOUNT"]) + _RIGHT_GROUP_ENTITY_COUNT:
        raise AutoCADIntegrationError("AutoCAD right-group entity total is invalid.")
    return values


def _parse_maximum_performance_fixture_markers(
    output: str, *, maximum_count: int
) -> dict[str, str]:
    required = {
        "ACADVER", "INSUNITS", "UNITSVALID", "FIXTUREPROFILE", "LAYERNAME",
        "LAYERCOLOR", "LAYERPREEXISTING", "LAYERVALID", "OBJECTCOUNT",
        "VALIDCOUNT", "LAYEROBJECTCOUNT", "BEFORECOUNT", "AFTERCOUNT",
        "DBMOD", "DONE",
    }
    values = _marker_lines(output)
    if set(values) != required or values["DONE"] != "1":
        raise AutoCADIntegrationError(
            "AutoCAD maximum performance fixture output is incomplete or invalid."
        )
    if (
        not values["ACADVER"].startswith("24.0")
        or re.fullmatch(r"[0-9A-Za-z .()_-]+", values["ACADVER"]) is None
    ):
        raise AutoCADIntegrationError(
            "AutoCAD maximum performance fixture version is invalid."
        )
    expected = {
        "INSUNITS": "4",
        "UNITSVALID": "1",
        "FIXTUREPROFILE": _PERFORMANCE_FIXTURE_PROFILE,
        "LAYERNAME": _PERFORMANCE_FIXTURE_LAYER,
        "LAYERCOLOR": str(_PERFORMANCE_FIXTURE_LAYER_COLOR),
        "LAYERPREEXISTING": "0",
        "LAYERVALID": "1",
        "OBJECTCOUNT": str(_MAXIMUM_PERFORMANCE_FIXTURE_OBJECTS),
        "VALIDCOUNT": str(_MAXIMUM_PERFORMANCE_FIXTURE_OBJECTS),
        "LAYEROBJECTCOUNT": str(_MAXIMUM_PERFORMANCE_FIXTURE_OBJECTS),
        "DBMOD": "0",
    }
    if any(values[name] != expected_value for name, expected_value in expected.items()):
        raise AutoCADIntegrationError(
            "AutoCAD maximum performance fixture does not match its fixed profile."
        )
    for name in (
        "INSUNITS", "UNITSVALID", "LAYERCOLOR", "LAYERPREEXISTING", "LAYERVALID",
        "OBJECTCOUNT", "VALIDCOUNT", "LAYEROBJECTCOUNT", "BEFORECOUNT",
        "AFTERCOUNT", "DBMOD",
    ):
        if re.fullmatch(r"\d+", values[name]) is None:
            raise AutoCADIntegrationError(
                f"AutoCAD maximum performance fixture {name} marker is invalid."
            )
    for name in ("OBJECTCOUNT", "VALIDCOUNT", "LAYEROBJECTCOUNT", "BEFORECOUNT", "AFTERCOUNT"):
        if int(values[name]) > maximum_count:
            raise AutoCADIntegrationError(
                f"AutoCAD maximum performance fixture {name} exceeds its configured limit."
            )
    if int(values["AFTERCOUNT"]) != int(values["BEFORECOUNT"]) + _MAXIMUM_PERFORMANCE_FIXTURE_OBJECTS:
        raise AutoCADIntegrationError(
            "AutoCAD maximum performance fixture entity total is invalid."
        )
    return values


def _parse_circuit_fragment_markers(
    output: str,
    *,
    symbol: ElectricalSymbol,
    maximum_count: int,
) -> dict[str, str]:
    required = {
        "ACADVER",
        "INSUNITS",
        "UNITSVALID",
        "SYMBOLID",
        "BLOCKNAME",
        "DEFINITIONHASH",
        "INSERTLAYERNAME",
        "INSERTLAYERCOLOR",
        "INSERTLINETYPE",
        "INSERTLAYERVALID",
        "WIRELAYERNAME",
        "WIRELAYERCOLOR",
        "WIRELINETYPE",
        "WIRELAYERVALID",
        "BLOCKNEW",
        "BLOCKDEFINED",
        "BLOCKVALID",
        "BLOCKHEADERVALID",
        "BLOCKENDVALID",
        "VALIDATEDGEOMETRYCOUNT",
        "REFERENCECOUNT",
        "VALIDREFERENCECOUNT",
        "SEGMENTCOUNT",
        "VALIDSEGMENTCOUNT",
        "BEFORECOUNT",
        "AFTERCOUNT",
        "DBMOD",
        "DONE",
    }
    values = _marker_lines(output)
    if set(values) != required or values["DONE"] != "1":
        raise AutoCADIntegrationError(
            "AutoCAD circuit-fragment output is incomplete or invalid."
        )
    if (
        not values["ACADVER"].startswith("24.0")
        or re.fullmatch(r"[0-9A-Za-z .()_-]+", values["ACADVER"]) is None
    ):
        raise AutoCADIntegrationError(
            "AutoCAD circuit-fragment version marker is invalid."
        )
    expected = {
        "INSUNITS": "4",
        "UNITSVALID": "1",
        "SYMBOLID": symbol.symbol_id,
        "BLOCKNAME": symbol.block_name,
        "DEFINITIONHASH": symbol.definition_sha256,
        "INSERTLAYERNAME": symbol.insertion_layer,
        "INSERTLAYERCOLOR": str(symbol.layer_color),
        "INSERTLINETYPE": symbol.linetype,
        "INSERTLAYERVALID": "1",
        "WIRELAYERNAME": _ELECTRICAL_WIRE_LAYER,
        "WIRELAYERCOLOR": str(_ELECTRICAL_WIRE_COLOR),
        "WIRELINETYPE": "CONTINUOUS",
        "WIRELAYERVALID": "1",
        "BLOCKDEFINED": "1",
        "BLOCKVALID": "1",
        "BLOCKHEADERVALID": "1",
        "BLOCKENDVALID": "1",
        "VALIDATEDGEOMETRYCOUNT": str(len(symbol.geometry)),
    }
    if any(values[name] != expected_value for name, expected_value in expected.items()):
        raise AutoCADIntegrationError(
            "AutoCAD circuit fragment does not match the approved catalog definition."
        )
    if values["BLOCKNEW"] not in {"0", "1"}:
        raise AutoCADIntegrationError(
            "AutoCAD circuit-fragment block reuse marker is invalid."
        )
    numeric_names = (
        "INSUNITS",
        "UNITSVALID",
        "INSERTLAYERCOLOR",
        "INSERTLAYERVALID",
        "WIRELAYERCOLOR",
        "WIRELAYERVALID",
        "BLOCKNEW",
        "BLOCKDEFINED",
        "BLOCKVALID",
        "BLOCKHEADERVALID",
        "BLOCKENDVALID",
        "VALIDATEDGEOMETRYCOUNT",
        "REFERENCECOUNT",
        "VALIDREFERENCECOUNT",
        "SEGMENTCOUNT",
        "VALIDSEGMENTCOUNT",
        "BEFORECOUNT",
        "AFTERCOUNT",
        "DBMOD",
    )
    for name in numeric_names:
        if re.fullmatch(r"\d+", values[name]) is None:
            raise AutoCADIntegrationError(
                f"AutoCAD circuit-fragment {name} marker is invalid."
            )
    for name in (
        "REFERENCECOUNT",
        "VALIDREFERENCECOUNT",
        "SEGMENTCOUNT",
        "VALIDSEGMENTCOUNT",
        "BEFORECOUNT",
        "AFTERCOUNT",
    ):
        if int(values[name]) > maximum_count:
            raise AutoCADIntegrationError(
                f"AutoCAD circuit-fragment {name} exceeds its configured limit."
            )
    reference_count = int(values["REFERENCECOUNT"])
    segment_count = int(values["SEGMENTCOUNT"])
    before = int(values["BEFORECOUNT"])
    after = int(values["AFTERCOUNT"])
    if not _MIN_CIRCUIT_REFERENCES <= reference_count <= _MAX_CIRCUIT_REFERENCES:
        raise AutoCADIntegrationError(
            "AutoCAD circuit-fragment reference count is outside its safe bounds."
        )
    if not 1 <= segment_count < _MAX_ROUTE_POINTS:
        raise AutoCADIntegrationError(
            "AutoCAD circuit-fragment segment count is outside its safe bounds."
        )
    if (
        int(values["VALIDREFERENCECOUNT"]) != reference_count
        or int(values["VALIDSEGMENTCOUNT"]) != segment_count
        or after != before + reference_count + segment_count
    ):
        raise AutoCADIntegrationError(
            "AutoCAD circuit-fragment entity counts are inconsistent."
        )
    if values["DBMOD"] != "0":
        raise AutoCADIntegrationError(
            "AutoCAD circuit-fragment output was not saved cleanly."
        )
    return values


def _parse_symbol_review_markers(
    output: str, *, expected_block_name: str
) -> _ParsedSymbolReview:
    values = _marker_lines(output)
    static = {
        "ACADVER",
        "INSUNITS",
        "BLOCKNAME",
        "BLOCKFOUND",
        "BLOCKFLAGS",
        "BASEPOINT",
        "ENTITYCOUNT",
        "LINECOUNT",
        "CIRCLECOUNT",
        "UNSUPPORTEDCOUNT",
        "LAYERZEROCOUNT",
        "ATTDEFCOUNT",
        "NESTEDINSERTCOUNT",
        "TRUNCATED",
        "DBMOD",
        "DONE",
    }
    dynamic = {name for name in values if re.fullmatch(r"ENTITY\d{4}", name)}
    if set(values) - dynamic != static or values.get("DONE") != "1":
        raise AutoCADIntegrationError(
            "AutoCAD electrical-symbol review output is incomplete or invalid."
        )
    if (
        not values["ACADVER"].startswith("24.0")
        or re.fullmatch(r"[0-9A-Za-z .()_-]+", values["ACADVER"]) is None
        or values["BLOCKNAME"] != expected_block_name
    ):
        raise AutoCADIntegrationError(
            "AutoCAD electrical-symbol review identity is invalid."
        )
    numeric_names = (
        "INSUNITS",
        "BLOCKFOUND",
        "BLOCKFLAGS",
        "ENTITYCOUNT",
        "LINECOUNT",
        "CIRCLECOUNT",
        "UNSUPPORTEDCOUNT",
        "LAYERZEROCOUNT",
        "ATTDEFCOUNT",
        "NESTEDINSERTCOUNT",
        "TRUNCATED",
        "DBMOD",
    )
    for name in numeric_names:
        if re.fullmatch(r"\d+", values[name]) is None:
            raise AutoCADIntegrationError(
                f"AutoCAD electrical-symbol review {name} marker is invalid."
            )
    entity_count = int(values["ENTITYCOUNT"])
    if entity_count > 32:
        raise AutoCADIntegrationError(
            "AutoCAD electrical-symbol review entity count exceeds 32."
        )
    expected_dynamic = {f"ENTITY{index:04d}" for index in range(1, entity_count + 1)}
    if dynamic != expected_dynamic:
        raise AutoCADIntegrationError(
            "AutoCAD electrical-symbol review entity markers are incomplete."
        )
    base_point = _parse_review_point(values["BASEPOINT"], "base point")
    geometry: list[dict[str, object]] = []
    line_count = 0
    circle_count = 0
    unsupported_count = 0
    layer_zero_count = 0
    attribute_count = 0
    nested_count = 0
    for index in range(1, entity_count + 1):
        parts = values[f"ENTITY{index:04d}"].split("|")
        if parts[0] == "LINE" and len(parts) == 8:
            start = tuple(_review_number(item) for item in parts[1:4])
            end = tuple(_review_number(item) for item in parts[4:7])
            if start == end:
                raise AutoCADIntegrationError(
                    "AutoCAD reviewed block contains a zero-length line."
                )
            layer_zero = _review_boolean(parts[7])
            geometry.append(
                {"type": "line", "start": list(start), "end": list(end)}
            )
            line_count += 1
        elif parts[0] == "CIRCLE" and len(parts) == 6:
            center = tuple(_review_number(item) for item in parts[1:4])
            radius = _review_number(parts[4])
            if radius <= 0:
                raise AutoCADIntegrationError(
                    "AutoCAD reviewed block contains an invalid circle radius."
                )
            layer_zero = _review_boolean(parts[5])
            geometry.append(
                {"type": "circle", "center": list(center), "radius": radius}
            )
            circle_count += 1
        elif (
            parts[0] == "OTHER"
            and len(parts) == 3
            and re.fullmatch(r"[A-Z0-9_]{1,32}", parts[1]) is not None
        ):
            layer_zero = _review_boolean(parts[2])
            unsupported_count += 1
            attribute_count += int(parts[1] == "ATTDEF")
            nested_count += int(parts[1] == "INSERT")
        else:
            raise AutoCADIntegrationError(
                "AutoCAD electrical-symbol review entity marker is invalid."
            )
        layer_zero_count += int(layer_zero)
    reported = {
        "LINECOUNT": line_count,
        "CIRCLECOUNT": circle_count,
        "UNSUPPORTEDCOUNT": unsupported_count,
        "LAYERZEROCOUNT": layer_zero_count,
        "ATTDEFCOUNT": attribute_count,
        "NESTEDINSERTCOUNT": nested_count,
    }
    if any(int(values[name]) != count for name, count in reported.items()):
        raise AutoCADIntegrationError(
            "AutoCAD electrical-symbol review aggregate counts are inconsistent."
        )
    if values["BLOCKFOUND"] not in {"0", "1"} or values["TRUNCATED"] not in {
        "0",
        "1",
    }:
        raise AutoCADIntegrationError(
            "AutoCAD electrical-symbol review boolean marker is invalid."
        )
    if values["DBMOD"] != "0":
        raise AutoCADIntegrationError(
            "AutoCAD electrical-symbol review modified the drawing database."
        )
    return _ParsedSymbolReview(
        acad_version=values["ACADVER"],
        drawing_units=int(values["INSUNITS"]),
        block_found=values["BLOCKFOUND"] == "1",
        block_flags=int(values["BLOCKFLAGS"]),
        base_point=base_point,
        entity_count=entity_count,
        line_count=line_count,
        circle_count=circle_count,
        unsupported_count=unsupported_count,
        layer_zero_count=layer_zero_count,
        attribute_definition_count=attribute_count,
        nested_insert_count=nested_count,
        truncated=values["TRUNCATED"] == "1",
        database_modified=int(values["DBMOD"]),
        geometry=tuple(geometry),
    )


def _symbol_review_reasons(
    review: _ParsedSymbolReview, block_name: str
) -> tuple[str, ...]:
    reasons: list[str] = []
    if not review.block_found:
        reasons.append("block_not_found")
    if not catalog_block_name_is_valid(block_name):
        reasons.append("block_name_requires_catalog_rename")
    if review.block_flags != 0:
        reasons.append("block_flags_not_supported")
    if review.drawing_units != 4:
        reasons.append("drawing_units_must_be_millimeters")
    if review.truncated:
        reasons.append("block_entity_limit_exceeded")
    if review.entity_count == 0:
        reasons.append("block_has_no_geometry")
    if review.unsupported_count:
        reasons.append("unsupported_geometry_present")
    if review.layer_zero_count != review.entity_count:
        reasons.append("internal_entities_must_use_layer_0")
    if review.attribute_definition_count:
        reasons.append("attributes_not_supported")
    if review.nested_insert_count:
        reasons.append("nested_blocks_not_supported")
    return tuple(reasons)


def _parse_review_point(value: str, label: str) -> tuple[float, float, float]:
    parts = value.split("|")
    if len(parts) != 3:
        raise AutoCADIntegrationError(f"AutoCAD reviewed {label} is invalid.")
    point = tuple(_review_number(item) for item in parts)
    return point[0], point[1], point[2]


def _review_number(value: str) -> float:
    if re.fullmatch(r"-?(?:\d+(?:\.\d*)?|\.\d+)", value) is None:
        raise AutoCADIntegrationError("AutoCAD reviewed geometry number is invalid.")
    number = float(value)
    if not math.isfinite(number) or abs(number) > 1_000_000_000:
        raise AutoCADIntegrationError("AutoCAD reviewed geometry number is out of bounds.")
    normalized = round(number, 9)
    return 0.0 if normalized == 0 else normalized


def _review_boolean(value: str) -> bool:
    if value not in {"0", "1"}:
        raise AutoCADIntegrationError("AutoCAD reviewed entity layer flag is invalid.")
    return value == "1"


def _marker_lines(output: str) -> dict[str, str]:
    matches = re.findall(
        r"(?m)^AIENG_([A-Z0-9]+)=([^\r\n\x00]{1,256})\r?$",
        output,
    )
    names = [name for name, _ in matches]
    if len(names) != len(set(names)):
        raise AutoCADIntegrationError("AutoCAD output contains duplicate result markers.")
    return {name: value.strip() for name, value in matches}


def _scratch_validation_failures(output: str) -> tuple[str, ...]:
    """Return bounded fail-closed marker details when a mutating script saved nothing."""
    try:
        values = _marker_lines(output)
    except AutoCADIntegrationError:
        return ()
    failures = [
        f"{name}=0"
        for name, value in sorted(values.items())
        if value == "0" and (name.endswith("VALID") or name.startswith("VALID"))
    ]
    return tuple(failures[:8])


def _terminate_process_tree(process_id: int) -> None:
    try:
        parent = psutil.Process(process_id)
        processes = parent.children(recursive=True)
        processes.append(parent)
        for process in processes:
            try:
                process.terminate()
            except psutil.NoSuchProcess:
                continue
        _, alive = psutil.wait_procs(processes, timeout=3)
        for process in alive:
            try:
                process.kill()
            except psutil.NoSuchProcess:
                continue
    except psutil.Error:
        return
