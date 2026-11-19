"""Typed command catalog exposing bounded Safe Core capabilities."""

from __future__ import annotations

import math
import hashlib
import re
import shutil
import stat
import sys
import tempfile
import time
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any, Callable, Mapping

from agent.command_engine import CommandContext, CommandDefinition, CommandRegistry
from agent.exceptions import AIEngineerError, AutoCADContextError, CommandExecutionError
from agent.permissions import RiskLevel
from core.file_manager import FileManager
from core.git_manager import GitManager
from core.process_manager import ProcessManager
from core.project_scanner import ProjectScanner
from core.system_info import SystemInfo
from core.terminal_manager import CommandPolicy, TerminalManager
from integrations.autocad import AutoCADManager, CircuitPlacement
from integrations.autocad_component_moves import ComponentMoveTarget, create_component_move_preview
from integrations.autocad_drawing_summary import summarize_drawing_index_for_ai
from integrations.autocad_operator_channel import (
    ActiveAutoCADDocument,
    cancel_operator_command,
    discover_active_documents,
    enqueue_operator_command,
    wait_for_operator_result,
)
from integrations.vscode import VSCodeManager


@dataclass(frozen=True, slots=True)
class CoreServices:
    """Safe Core services available to registered handlers."""

    files: FileManager
    scanner: ProjectScanner
    system: SystemInfo
    processes: ProcessManager
    vscode: VSCodeManager
    autocad: AutoCADManager
    terminal: TerminalManager
    operator_workspace_root: Path


_PAYLOAD_CONTRACTS: dict[str, str] = {
    "_autocad_component_move_preview_payload": '{"path":"<exact saved millimetre DWG <=2 MiB>","expected_source_sha256":"<64 lowercase hex>","context_id":"<32 lowercase hex>","evidence_sha256":"<64 lowercase hex>","moves":[{"object_id":"<exact uppercase block handle>","target_mm":[100,200,0]}]}; 1..32 moves; read-only preview, never execution',
    "_empty_payload": "{}",
    "_path_payload": '{"path":"<relative workspace path; use . for the workspace root>"}',
    "_repository_payload": '{"repository":"<relative workspace repository path; use . for the current workspace>"}',
    "_file_write_payload": '{"path":"<relative file path>","content":"<UTF-8 text>","create_parents":false}; create_parents is optional',
    "_process_terminate_payload": '{"pid":1234,"expected_create_time":1234567890.0}',
    "_vscode_open_payload": '{"path":"<relative workspace path>","line":1,"column":1}; line and column are optional, column requires line',
    "_autocad_line_payload": '{"path":"<scratch DWG path>","start":[0,0,0],"end":[100,0,0]}',
    "_autocad_context_payload": '{"context_id":"<32 lowercase hex chars>","evidence_sha256":"<64 lowercase hex chars>"}',
    "_autocad_drawing_summary_payload": '{"index_id":"<32 lowercase hex chars from capture_live_drawing_index context_id>","evidence_sha256":"<64 lowercase hex chars from capture_live_drawing_index context_sha256>"}; emits a bounded engineering summary to the configured AI provider after approval; no paths, full entity records, or drawing mutation',
    "_autocad_handoff_new_payload": '{"run_id":"<32 lowercase hex chars>","output_sha256":"<64 lowercase hex chars>","destination":"<workspace DWG path>"}',
    "_autocad_handoff_replace_payload": '{"run_id":"<32 lowercase hex chars>","output_sha256":"<64 lowercase hex chars>","destination":"<workspace DWG path>","expected_destination_sha256":"<64 lowercase hex chars>"}',
    "_autocad_wire_route_payload": '{"path":"<scratch DWG path>","points":[[0,0,0],[100,0,0]]}; points requires 2..16 distinct adjacent 3-number arrays',
    "_autocad_circuit_fragment_payload": '{"path":"<scratch DWG path>","symbol_id":"<catalog id>","definition_sha256":"<64 lowercase hex chars>","placements":[{"point":[0,0,0],"rotation_degrees":0,"scale":1},{"point":[100,0,0],"rotation_degrees":0,"scale":1}],"wire_points":[[0,0,0],[100,0,0]]}',
    "_autocad_panel_sld_fixture_payload": '{"path":"<disposable millimeter DWG path>","expected_source_sha256":"<64 lowercase hex chars>"}',
    "_autocad_maximum_performance_fixture_payload": '{"path":"<reviewed disposable millimeter DWG path>","expected_source_sha256":"<64 lowercase hex chars>"}',
    "_autocad_symbol_payload": '{"path":"<scratch DWG path>","symbol_id":"<catalog id>","definition_sha256":"<64 lowercase hex chars>","insertion_point":[0,0,0],"rotation_degrees":0,"scale":1}; rotation_degrees and scale are optional',
    "_autocad_symbol_review_payload": '{"path":"<scratch DWG path>","block_name":"<UPPERCASE block>","symbol_id":"<lowercase id>","symbol_version":1,"insertion_layer":"E-XXXX","layer_color":1}',
    "_autocad_symbol_registration_payload": '{"review_run_id":"<32 lowercase hex chars>","proposal_sha256":"<64 lowercase hex chars>","source_sha256":"<64 lowercase hex chars>"}',
    "_autocad_live_operator_payload": '{"drawing_name":"<optional exact active DWG file name>","timeout_seconds":30}; both fields are optional; no command text is accepted',
    "_git_diff_payload": '{"repository":"<relative repository path; use . for current workspace>","cached":false,"path":"<optional relative path>"}; cached and path are optional',
    "_git_log_payload": '{"repository":"<relative repository path; use . for current workspace>","limit":10}; limit is optional from 1..100',
    "_python_compileall_payload": '{"path":"<relative source tree; use . for current workspace>","quiet":true}; quiet is optional',
    "_python_pytest_payload": '{"cwd":"<relative workspace directory; use . for current workspace>","targets":[],"quiet":true,"timeout_seconds":300}; optional targets max 32; no option flags',
}


def _payload_contract_for_validator(validator: Callable[[Mapping[str, Any]], dict[str, Any]]) -> str:
    return _PAYLOAD_CONTRACTS.get(
        getattr(validator, "__name__", ""),
        "Use only exact fields already evidenced by the reviewed plan or prior observation.",
    )


