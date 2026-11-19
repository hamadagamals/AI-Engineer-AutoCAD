"""Tests for the Milestone 0 application foundation."""

from __future__ import annotations

import json
from datetime import UTC, datetime
from pathlib import Path

import pytest

from agent.app import AppStatus, Application
from agent.config import AppConfig
from agent.exceptions import (
    CommandExecutionError,
    ConfigurationError,
    LifecycleError,
    StateError,
)
from agent.logger import configure_logging
from agent.state import StateStore


def create_project(tmp_path: Path, *, log_level: str = "INFO") -> Path:
    """Create the minimum valid project data used by foundation tests."""
    data_dir = tmp_path / "data"
    data_dir.mkdir()
    (data_dir / "settings.json").write_text(
        json.dumps({"schema_version": 1, "log_level": log_level}), encoding="utf-8"
    )
    (data_dir / "agent_state.json").write_text(
        json.dumps({"schema_version": 1, "status": "stopped", "start_count": 0}),
        encoding="utf-8",
    )
    return tmp_path


def test_config_resolves_paths_from_supplied_root(tmp_path: Path) -> None:
    root = create_project(tmp_path)
    config = AppConfig.load(root)

    assert config.project_root == root.resolve()
    assert config.state_file == root.resolve() / "data" / "agent_state.json"
    assert config.log_file == root.resolve() / "logs" / "ai-engineer.jsonl"


@pytest.mark.parametrize("value", ["verbose", 10, None])
def test_config_rejects_invalid_log_level(tmp_path: Path, value: object) -> None:
    root = create_project(tmp_path)
    (root / "data" / "settings.json").write_text(
        json.dumps({"log_level": value}), encoding="utf-8"
    )

    with pytest.raises(ConfigurationError, match="log_level"):
        AppConfig.load(root)


def test_state_store_round_trip_replaces_existing_json(tmp_path: Path) -> None:
    path = tmp_path / "state.json"
    store = StateStore(path)

    store.save({"status": "running", "count": 1})
    store.save({"status": "stopped", "count": 2})

    assert store.load() == {"status": "stopped", "count": 2}
    assert not list(tmp_path.glob("*.tmp"))


def test_state_store_rejects_invalid_json(tmp_path: Path) -> None:
    path = tmp_path / "state.json"
    path.write_text("not json", encoding="utf-8")

    with pytest.raises(StateError, match="valid JSON"):
        StateStore(path).load()


def test_json_logger_writes_structured_context(tmp_path: Path) -> None:
    path = tmp_path / "app.jsonl"
    logger = configure_logging(path)

    logger.info("test_event", extra={"request_id": "abc-123"})
    for handler in logger.handlers:
        handler.flush()

    record = json.loads(path.read_text(encoding="utf-8").strip())
    assert record["level"] == "INFO"
    assert record["message"] == "test_event"
    assert record["context"]["request_id"] == "abc-123"


def test_application_lifecycle_persists_state_and_logs(tmp_path: Path) -> None:
    root = create_project(tmp_path)
    moments = iter(
        [
            datetime(2026, 8, 5, 10, 0, tzinfo=UTC),
            datetime(2026, 8, 5, 10, 1, tzinfo=UTC),
        ]
    )
    application = Application(root, clock=lambda: next(moments))

    started = application.start()
    stopped = application.stop()

    assert started["status"] == "running"
    assert started["start_count"] == 1
    assert stopped["status"] == "stopped"
    assert application.status is AppStatus.STOPPED
    log_messages = [
        json.loads(line)["message"]
        for line in (root / "logs" / "ai-engineer.jsonl").read_text(encoding="utf-8").splitlines()
    ]
    assert log_messages == ["application_started", "application_stopped"]


def test_application_rejects_invalid_transition(tmp_path: Path) -> None:
    application = Application(create_project(tmp_path))

    with pytest.raises(LifecycleError, match="Cannot stop"):
        application.stop()


def test_application_initializes_safe_core_command_catalog(tmp_path: Path) -> None:
    root = create_project(tmp_path)
    application = Application(root)
    application.start()
    try:
        assert application.command_registry is not None
        assert application.task_engine is not None
        assert application.approvals is not None
        names = {
            definition.name
            for definition in application.command_registry.list_definitions()
        }
        assert names == {
            "file.delete",
            "file.read",
            "file.write",
                "autocad.detect",
                "autocad.get_live_document_status",
                "autocad.show_live_palette",
                "autocad.capture_live_drawing_index",
                "autocad.summarize_drawing_index_for_ai",
                "autocad.preview_component_moves",
                "autocad.create_electrical_circuit_fragment_scratch",
            "autocad.create_panel_sld_fixture_scratch",
            "autocad.create_maximum_performance_fixture_scratch",
            "autocad.create_line_scratch",
            "autocad.create_wire_route_scratch",
            "autocad.inspect_scratch",
            "autocad.import_drawing_context",
            "autocad.inventory_scratch",
            "autocad.list_electrical_symbols",
            "autocad.place_electrical_symbol_scratch",
            "autocad.review_electrical_symbol_source",
            "autocad.register_electrical_symbol_review",
            "autocad.handoff_verified_output_new",
            "autocad.replace_with_verified_output",
            "git.diff",
            "git.log",
            "git.status",
            "process.list",
            "process.terminate",
            "project.scan",
            "python.compileall",
            "python.pytest",
            "system.snapshot",
            "vscode.detect",
            "vscode.open",
        }
    finally:
        application.stop()


def test_high_risk_core_file_command_requires_approval(tmp_path: Path) -> None:
    root = create_project(tmp_path)
    application = Application(root)
    application.start()
    try:
        assert application.task_engine is not None
        assert application.approvals is not None
        write_task = application.task_engine.submit(
            "file.write",
            {"path": "generated/result.txt", "content": "verified", "create_parents": True},
        )
        awaiting = application.task_engine.run(write_task.task_id)
        assert awaiting.status.value == "awaiting_approval"
        assert awaiting.approval_id is not None
        application.approvals.decide(awaiting.approval_id, approved=True)

        written = application.task_engine.run(
            awaiting.task_id, approval_id=awaiting.approval_id
        )
        assert written.status.value == "succeeded"
        assert (root / "workspace" / "generated" / "result.txt").read_text(
            encoding="utf-8"
        ) == "verified"

        read_task = application.task_engine.submit(
            "file.read", {"path": "generated/result.txt"}
        )
        read = application.task_engine.run(read_task.task_id)
        assert read.result["content"] == "verified"
    finally:
        application.stop()


def test_vscode_open_command_requires_durable_approval_before_cli(
    tmp_path: Path,
) -> None:
    root = create_project(tmp_path)
    application = Application(root)
    application.start()
    try:
        assert application.task_engine is not None
        task = application.task_engine.submit(
            "vscode.open", {"path": "project", "line": None, "column": None}
        )

        awaiting = application.task_engine.run(task.task_id)

        assert awaiting.status.value == "awaiting_approval"
        assert awaiting.approval_id is not None
    finally:
        application.stop()


def test_autocad_scratch_command_requires_approval_before_copy_or_launch(
    tmp_path: Path,
) -> None:
    root = create_project(tmp_path)
    application = Application(root)
    application.start()
    try:
        assert application.task_engine is not None
        task = application.task_engine.submit(
            "autocad.inspect_scratch", {"path": "drawing.dwg"}
        )

        awaiting = application.task_engine.run(task.task_id)

        assert awaiting.status.value == "awaiting_approval"
        assert awaiting.approval_id is not None
        assert not (root / "workspace" / ".ai-engineer").exists()
    finally:
        application.stop()


def test_autocad_context_import_validates_evidence_and_requires_approval(
    tmp_path: Path,
) -> None:
    root = create_project(tmp_path)
    application = Application(root)
    application.start()
    try:
        assert application.task_engine is not None
        with pytest.raises(CommandExecutionError, match="context_id"):
            application.task_engine.submit(
                "autocad.import_drawing_context",
                {"context_id": "bad", "evidence_sha256": "a" * 64},
            )
        task = application.task_engine.submit(
            "autocad.import_drawing_context",
            {"context_id": "a" * 32, "evidence_sha256": "b" * 64},
        )

        awaiting = application.task_engine.run(task.task_id)

        assert awaiting.status.value == "awaiting_approval"
    finally:
        application.stop()


def test_autocad_inventory_command_requires_approval_before_copy_or_launch(
    tmp_path: Path,
) -> None:
    root = create_project(tmp_path)
    application = Application(root)
    application.start()
    try:
        assert application.task_engine is not None
        task = application.task_engine.submit(
            "autocad.inventory_scratch", {"path": "drawing.dwg"}
        )

        awaiting = application.task_engine.run(task.task_id)

        assert awaiting.status.value == "awaiting_approval"
        assert awaiting.approval_id is not None
        assert not (root / "workspace" / ".ai-engineer").exists()
    finally:
        application.stop()


def test_autocad_line_command_validates_points_and_requires_approval(
    tmp_path: Path,
) -> None:
    root = create_project(tmp_path)
    application = Application(root)
    application.start()
    try:
        assert application.task_engine is not None
        with pytest.raises(CommandExecutionError, match="different"):
            application.task_engine.submit(
                "autocad.create_line_scratch",
                {
                    "path": "drawing.dwg",
                    "start": [0, 0, 0],
                    "end": [0, 0, 0],
                },
            )
        task = application.task_engine.submit(
            "autocad.create_line_scratch",
            {
                "path": "drawing.dwg",
                "start": [0, 0, 0],
                "end": [1000, 0, 0],
            },
        )

        awaiting = application.task_engine.run(task.task_id)

        assert awaiting.status.value == "awaiting_approval"
        assert awaiting.approval_id is not None
        assert not (root / "workspace" / ".ai-engineer").exists()
    finally:
        application.stop()