def _definition(
    name: str,
    description: str,
    permission: str,
    risk: RiskLevel,
    validator: Callable[[Mapping[str, Any]], dict[str, Any]],
    handler: Callable[[dict[str, Any], CommandContext], Any],
    *,
    timeout_seconds: int = 30,
) -> CommandDefinition:
    return CommandDefinition(
        name,
        description,
        permission,
        risk,
        validator,
        handler,
        payload_contract=_payload_contract_for_validator(validator),
        timeout_seconds=timeout_seconds,
    )


CORE_PERMISSIONS = frozenset(
    {
        "file.read",
        "file.write",
        "file.delete",
        "git.read",
        "process.read",
        "process.terminate",
        "project.scan",
        "system.read",
        "vscode.open",
        "vscode.read",
        "autocad.read",
        "autocad.run",
        "autocad.catalog.write",
        "autocad.handoff.write",
        "autocad.drawing.replace",
        "autocad.context.read",
        "python.test",
        "python.compile",
    }
)


def register_core_commands(registry: CommandRegistry, services: CoreServices) -> None:
    """Register the bounded built-in Safe Core command catalog."""
    definitions = (
        _definition(
            "autocad.detect",
            "Detect supported AutoCAD 2021/2023 and validated integration methods.",
            "autocad.read",
            RiskLevel.LOW,
            _empty_payload,
            _safe_handler(lambda payload, context: services.autocad.detect().to_dict()),
        ),
        _definition(
            "autocad.get_live_document_status",
            "Request exact read-only status from one active AutoCAD document through the allowlisted local channel.",
            "autocad.context.read",
            RiskLevel.LOW,
            _autocad_live_operator_payload,
            _safe_handler(
                lambda payload, context: _execute_live_operator_action(
                    services,
                    payload,
                    context,
                    requested_action="get_document_status",
                )
            ),
            timeout_seconds=45,
        ),
        _definition(
            "autocad.show_live_palette",
            "Show the existing AI-Engineer palette in one active AutoCAD document without drawing mutation.",
            "autocad.context.read",
            RiskLevel.MODERATE,
            _autocad_live_operator_payload,
            _safe_handler(
                lambda payload, context: _execute_live_operator_action(
                    services,
                    payload,
                    context,
                    requested_action="show_palette",
                )
            ),
            timeout_seconds=45,
        ),
        _definition(
            "autocad.capture_live_drawing_index",
            "Capture a bounded full-drawing read-only index from one active AutoCAD document after approval.",
            "autocad.context.read",
            RiskLevel.HIGH,
            _autocad_live_operator_payload,
            _safe_handler(
                lambda payload, context: _execute_live_operator_action(
                    services,
                    payload,
                    context,
                    requested_action="capture_drawing_index",
                )
            ),
            timeout_seconds=150,
        ),
        _definition(
            "autocad.summarize_drawing_index_for_ai",
            "Validate one exact captured drawing index and disclose only a bounded engineering summary to the configured AI provider after explicit approval.",
            "autocad.context.read",
            RiskLevel.HIGH,
            _autocad_drawing_summary_payload,
            _safe_handler(
                lambda payload, context: summarize_drawing_index_for_ai(
                    services.operator_workspace_root,
                    index_id=payload["index_id"],
                    evidence_sha256=payload["evidence_sha256"],
                )
            ),
        ),
        _definition(
            "autocad.list_electrical_symbols",
            "List validated metadata from the approved electrical symbol catalog.",
            "autocad.read",
            RiskLevel.LOW,
            _empty_payload,
            _safe_handler(
                lambda payload, context: services.autocad.list_electrical_symbols()
            ),
        ),
        _definition(
            "autocad.import_drawing_context",
            "Import one exact local read-only AutoCAD selection or drawing context.",
            "autocad.context.read",
            RiskLevel.HIGH,
            _autocad_context_payload,
            _safe_handler(
                lambda payload, context: services.autocad.import_drawing_context(
                    context_id=payload["context_id"],
                    evidence_sha256=payload["evidence_sha256"],
                    confirmed=True,
                ).to_dict()
            ),
        ),
        _definition(
            "autocad.preview_component_moves",
            "Preview source-bound translations of 1..32 existing Model-space blocks; reports hold points and cannot execute or certify spacing.",
            "autocad.context.read",
            RiskLevel.HIGH,
            _autocad_component_move_preview_payload,
            _safe_handler(lambda payload, context: _preview_component_moves(services, payload)),
        ),
        _definition(
            "autocad.review_electrical_symbol_source",
            "Review one named user block in a read-only scratch copy after approval.",
            "autocad.run",
            RiskLevel.HIGH,
            _autocad_symbol_review_payload,
            _safe_handler(
                lambda payload, context: services.autocad.review_electrical_symbol_source(
                    payload["path"],
                    block_name=payload["block_name"],
                    symbol_id=payload["symbol_id"],
                    symbol_version=payload["symbol_version"],
                    insertion_layer=payload["insertion_layer"],
                    layer_color=payload["layer_color"],
                    confirmed=True,
                ).to_dict()
            ),
        ),
        _definition(
            "autocad.register_electrical_symbol_review",
            "Register one exact eligible review with backup after separate approval.",
            "autocad.catalog.write",
            RiskLevel.HIGH,
            _autocad_symbol_registration_payload,
            _safe_handler(
                lambda payload, context: services.autocad.register_electrical_symbol_review(
                    review_run_id=payload["review_run_id"],
                    proposal_sha256=payload["proposal_sha256"],
                    source_sha256=payload["source_sha256"],
                    confirmed=True,
                )
            ),
        ),
        _definition(
            "autocad.inspect_scratch",
            "Inspect a copied drawing with read-only Core Console after approval.",
            "autocad.run",
            RiskLevel.HIGH,
            _path_payload,
            _safe_handler(
                lambda payload, context: services.autocad.inspect_scratch(
                    payload["path"], confirmed=True
                ).to_dict()
            ),
        ),
        _definition(
            "autocad.inventory_scratch",
            "Count layers and entities in a read-only drawing copy after approval.",
            "autocad.run",
            RiskLevel.HIGH,
            _path_payload,
            _safe_handler(
                lambda payload, context: services.autocad.inventory_scratch(
                    payload["path"], confirmed=True
                ).to_dict()
            ),
        ),
        _definition(
            "autocad.create_line_scratch",
            "Create one bounded line only in a saved scratch copy after approval.",
            "autocad.run",
            RiskLevel.HIGH,
            _autocad_line_payload,
            _safe_handler(
                lambda payload, context: services.autocad.create_line_scratch(
                    payload["path"],
                    start=payload["start"],
                    end=payload["end"],
                    confirmed=True,
                ).to_dict()
            ),
        ),
        _definition(
            "autocad.create_wire_route_scratch",
            "Create a bounded E-WIRE route only in a saved scratch copy after approval.",
            "autocad.run",
            RiskLevel.HIGH,
            _autocad_wire_route_payload,
            _safe_handler(
                lambda payload, context: services.autocad.create_wire_route_scratch(
                    payload["path"],
                    points=payload["points"],
                    confirmed=True,
                ).to_dict()
            ),
        ),
        _definition(
            "autocad.create_electrical_circuit_fragment_scratch",
            "Create a catalog-verified bounded circuit only in one scratch copy.",
            "autocad.run",
            RiskLevel.HIGH,
            _autocad_circuit_fragment_payload,
            _safe_handler(
                lambda payload, context: (
                    services.autocad.create_electrical_circuit_fragment_scratch(
                        payload["path"],
                        symbol_id=payload["symbol_id"],
                        definition_sha256=payload["definition_sha256"],
                        placements=tuple(
                            CircuitPlacement(
                                point=item["point"],
                                rotation_degrees=item["rotation_degrees"],
                                scale=item["scale"],
                            )
                            for item in payload["placements"]
                        ),
                        wire_points=payload["wire_points"],
                        confirmed=True,
                    ).to_dict()
                )
            ),
        ),
        _definition(
            "autocad.create_panel_sld_fixture_scratch",
            "Create the fixed M21 acceptance fixture only in one hash-bound scratch copy.",
            "autocad.run",
            RiskLevel.HIGH,
            _autocad_panel_sld_fixture_payload,
            _safe_handler(
                lambda payload, context: services.autocad.create_panel_sld_fixture_scratch(
                    payload["path"],
                    expected_source_sha256=payload["expected_source_sha256"],
                    confirmed=True,
                ).to_dict()
            ),
        ),
        _definition(
            "autocad.create_maximum_performance_fixture_scratch",
            "Create the fixed 1,792-object performance fixture only in one hash-bound scratch copy.",
            "autocad.run",
            RiskLevel.HIGH,
            _autocad_maximum_performance_fixture_payload,
            _safe_handler(
                lambda payload, context: services.autocad.create_maximum_performance_fixture_scratch(
                    payload["path"],
                    expected_source_sha256=payload["expected_source_sha256"],
                    confirmed=True,
                ).to_dict()
            ),
        ),
        _definition(
            "autocad.place_electrical_symbol_scratch",
            "Place one hash-approved electrical block only in a saved scratch copy.",
            "autocad.run",
            RiskLevel.HIGH,
            _autocad_symbol_payload,
            _safe_handler(
                lambda payload, context: services.autocad.place_electrical_symbol_scratch(
                    payload["path"],
                    symbol_id=payload["symbol_id"],
                    definition_sha256=payload["definition_sha256"],
                    insertion_point=payload["insertion_point"],
                    rotation_degrees=payload["rotation_degrees"],
                    scale=payload["scale"],
                    confirmed=True,
                ).to_dict()
            ),
        ),
        _definition(
            "autocad.handoff_verified_output_new",
            "Create one new workspace drawing from exact hash-bound run evidence.",
            "autocad.handoff.write",
            RiskLevel.HIGH,
            _autocad_handoff_new_payload,
            _safe_handler(
                lambda payload, context: services.autocad.handoff_verified_output_new(
                    run_id=payload["run_id"],
                    output_sha256=payload["output_sha256"],
                    destination=payload["destination"],
                    confirmed=True,
                ).to_dict()
            ),
        ),
        _definition(
            "autocad.replace_with_verified_output",
            "Replace one drawing from hash-bound evidence with backup and rollback.",
            "autocad.drawing.replace",
            RiskLevel.HIGH,
            _autocad_handoff_replace_payload,
            _safe_handler(
                lambda payload, context: services.autocad.replace_with_verified_output(
                    run_id=payload["run_id"],
                    output_sha256=payload["output_sha256"],
                    destination=payload["destination"],
                    expected_destination_sha256=payload[
                        "expected_destination_sha256"
                    ],
                    confirmed=True,
                ).to_dict()
            ),
        ),
        _definition(
            "python.compileall",
            "Compile one bounded Python source tree without executing project code.",
            "python.compile",
            RiskLevel.MODERATE,
            _python_compileall_payload,
            _safe_handler(lambda payload, context: _python_compileall(services, payload)),
        ),
        _definition(
            "python.pytest",
            "Run bounded pytest targets inside an approved workspace root.",
            "python.test",
            RiskLevel.HIGH,
            _python_pytest_payload,
            _safe_handler(lambda payload, context: _python_pytest(services, payload)),
        ),
        _definition(
            "vscode.detect",
            "Detect supported non-secret Visual Studio Code CLI metadata.",
            "vscode.read",
            RiskLevel.LOW,
            _empty_payload,
            _safe_handler(lambda payload, context: services.vscode.detect().to_dict()),
        ),
        _definition(
            "vscode.open",
            "Open one existing workspace path with the supported CLI after approval.",
            "vscode.open",
            RiskLevel.HIGH,
            _vscode_open_payload,
            _safe_handler(
                lambda payload, context: services.vscode.open_path(
                    payload["path"],
                    line=payload["line"],
                    column=payload["column"],
                    confirmed=True,
                ).to_dict()
            ),
        ),
        _definition(
            "system.snapshot",
            "Collect non-secret local system information.",
            "system.read",
            RiskLevel.LOW,
            _empty_payload,
            _safe_handler(lambda payload, context: services.system.snapshot().to_dict()),
        ),
        _definition(
            "process.list",
            "List bounded non-secret process metadata.",
            "process.read",
            RiskLevel.LOW,
            _empty_payload,
            _safe_handler(lambda payload, context: _process_list(services)),
        ),
        _definition(
            "process.terminate",
            "Gracefully terminate one exact process instance after approval.",
            "process.terminate",
            RiskLevel.DESTRUCTIVE,
            _process_terminate_payload,
            _safe_handler(
                lambda payload, context: asdict(
                    services.processes.terminate(
                        payload["pid"],
                        expected_create_time=payload["expected_create_time"],
                        confirmed=True,
                    )
                )
            ),
        ),
        _definition(
            "project.scan",
            "Scan bounded project metadata without reading file contents.",
            "project.scan",
            RiskLevel.LOW,
            _path_payload,
            _safe_handler(
                lambda payload, context: services.scanner.scan(payload["path"]).to_dict()
            ),
        ),
        _definition(
            "file.read",
            "Read one bounded UTF-8 workspace file.",
            "file.read",
            RiskLevel.LOW,
            _path_payload,
            _safe_handler(
                lambda payload, context: {
                    "path": str(services.files.resolve_path(payload["path"])),
                    "content": services.files.read_text(payload["path"]),
                }
            ),
        ),
        _definition(
            "file.write",
            "Atomically write a UTF-8 workspace file after approval.",
            "file.write",
            RiskLevel.HIGH,
            _file_write_payload,
            _safe_handler(
                lambda payload, context: _operation_result(
                    services.files.write_text(
                        payload["path"],
                        payload["content"],
                        confirm_overwrite=True,
                        create_parents=payload["create_parents"],
                    )
                )
            ),
        ),
        _definition(
            "file.delete",
            "Back up and delete one workspace file after approval.",
            "file.delete",
            RiskLevel.DESTRUCTIVE,
            _path_payload,
            _safe_handler(
                lambda payload, context: _operation_result(
                    services.files.delete_file(payload["path"], confirmed=True)
                )
            ),
        ),
        _definition(
            "git.status",
            "Read bounded Git branch and working-tree status.",
            "git.read",
            RiskLevel.LOW,
            _repository_payload,
            _safe_handler(lambda payload, context: _git_status(services, payload)),
        ),
        _definition(
            "git.diff",
            "Read a bounded Git patch without external diff tools.",
            "git.read",
            RiskLevel.LOW,
            _git_diff_payload,
            _safe_handler(
                lambda payload, context: {
                    "diff": GitManager(services.files, payload["repository"]).diff(
                        cached=payload["cached"], path=payload["path"]
                    )
                }
            ),
        ),
        _definition(
            "git.log",
            "Read bounded non-sensitive Git commit metadata.",
            "git.read",
            RiskLevel.LOW,
            _git_log_payload,
            _safe_handler(
                lambda payload, context: {
                    "commits": [
                        item.to_dict()
                        for item in GitManager(
                            services.files, payload["repository"]
                        ).recent_commits(limit=payload["limit"])
                    ]
                }
            ),
        ),
    )
    for definition in definitions:
        registry.register(definition)