def test_autocad_wire_route_command_validates_points_and_requires_approval(
    tmp_path: Path,
) -> None:
    root = create_project(tmp_path)
    application = Application(root)
    application.start()
    try:
        assert application.task_engine is not None
        with pytest.raises(CommandExecutionError, match="adjacent"):
            application.task_engine.submit(
                "autocad.create_wire_route_scratch",
                {"path": "drawing.dwg", "points": [[0, 0, 0], [0, 0, 0]]},
            )
        task = application.task_engine.submit(
            "autocad.create_wire_route_scratch",
            {
                "path": "drawing.dwg",
                "points": [[0, 0, 0], [1000, 0, 0], [1000, 500, 0]],
            },
        )
        awaiting = application.task_engine.run(task.task_id)
        assert awaiting.status.value == "awaiting_approval"
        assert awaiting.approval_id is not None
        assert not (root / "workspace" / ".ai-engineer").exists()
    finally:
        application.stop()


def test_autocad_circuit_fragment_command_is_bounded_and_requires_approval(
    tmp_path: Path,
) -> None:
    root = create_project(tmp_path)
    application = Application(root)
    application.start()
    try:
        assert application.task_engine is not None
        base_payload = {
            "path": "drawing.dwg",
            "symbol_id": "socket_single",
            "definition_sha256": (
                "8a2f891627de4c1c5d80670a1aed95a6"
                "f1b0f602ea4bddd2e3dc94232ea82a95"
            ),
            "placements": [{"point": [0, 0, 0]}],
            "wire_points": [[0, 0, 0], [1000, 0, 0]],
        }
        with pytest.raises(CommandExecutionError, match="2 through 8"):
            application.task_engine.submit(
                "autocad.create_electrical_circuit_fragment_scratch",
                base_payload,
            )
        base_payload["placements"] = [
            {"point": [0, 0, 0]},
            {"point": [1000, 0, 0], "rotation_degrees": 90, "scale": 1.5},
        ]
        task = application.task_engine.submit(
            "autocad.create_electrical_circuit_fragment_scratch",
            base_payload,
        )
        awaiting = application.task_engine.run(task.task_id)
        assert awaiting.status.value == "awaiting_approval"
        assert awaiting.approval_id is not None
        assert not (root / "workspace" / ".ai-engineer").exists()
    finally:
        application.stop()


def test_autocad_panel_sld_fixture_requires_exact_hash_and_approval(
    tmp_path: Path,
) -> None:
    root = create_project(tmp_path)
    application = Application(root)
    application.start()
    try:
        assert application.task_engine is not None
        with pytest.raises(CommandExecutionError, match="expected_source_sha256"):
            application.task_engine.submit(
                "autocad.create_panel_sld_fixture_scratch",
                {"path": "drawing.dwg", "expected_source_sha256": "bad"},
            )
        task = application.task_engine.submit(
            "autocad.create_panel_sld_fixture_scratch",
            {"path": "drawing.dwg", "expected_source_sha256": "a" * 64},
        )
        awaiting = application.task_engine.run(task.task_id)
        assert awaiting.status.value == "awaiting_approval"
        assert awaiting.approval_id is not None
        assert not (root / "workspace" / ".ai-engineer").exists()
    finally:
        application.stop()


def test_autocad_maximum_performance_fixture_requires_exact_hash_and_approval(
    tmp_path: Path,
) -> None:
    root = create_project(tmp_path)
    application = Application(root)
    application.start()
    try:
        assert application.task_engine is not None
        with pytest.raises(CommandExecutionError, match="expected_source_sha256"):
            application.task_engine.submit(
                "autocad.create_maximum_performance_fixture_scratch",
                {"path": "drawing.dwg", "expected_source_sha256": "bad"},
            )
        task = application.task_engine.submit(
            "autocad.create_maximum_performance_fixture_scratch",
            {"path": "drawing.dwg", "expected_source_sha256": "a" * 64},
        )
        awaiting = application.task_engine.run(task.task_id)
        assert awaiting.status.value == "awaiting_approval"
        assert awaiting.approval_id is not None
        assert not (root / "workspace" / ".ai-engineer").exists()
    finally:
        application.stop()