def _safe_handler(
    handler: Callable[[dict[str, Any], CommandContext], Any]
) -> Callable[[dict[str, Any], CommandContext], Any]:
    def execute(payload: dict[str, Any], context: CommandContext) -> Any:
        context.raise_if_cancelled()
        try:
            return handler(payload, context)
        except AIEngineerError as exc:
            raise CommandExecutionError(str(exc)) from exc

    return execute


def _require_keys(
    payload: Mapping[str, Any], required: set[str], optional: set[str] | None = None
) -> dict[str, Any]:
    optional = optional or set()
    keys = set(payload)
    missing = required - keys
    unexpected = keys - required - optional
    if missing:
        raise ValueError(f"Missing fields: {', '.join(sorted(missing))}")
    if unexpected:
        raise ValueError(f"Unexpected fields: {', '.join(sorted(unexpected))}")
    return dict(payload)


def _empty_payload(payload: Mapping[str, Any]) -> dict[str, Any]:
    return _require_keys(payload, set())


def _path_payload(payload: Mapping[str, Any]) -> dict[str, Any]:
    value = _require_keys(payload, {"path"})
    if not isinstance(value["path"], str) or not value["path"]:
        raise ValueError("path must be a non-empty string")
    return value


def _repository_payload(payload: Mapping[str, Any]) -> dict[str, Any]:
    value = _require_keys(payload, {"repository"})
    if not isinstance(value["repository"], str) or not value["repository"]:
        raise ValueError("repository must be a non-empty string")
    return value


def _file_write_payload(payload: Mapping[str, Any]) -> dict[str, Any]:
    value = _require_keys(payload, {"path", "content"}, {"create_parents"})
    if not isinstance(value["path"], str) or not value["path"]:
        raise ValueError("path must be a non-empty string")
    if not isinstance(value["content"], str):
        raise ValueError("content must be a string")
    create_parents = value.get("create_parents", False)
    if not isinstance(create_parents, bool):
        raise ValueError("create_parents must be a boolean")
    value["create_parents"] = create_parents
    return value


def _process_terminate_payload(payload: Mapping[str, Any]) -> dict[str, Any]:
    value = _require_keys(payload, {"pid", "expected_create_time"})
    if not isinstance(value["pid"], int) or isinstance(value["pid"], bool):
        raise ValueError("pid must be an integer")
    if not isinstance(value["expected_create_time"], (int, float)) or isinstance(
        value["expected_create_time"], bool
    ):
        raise ValueError("expected_create_time must be numeric")
    return value


def _vscode_open_payload(payload: Mapping[str, Any]) -> dict[str, Any]:
    value = _require_keys(payload, {"path"}, {"line", "column"})
    _path_payload({"path": value["path"]})
    line = value.get("line")
    column = value.get("column")
    for name, position in (("line", line), ("column", column)):
        if position is not None and (
            not isinstance(position, int)
            or isinstance(position, bool)
            or not 1 <= position <= 1_000_000
        ):
            raise ValueError(f"{name} must be null or an integer from 1 through 1000000")
    if column is not None and line is None:
        raise ValueError("column requires line")
    value.update({"line": line, "column": column})
    return value