def test_autocad_symbol_command_binds_approval_to_definition_hash(
    tmp_path: Path,
) -> None:
    root = create_project(tmp_path)
    application = Application(root)
    application.start()
    try:
        assert application.task_engine is not None
        with pytest.raises(CommandExecutionError, match="SHA-256"):
            application.task_engine.submit(
                "autocad.place_electrical_symbol_scratch",
                {
                    "path": "drawing.dwg",
                    "symbol_id": "socket_single",
                    "definition_sha256": "bad",
                    "insertion_point": [0, 0, 0],
                },
            )
        task = application.task_engine.submit(
            "autocad.place_electrical_symbol_scratch",
            {
                "path": "drawing.dwg",
                "symbol_id": "socket_single",
                "definition_sha256": (
                    "8a2f891627de4c1c5d80670a1aed95a6"
                    "f1b0f602ea4bddd2e3dc94232ea82a95"
                ),
                "insertion_point": [250, 500, 0],
                "rotation_degrees": 90,
                "scale": 1.5,
            },
        )
        awaiting = application.task_engine.run(task.task_id)
        assert awaiting.status.value == "awaiting_approval"
        assert awaiting.approval_id is not None
        assert not (root / "workspace" / ".ai-engineer").exists()
    finally:
        application.stop()


def test_autocad_user_symbol_review_command_requires_durable_approval(
    tmp_path: Path,
) -> None:
    root = create_project(tmp_path)
    application = Application(root)
    application.start()
    try:
        assert application.task_engine is not None
        with pytest.raises(CommandExecutionError, match="block_name"):
            application.task_engine.submit(
                "autocad.review_electrical_symbol_source",
                {
                    "path": "user-symbol.dwg",
                    "block_name": "bad block",
                    "symbol_id": "socket_custom",
                    "symbol_version": 1,
                    "insertion_layer": "E-POWR-DEVC",
                    "layer_color": 2,
                },
            )
        task = application.task_engine.submit(
            "autocad.review_electrical_symbol_source",
            {
                "path": "user-symbol.dwg",
                "block_name": "AIENG_E_SOCKET_CUSTOM_V1",
                "symbol_id": "socket_custom",
                "symbol_version": 1,
                "insertion_layer": "E-POWR-DEVC",
                "layer_color": 2,
            },
        )
        awaiting = application.task_engine.run(task.task_id)
        assert awaiting.status.value == "awaiting_approval"
        assert awaiting.approval_id is not None
        assert not (root / "workspace" / ".ai-engineer").exists()
    finally:
        application.stop()


def test_autocad_symbol_registration_command_requires_separate_approval(
    tmp_path: Path,
) -> None:
    root = create_project(tmp_path)
    application = Application(root)
    application.start()
    try:
        assert application.task_engine is not None
        task = application.task_engine.submit(
            "autocad.register_electrical_symbol_review",
            {
                "review_run_id": "a" * 32,
                "proposal_sha256": "b" * 64,
                "source_sha256": "c" * 64,
            },
        )
        awaiting = application.task_engine.run(task.task_id)
        assert awaiting.status.value == "awaiting_approval"
        assert awaiting.approval_id is not None
    finally:
        application.stop()


def test_autocad_handoff_commands_are_strict_and_separately_approved(
    tmp_path: Path,
) -> None:
    root = create_project(tmp_path)
    application = Application(root)
    application.start()
    try:
        assert application.task_engine is not None
        with pytest.raises(CommandExecutionError, match="Unexpected fields"):
            application.task_engine.submit(
                "autocad.handoff_verified_output_new",
                {
                    "run_id": "a" * 32,
                    "output_sha256": "b" * 64,
                    "destination": "released.dwg",
                    "source": "caller-controlled.dwg",
                },
            )
        with pytest.raises(CommandExecutionError, match="SHA-256"):
            application.task_engine.submit(
                "autocad.replace_with_verified_output",
                {
                    "run_id": "a" * 32,
                    "output_sha256": "b" * 64,
                    "destination": "released.dwg",
                    "expected_destination_sha256": "bad",
                },
            )
        new_task = application.task_engine.submit(
            "autocad.handoff_verified_output_new",
            {
                "run_id": "a" * 32,
                "output_sha256": "b" * 64,
                "destination": "released.dwg",
            },
        )
        replacement_task = application.task_engine.submit(
            "autocad.replace_with_verified_output",
            {
                "run_id": "a" * 32,
                "output_sha256": "b" * 64,
                "destination": "released.dwg",
                "expected_destination_sha256": "c" * 64,
            },
        )
        new_awaiting = application.task_engine.run(new_task.task_id)
        replacement_awaiting = application.task_engine.run(replacement_task.task_id)
        assert new_awaiting.status.value == "awaiting_approval"
        assert replacement_awaiting.status.value == "awaiting_approval"
        assert new_awaiting.approval_id != replacement_awaiting.approval_id
    finally:
        application.stop()