def _autocad_line_payload(payload: Mapping[str, Any]) -> dict[str, Any]:
    value = _require_keys(payload, {"path", "start", "end"})
    _path_payload({"path": value["path"]})
    points: dict[str, tuple[float, float, float]] = {}
    for name in ("start", "end"):
        point = value[name]
        if not isinstance(point, list) or len(point) != 3:
            raise ValueError(f"{name} must be a three-number array")
        coordinates: list[float] = []
        for coordinate in point:
            if (
                not isinstance(coordinate, (int, float))
                or isinstance(coordinate, bool)
                or not math.isfinite(coordinate)
                or abs(coordinate) > 1_000_000_000
            ):
                raise ValueError(
                    f"{name} coordinates must be finite numbers within bounds"
                )
            normalized = round(float(coordinate), 9)
            coordinates.append(0.0 if normalized == 0 else normalized)
        points[name] = coordinates[0], coordinates[1], coordinates[2]
    if points["start"] == points["end"]:
        raise ValueError("start and end points must be different")
    value.update(points)
    return value


def _autocad_context_payload(payload: Mapping[str, Any]) -> dict[str, Any]:
    value = _require_keys(payload, {"context_id", "evidence_sha256"})
    if (
        not isinstance(value["context_id"], str)
        or re.fullmatch(r"[0-9a-f]{32}", value["context_id"]) is None
    ):
        raise ValueError("context_id must be a lowercase 32-character identifier")
    if (
        not isinstance(value["evidence_sha256"], str)
        or re.fullmatch(r"[0-9a-f]{64}", value["evidence_sha256"]) is None
    ):
        raise ValueError("evidence_sha256 must be a lowercase SHA-256 digest")
    return value


def _autocad_component_move_preview_payload(payload: Mapping[str, Any]) -> dict[str, Any]:
    """Reject unknown fields, duplicate handles, non-finite points and unbounded work."""
    value = _require_keys(payload, {"path", "expected_source_sha256", "context_id", "evidence_sha256", "moves"})
    _autocad_panel_sld_fixture_payload({"path": value["path"], "expected_source_sha256": value["expected_source_sha256"]})
    _autocad_context_payload({"context_id": value["context_id"], "evidence_sha256": value["evidence_sha256"]})
    raw_moves = value["moves"]
    if not isinstance(raw_moves, list) or not 1 <= len(raw_moves) <= 32:
        raise ValueError("moves must be a list of 1..32 exact block targets")
    moves: list[dict[str, Any]] = []
    handles: set[str] = set()
    for raw in raw_moves:
        if not isinstance(raw, dict):
            raise ValueError("each move must be an object")
        move = _require_keys(raw, {"object_id", "target_mm"})
        point = move["target_mm"]
        if not isinstance(point, list) or len(point) != 3:
            raise ValueError("target_mm must be an XYZ array")
        try:
            target = ComponentMoveTarget(move["object_id"], tuple(point))
        except AutoCADContextError as exc:
            raise ValueError(str(exc)) from exc
        if target.object_id in handles:
            raise ValueError("duplicate component handle")
        handles.add(target.object_id)
        moves.append({"object_id": target.object_id, "target_mm": list(target.target_mm)})
    value["moves"] = moves
    return value


def _preview_component_moves(services: CoreServices, payload: dict[str, Any]) -> dict[str, Any]:
    """Read exact sandboxed source and capture; never invoke AutoCAD mutation."""
    source = services.files.resolve_path(payload["path"])
    if source.suffix.casefold() != ".dwg":
        raise AutoCADContextError("Component preview source must be a DWG file.")
    content = services.files.read_bytes(source, maximum_bytes=2_097_152)
    if not content.startswith(b"AC10"):
        raise AutoCADContextError("Component preview source has no supported DWG header.")
    digest = hashlib.sha256(content).hexdigest()
    if digest != payload["expected_source_sha256"]:
        raise AutoCADContextError("Component preview source differs from its expected SHA-256.")
    imported = services.autocad.import_drawing_context(
        context_id=payload["context_id"], evidence_sha256=payload["evidence_sha256"], confirmed=True,
    )
    if hashlib.sha256(services.files.read_bytes(source, maximum_bytes=2_097_152)).hexdigest() != digest:
        raise AutoCADContextError("Component preview source changed during context import.")
    return create_component_move_preview(
        imported, source_sha256=digest, source_name=source.name,
        moves=tuple(ComponentMoveTarget(move["object_id"], tuple(move["target_mm"])) for move in payload["moves"]),
    )


def _autocad_live_operator_payload(payload: Mapping[str, Any]) -> dict[str, Any]:
    """Validate one exact live read-only AutoCAD channel request.

    Deliberately no action or arbitrary command-string field is accepted.
    """
    value = _require_keys(payload, set(), {"drawing_name", "timeout_seconds"})
    drawing_name = value.get("drawing_name")
    if drawing_name is not None:
        if (
            not isinstance(drawing_name, str)
            or not drawing_name
            or drawing_name != Path(drawing_name).name
            or not drawing_name.casefold().endswith(".dwg")
            or len(drawing_name.encode("utf-8")) > 255
        ):
            raise ValueError("drawing_name must be one exact bounded DWG file name")
    timeout_seconds = value.get("timeout_seconds", 30.0)
    if (
        not isinstance(timeout_seconds, (int, float))
        or isinstance(timeout_seconds, bool)
        or not math.isfinite(float(timeout_seconds))
        or not 1 <= float(timeout_seconds) <= 120
    ):
        raise ValueError("timeout_seconds must be from 1 through 120")
    value.update(
        {
            "drawing_name": drawing_name,
            "timeout_seconds": float(timeout_seconds),
        }
    )
    return value


def _autocad_drawing_summary_payload(payload: Mapping[str, Any]) -> dict[str, Any]:
    value = _require_keys(payload, {"index_id", "evidence_sha256"})
    index_id = value["index_id"]
    digest = value["evidence_sha256"]
    if not isinstance(index_id, str) or re.fullmatch(r"[0-9a-f]{32}", index_id) is None:
        raise ValueError("index_id must be a lowercase 32-character identifier")
    if not isinstance(digest, str) or re.fullmatch(r"[0-9a-f]{64}", digest) is None:
        raise ValueError("evidence_sha256 must be a lowercase SHA-256 digest")
    return value


def _autocad_handoff_new_payload(payload: Mapping[str, Any]) -> dict[str, Any]:
    value = _require_keys(payload, {"run_id", "output_sha256", "destination"})
    _validate_handoff_identity(value)
    return value


def _autocad_handoff_replace_payload(payload: Mapping[str, Any]) -> dict[str, Any]:
    value = _require_keys(
        payload,
        {
            "run_id",
            "output_sha256",
            "destination",
            "expected_destination_sha256",
        },
    )
    _validate_handoff_identity(value)
    digest = value["expected_destination_sha256"]
    if not isinstance(digest, str) or re.fullmatch(r"[0-9a-f]{64}", digest) is None:
        raise ValueError(
            "expected_destination_sha256 must be a lowercase SHA-256 digest"
        )
    return value


def _validate_handoff_identity(value: Mapping[str, Any]) -> None:
    run_id = value["run_id"]
    digest = value["output_sha256"]
    destination = value["destination"]
    if not isinstance(run_id, str) or re.fullmatch(r"[0-9a-f]{32}", run_id) is None:
        raise ValueError("run_id must be a lowercase 32-character identifier")
    if not isinstance(digest, str) or re.fullmatch(r"[0-9a-f]{64}", digest) is None:
        raise ValueError("output_sha256 must be a lowercase SHA-256 digest")
    if not isinstance(destination, str) or not destination:
        raise ValueError("destination must be a non-empty string")


def _autocad_wire_route_payload(payload: Mapping[str, Any]) -> dict[str, Any]:
    value = _require_keys(payload, {"path", "points"})
    _path_payload({"path": value["path"]})
    raw_points = value["points"]
    if not isinstance(raw_points, list) or not 2 <= len(raw_points) <= 16:
        raise ValueError("points must contain 2 through 16 three-number arrays")
    points: list[tuple[float, float, float]] = []
    for point in raw_points:
        if not isinstance(point, list) or len(point) != 3:
            raise ValueError("each route point must be a three-number array")
        coordinates: list[float] = []
        for coordinate in point:
            if (
                not isinstance(coordinate, (int, float))
                or isinstance(coordinate, bool)
                or not math.isfinite(coordinate)
                or abs(coordinate) > 1_000_000_000
            ):
                raise ValueError(
                    "route coordinates must be finite numbers within bounds"
                )
            normalized = round(float(coordinate), 9)
            coordinates.append(0.0 if normalized == 0 else normalized)
        points.append((coordinates[0], coordinates[1], coordinates[2]))
    if any(first == second for first, second in zip(points, points[1:])):
        raise ValueError("adjacent route points must be different")
    value["points"] = tuple(points)
    return value


def _autocad_circuit_fragment_payload(
    payload: Mapping[str, Any],
) -> dict[str, Any]:
    value = _require_keys(
        payload,
        {"path", "symbol_id", "definition_sha256", "placements", "wire_points"},
    )
    raw_placements = value["placements"]
    if not isinstance(raw_placements, list) or not 2 <= len(raw_placements) <= 8:
        raise ValueError("placements must contain 2 through 8 bounded transforms")
    placements: list[dict[str, object]] = []
    for raw in raw_placements:
        if not isinstance(raw, Mapping):
            raise ValueError("each placement must be an object")
        item = _require_keys(raw, {"point"}, {"rotation_degrees", "scale"})
        validated = _autocad_symbol_payload(
            {
                "path": value["path"],
                "symbol_id": value["symbol_id"],
                "definition_sha256": value["definition_sha256"],
                "insertion_point": item["point"],
                "rotation_degrees": item.get("rotation_degrees", 0.0),
                "scale": item.get("scale", 1.0),
            }
        )
        placements.append(
            {
                "point": validated["insertion_point"],
                "rotation_degrees": validated["rotation_degrees"],
                "scale": validated["scale"],
            }
        )
    route = _autocad_wire_route_payload(
        {"path": value["path"], "points": value["wire_points"]}
    )
    value.update(
        {
            "placements": tuple(placements),
            "wire_points": route["points"],
        }
    )
    return value


def _autocad_panel_sld_fixture_payload(
    payload: Mapping[str, Any],
) -> dict[str, Any]:
    value = _require_keys(payload, {"path", "expected_source_sha256"})
    _path_payload({"path": value["path"]})
    digest = value["expected_source_sha256"]
    if not isinstance(digest, str) or re.fullmatch(r"[0-9a-f]{64}", digest) is None:
        raise ValueError("expected_source_sha256 must be a lowercase SHA-256 digest")
    return value


def _autocad_maximum_performance_fixture_payload(
    payload: Mapping[str, Any],
) -> dict[str, Any]:
    value = _require_keys(payload, {"path", "expected_source_sha256"})
    _path_payload({"path": value["path"]})
    digest = value["expected_source_sha256"]
    if not isinstance(digest, str) or re.fullmatch(r"[0-9a-f]{64}", digest) is None:
        raise ValueError("expected_source_sha256 must be a lowercase SHA-256 digest")
    return value


def _autocad_symbol_payload(payload: Mapping[str, Any]) -> dict[str, Any]:
    value = _require_keys(
        payload,
        {"path", "symbol_id", "definition_sha256", "insertion_point"},
        {"rotation_degrees", "scale"},
    )
    _path_payload({"path": value["path"]})
    if (
        not isinstance(value["symbol_id"], str)
        or re.fullmatch(r"[a-z][a-z0-9_]{0,47}", value["symbol_id"]) is None
    ):
        raise ValueError("symbol_id must be a bounded lowercase identifier")
    if (
        not isinstance(value["definition_sha256"], str)
        or re.fullmatch(r"[0-9a-f]{64}", value["definition_sha256"]) is None
    ):
        raise ValueError("definition_sha256 must be a lowercase SHA-256 digest")
    raw_point = value["insertion_point"]
    if not isinstance(raw_point, list) or len(raw_point) != 3:
        raise ValueError("insertion_point must be a three-number array")
    coordinates: list[float] = []
    for coordinate in raw_point:
        if (
            not isinstance(coordinate, (int, float))
            or isinstance(coordinate, bool)
            or not math.isfinite(coordinate)
            or abs(coordinate) > 1_000_000_000
        ):
            raise ValueError(
                "insertion_point coordinates must be finite numbers within bounds"
            )
        normalized = round(float(coordinate), 9)
        coordinates.append(0.0 if normalized == 0 else normalized)
    rotation = value.get("rotation_degrees", 0.0)
    if (
        not isinstance(rotation, (int, float))
        or isinstance(rotation, bool)
        or not math.isfinite(rotation)
        or not -360 <= rotation <= 360
    ):
        raise ValueError("rotation_degrees must be finite and within -360 through 360")
    scale = value.get("scale", 1.0)
    if (
        not isinstance(scale, (int, float))
        or isinstance(scale, bool)
        or not math.isfinite(scale)
        or not 0.001 <= scale <= 1000
    ):
        raise ValueError("scale must be finite and within 0.001 through 1000")
    normalized_rotation = round(float(rotation), 9)
    value.update(
        {
            "insertion_point": (coordinates[0], coordinates[1], coordinates[2]),
            "rotation_degrees": (
                0.0 if normalized_rotation == 0 else normalized_rotation
            ),
            "scale": round(float(scale), 9),
        }
    )
    return value


def _autocad_symbol_review_payload(payload: Mapping[str, Any]) -> dict[str, Any]:
    value = _require_keys(
        payload,
        {
            "path",
            "block_name",
            "symbol_id",
            "symbol_version",
            "insertion_layer",
            "layer_color",
        },
    )
    _path_payload({"path": value["path"]})
    if (
        not isinstance(value["block_name"], str)
        or re.fullmatch(r"[A-Z][A-Z0-9_-]{0,63}", value["block_name"]) is None
    ):
        raise ValueError("block_name must be a bounded uppercase safe name")
    if (
        not isinstance(value["symbol_id"], str)
        or re.fullmatch(r"[a-z][a-z0-9_]{0,47}", value["symbol_id"]) is None
    ):
        raise ValueError("symbol_id must be a bounded lowercase identifier")
    if (
        not isinstance(value["symbol_version"], int)
        or isinstance(value["symbol_version"], bool)
        or not 1 <= value["symbol_version"] <= 9999
    ):
        raise ValueError("symbol_version must be an integer from 1 through 9999")
    if (
        not isinstance(value["insertion_layer"], str)
        or re.fullmatch(
            r"E-[A-Z0-9]{4}(?:-[A-Z0-9]{4})?", value["insertion_layer"]
        )
        is None
    ):
        raise ValueError("insertion_layer must use the bounded electrical layer format")
    if (
        not isinstance(value["layer_color"], int)
        or isinstance(value["layer_color"], bool)
        or not 1 <= value["layer_color"] <= 255
    ):
        raise ValueError("layer_color must be an ACI integer from 1 through 255")
    return value


def _autocad_symbol_registration_payload(
    payload: Mapping[str, Any],
) -> dict[str, Any]:
    value = _require_keys(
        payload,
        {"review_run_id", "proposal_sha256", "source_sha256"},
    )
    if (
        not isinstance(value["review_run_id"], str)
        or re.fullmatch(r"[0-9a-f]{32}", value["review_run_id"]) is None
    ):
        raise ValueError("review_run_id must be a lowercase 32-character identifier")
    for name in ("proposal_sha256", "source_sha256"):
        if (
            not isinstance(value[name], str)
            or re.fullmatch(r"[0-9a-f]{64}", value[name]) is None
        ):
            raise ValueError(f"{name} must be a lowercase SHA-256 digest")
    return value


def _git_diff_payload(payload: Mapping[str, Any]) -> dict[str, Any]:
    value = _require_keys(payload, {"repository"}, {"cached", "path"})
    _repository_payload({"repository": value["repository"]})
    cached = value.get("cached", False)
    path = value.get("path")
    if not isinstance(cached, bool):
        raise ValueError("cached must be a boolean")
    if path is not None and (not isinstance(path, str) or not path):
        raise ValueError("path must be null or a non-empty string")
    value.update({"cached": cached, "path": path})
    return value


def _git_log_payload(payload: Mapping[str, Any]) -> dict[str, Any]:
    value = _require_keys(payload, {"repository"}, {"limit"})
    _repository_payload({"repository": value["repository"]})
    limit = value.get("limit", 10)
    if not isinstance(limit, int) or isinstance(limit, bool) or not 1 <= limit <= 100:
        raise ValueError("limit must be an integer from 1 through 100")
    value["limit"] = limit
    return value


def _operation_result(result: Any) -> dict[str, Any]:
    return {
        "action": result.action,
        "path": str(result.path),
        "backup_path": str(result.backup_path) if result.backup_path else None,
        "bytes_written": result.bytes_written,
    }


def _git_status(services: CoreServices, payload: dict[str, Any]) -> dict[str, Any]:
    status = GitManager(services.files, payload["repository"]).status()
    return {
        "repository_root": str(status.repository_root),
        "branch": status.branch,
        "clean": status.clean,
        "entries": [asdict(item) for item in status.entries],
    }


def _process_list(services: CoreServices) -> dict[str, Any]:
    result = services.processes.list_processes()
    return {
        "processes": [item.to_dict() for item in result.processes],
        "skipped_count": result.skipped_count,
        "truncated": result.truncated,
    }


def _execute_live_operator_action(
    services: CoreServices,
    payload: dict[str, Any],
    context: CommandContext,
    *,
    requested_action: str,
) -> dict[str, Any]:
    """Round-trip one fixed no-mutation action through the AutoCAD plugin.

    Local paths and drawing contents are omitted from the provider observation.
    """
    root = services.operator_workspace_root.resolve(strict=True)
    active = _select_live_operator_document(
        discover_active_documents(root), drawing_name=payload["drawing_name"]
    )
    queued = enqueue_operator_command(
        root,
        document_id=active.document_id,
        source_drawing_sha256=active.source_drawing_sha256,
        requested_action=requested_action,
    )
    deadline = time.monotonic() + payload["timeout_seconds"]
    while True:
        if context.cancellation_event.is_set():
            cancel_operator_command(root, queued=queued)
            context.raise_if_cancelled()
        context.raise_if_expired()
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise AutoCADContextError(
                "AutoCAD live read-only action did not complete before timeout."
            )
        try:
            result, _result_path, result_sha256 = wait_for_operator_result(
                root,
                queued=queued,
                timeout_seconds=min(0.5, remaining),
            )
            break
        except AutoCADContextError as exc:
            if "did not arrive before timeout" not in str(exc):
                raise
    if result.status != "completed":
        detail = result.error or result.status
        raise AutoCADContextError(
            f"AutoCAD live read-only action did not complete: {detail}"
        )
    return {
        "requested_action": result.requested_action,
        "status": result.status,
        "document_id": result.document_id,
        "drawing_name": result.drawing_name,
        "source_drawing_sha256": result.source_drawing_sha256,
        "database_modified_before": result.database_modified_before,
        "database_modified_after": result.database_modified_after,
        "context_id": result.context_id,
        "context_sha256": result.context_sha256,
        "entity_count": result.entity_count,
        "duration_milliseconds": result.duration_milliseconds,
        "result_sha256": result_sha256,
        "drawing_mutation_performed": False,
    }


def _select_live_operator_document(
    documents: tuple[ActiveAutoCADDocument, ...], *, drawing_name: str | None
) -> ActiveAutoCADDocument:
    matches = documents
    if drawing_name is not None:
        expected = drawing_name.casefold()
        matches = tuple(
            document
            for document in documents
            if document.drawing_name.casefold() == expected
        )
    if len(matches) != 1:
        detail = "no matching" if not matches else "multiple matching"
        raise AutoCADContextError(
            f"AutoCAD live channel found {detail} active documents."
        )
    return matches[0]


def _python_compileall_payload(payload: Mapping[str, Any]) -> dict[str, Any]:
    value = _require_keys(payload, {"path"}, {"quiet"})
    _path_payload({"path": value["path"]})
    quiet = value.get("quiet", True)
    if not isinstance(quiet, bool):
        raise ValueError("quiet must be a boolean")
    value["quiet"] = quiet
    return value


def _python_pytest_payload(payload: Mapping[str, Any]) -> dict[str, Any]:
    value = _require_keys(payload, {"cwd"}, {"targets", "quiet", "timeout_seconds"})
    _path_payload({"path": value["cwd"]})
    targets = value.get("targets", [])
    if not isinstance(targets, list) or len(targets) > 32:
        raise ValueError("targets must be an array of at most 32 workspace paths")
    parsed: list[str] = []
    for target in targets:
        if not isinstance(target, str) or not target or target.startswith("-"):
            raise ValueError("pytest targets must be non-empty paths without option flags")
        if "::" in target:
            path_part, *selectors = target.split("::")
            if not path_part or any(not item or len(item) > 256 for item in selectors):
                raise ValueError("pytest node selectors are invalid")
            _path_payload({"path": path_part})
        else:
            _path_payload({"path": target})
        parsed.append(target)
    quiet = value.get("quiet", True)
    if not isinstance(quiet, bool):
        raise ValueError("quiet must be a boolean")
    timeout = value.get("timeout_seconds", 300)
    if (
        not isinstance(timeout, (int, float))
        or isinstance(timeout, bool)
        or not 1 <= float(timeout) <= 300
    ):
        raise ValueError("timeout_seconds must be from 1 through 300")
    value.update({"targets": tuple(parsed), "quiet": quiet, "timeout_seconds": float(timeout)})
    return value


def _python_compileall(services: CoreServices, payload: dict[str, Any]) -> dict[str, Any]:
    target = services.files.resolve_path(payload["path"])
    command = [sys.executable, "-m", "compileall"]
    if payload["quiet"]:
        command.append("-q")
    command.append(str(target))
    result = services.terminal.execute(command, cwd=str(target if target.is_dir() else target.parent))
    return _successful_command_result(result, "Python compilation")


def _python_pytest(services: CoreServices, payload: dict[str, Any]) -> dict[str, Any]:
    cwd = services.files.resolve_path(payload["cwd"])
    temporary_root = Path(tempfile.mkdtemp(prefix=".ai-engineer-pytest-", dir=cwd))
    try:
        command = [
            sys.executable,
            "-m",
            "pytest",
            "-p",
            "no:cacheprovider",
            "--basetemp",
            str(temporary_root),
        ]
        if payload["quiet"]:
            command.append("-q")
        command.extend(payload["targets"])
        result = services.terminal.execute(
            command,
            cwd=str(cwd),
            timeout_seconds=payload["timeout_seconds"],
            confirmed=True,
        )
        return _successful_command_result(result, "Pytest")
    finally:
        shutil.rmtree(temporary_root, onexc=_remove_readonly_pytest_temp)


def _command_result(result: Any) -> dict[str, Any]:
    return {
        "command": list(result.command),
        "cwd": str(result.cwd),
        "return_code": result.return_code,
        "stdout": result.stdout,
        "stderr": result.stderr,
        "duration_seconds": result.duration_seconds,
        "timed_out": result.timed_out,
        "stdout_truncated": result.stdout_truncated,
        "stderr_truncated": result.stderr_truncated,
        "succeeded": result.succeeded,
    }


def _successful_command_result(result: Any, label: str) -> dict[str, Any]:
    """Return bounded process evidence or fail the task on a non-zero/timed-out run."""
    value = _command_result(result)
    if result.succeeded:
        return value
    if result.timed_out:
        raise CommandExecutionError(f"{label} timed out before completion.")
    raise CommandExecutionError(f"{label} failed with exit code {result.return_code}.")


def _remove_readonly_pytest_temp(
    function: Callable[[str], Any], path: str, error: BaseException
) -> None:
    """Retry removal of Git-created read-only pytest artifacts on Windows only."""
    if not isinstance(error, PermissionError):
        raise error
    Path(path).chmod(stat.S_IWRITE)
    function(path)
