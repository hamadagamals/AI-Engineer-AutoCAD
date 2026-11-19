"""Tests for portable packaging, installation, recovery, migration, and launcher."""

from __future__ import annotations

import hashlib
import json
import subprocess
import sys
import zipfile
from datetime import UTC, datetime
from pathlib import Path

import pytest

import launcher
from agent.config import AppConfig
from agent.exceptions import MigrationError, PackagingError, RecoveryError
from agent.state import StateStore
from agent.version import __version__
from core.package_manager import (
    MigrationManager,
    PackageBuilder,
    PackageInstaller,
    RecoveryManager,
)
from core.autocad_release_readiness import AUTOCAD_PRODUCT_FILES


PROJECT_ROOT = Path(__file__).resolve().parents[1]
FIXED_TIME = datetime(2026, 8, 6, 8, 0, tzinfo=UTC)


def _runtime_project(tmp_path: Path, *, complete: bool = True) -> Path:
    data = tmp_path / "data"
    data.mkdir(parents=True)
    documents = {
        "settings.json": {"schema_version": 1, "log_level": "INFO"},
        "mission.json": {
            "schema_version": 1,
            "project": "AI-Engineer",
            "mission": "Safe local agent",
            "current_milestone": 1,
            "status": "legacy",
        },
        "agent_state.json": {
            "schema_version": 1,
            "application_version": "0.1.0",
            "status": "stopped",
            "start_count": 2,
        },
    }
    if complete:
        documents.update(
            {
                "approvals.json": {"schema_version": 1, "approvals": []},
                "developer_runs.json": {
                    "schema_version": 1,
                    "runs": [],
                    "events": [],
                },
                "drafting_runs.json": {"schema_version": 1, "runs": []},
                "history.json": {"schema_version": 1, "tasks": [], "events": []},
            }
        )
    for name, document in documents.items():
        (data / name).write_text(json.dumps(document), encoding="utf-8")
    return tmp_path


def test_portable_package_has_hash_manifest_and_clean_runtime_templates(
    tmp_path: Path,
) -> None:
    output = tmp_path / "ai-engineer.zip"

    result = PackageBuilder(PROJECT_ROOT, clock=lambda: FIXED_TIME).build(output)

    assert result.archive == output.resolve()
    assert result.version == __version__
    with zipfile.ZipFile(output) as archive:
        names = set(archive.namelist())
        manifest = json.loads(archive.read("package_manifest.json"))
        state = json.loads(archive.read("data/agent_state.json"))
        history = json.loads(archive.read("data/history.json"))
        drafting = json.loads(archive.read("data/drafting_runs.json"))
        for name, expected_hash in manifest["files"].items():
            assert hashlib.sha256(archive.read(name)).hexdigest() == expected_hash
    assert manifest["package_type"] == "ai-engineer-portable"
    assert manifest["python_requires"] == ">=3.13,<3.14"
    assert state["start_count"] == 0
    assert state["application_version"] == __version__
    assert history["tasks"] == []
    assert drafting == {"schema_version": 1, "runs": []}
    assert "launcher.py" in names
    assert "start-operator.ps1" in names
    assert "START-AI-ENGINEER-PROVIDER.ps1" in names
    assert "START-AI-ENGINEER-LOCAL.bat" in names
    assert "START-AI-ENGINEER-CODEX.bat" in names
    assert "START-AI-ENGINEER-HYBRID.bat" in names
    assert "START-AI-ENGINEER-AUTOCAD-PREVIEW.ps1" in names
    assert "START-AI-ENGINEER-AUTOCAD-COMPLIANCE.ps1" in names
    assert "START-AI-ENGINEER-AUTOCAD-MANUFACTURER.ps1" in names
    assert "START-AI-ENGINEER-AUTOCAD-DISCOVERY.ps1" in names
    assert "integrations/ollama_provider.py" in names
    assert "integrations/codex_provider.py" in names
    assert "integrations/hybrid_provider.py" in names
    assert "integrations/provider_contracts.py" in names
    assert "integrations/gemini_mcp_server.py" in names
    assert "integrations/install_gemini_mcp.ps1" in names
    assert "GEMINI.md" in names
    assert "integrations/manufacturer_templates/free-candidates.template.json" in names
    assert not any(
        name.startswith("docs/evidence/autocad-beta-package-candidate-")
        for name in names
    )
    assert "clients/vscode/package.json" in names
    assert "clients/vscode/extension.js" in names
    assert "__main__.py" in names
    assert "ai-engineer.cmd" in names
    assert "agent/copilot.py" in names
    assert "agent/copilot_service.py" in names
    assert "agent/copilot_provider.py" in names
    assert "agent/drafting_service.py" in names
    assert "agent/drafting_planner.py" in names
    assert "core/electrical_drafting.py" in names
    assert "core/electrical_drafting_validation.py" in names
    assert "core/quantity_takeoff.py" in names
    assert "core/quantity_export.py" in names
    assert "core/low_voltage_calculations.py" in names
    assert "core/load_aggregation.py" in names
    assert "core/cable_ampacity.py" in names
    assert "core/short_circuit.py" in names
    assert "core/candidate_adequacy.py" in names
    assert "core/protective_conductor.py" in names
    assert "core/panel_sld.py" in names
    assert "core/panel_schedule.py" in names
    assert "core/panel_sld_extraction.py" in names
    assert "core/electrical_model.py" in names
    assert "integrations/autocad_context.py" in names
    assert "integrations/autocad_context_plugin/ContextCommands.cs" in names
    assert "integrations/autocad_context_plugin/DrawingChangeTracker.cs" in names
    assert "integrations/autocad_context_plugin/OperatorCommandChannel.cs" in names
    assert "integrations/autocad_context_plugin/PaletteCommands.cs" in names
    assert "integrations/autocad_context_plugin/build.ps1" in names
    assert "integrations/autocad_context_plugin/live_palette.scr" in names
    assert "integrations/autocad_drawing_delta.py" in names
    assert "integrations/autocad_drawing_index.py" in names
    assert "integrations/autocad_drawing_inventory.py" in names
    assert "integrations/autocad_inventory_delta.py" in names
    assert "integrations/autocad_inventory_refresh.py" in names
    assert "tests/test_api.py" not in names
    assert not any(name.startswith(("logs/", "workspace/", "backup/", ".git/")) for name in names)


def test_package_build_refuses_overwrite(tmp_path: Path) -> None:
    output = tmp_path / "existing.zip"
    output.write_bytes(b"keep")

    with pytest.raises(PackagingError, match="already exists"):
        PackageBuilder(PROJECT_ROOT).build(output)

    assert output.read_bytes() == b"keep"


def test_installer_requires_confirmation_and_new_destination(tmp_path: Path) -> None:
    archive = PackageBuilder(PROJECT_ROOT).build(tmp_path / "package.zip").archive
    destination = tmp_path / "installed"
    installer = PackageInstaller()

    with pytest.raises(PackagingError, match="confirmation"):
        installer.install(archive, destination)
    result = installer.install(archive, destination, confirmed=True)

    assert result.destination == destination.resolve()
    assert (destination / "launcher.py").is_file()
    assert (destination / "package_manifest.json").is_file()
    assert (destination / "workspace").is_dir()
    assert (destination / "logs").is_dir()
    assert (destination / "backup").is_dir()
    with pytest.raises(PackagingError, match="must not already exist"):
        installer.install(archive, destination, confirmed=True)


def test_installer_rejects_hash_tampering_and_path_traversal(tmp_path: Path) -> None:
    original = PackageBuilder(PROJECT_ROOT).build(tmp_path / "original.zip").archive
    tampered = tmp_path / "tampered.zip"
    with zipfile.ZipFile(original) as source, zipfile.ZipFile(tampered, "w") as target:
        for info in source.infolist():
            content = source.read(info.filename)
            if info.filename == "README.md":
                content = b"tampered"
            target.writestr(info, content)
    with pytest.raises(PackagingError, match="hash mismatch"):
        PackageInstaller().install(tampered, tmp_path / "bad", confirmed=True)

    traversal = tmp_path / "traversal.zip"
    with zipfile.ZipFile(traversal, "w") as archive:
        archive.writestr("../outside.txt", "escape")
        archive.writestr(
            "package_manifest.json",
            json.dumps(
                {
                    "schema_version": 1,
                    "package_type": "ai-engineer-portable",
                    "application_version": __version__,
                    "files": {"../outside.txt": hashlib.sha256(b"escape").hexdigest()},
                }
            ),
        )
    with pytest.raises(PackagingError, match="unsafe"):
        PackageInstaller().install(traversal, tmp_path / "escape", confirmed=True)
    assert not (tmp_path / "outside.txt").exists()


def test_installer_rolls_back_a_failed_new_destination(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    archive = PackageBuilder(PROJECT_ROOT).build(tmp_path / "package.zip").archive
    destination = tmp_path / "failed-install"
    original = Path.mkdir

    def fail_runtime_directory(path: Path, *args: object, **kwargs: object) -> None:
        if path.name == "logs" and path.parent == destination:
            raise OSError("simulated runtime-directory failure")
        original(path, *args, **kwargs)

    monkeypatch.setattr(Path, "mkdir", fail_runtime_directory)

    with pytest.raises(PackagingError, match="Cannot install portable package"):
        PackageInstaller().install(archive, destination, confirmed=True)

    assert not destination.exists()


def test_recovery_snapshot_restore_backs_up_and_restores_atomically(
    tmp_path: Path,
) -> None:
    root = _runtime_project(tmp_path / "project")
    config = AppConfig.load(root)
    manager = RecoveryManager(config, clock=lambda: FIXED_TIME)
    archive = manager.create_snapshot(tmp_path / "recovery.zip").archive
    StateStore(config.state_file).save(
        {
            "schema_version": 1,
            "application_version": __version__,
            "status": "stopped",
            "start_count": 99,
        }
    )

    with pytest.raises(RecoveryError, match="confirmation"):
        manager.restore_snapshot(archive)
    restored = manager.restore_snapshot(archive, confirmed=True)

    assert StateStore(config.state_file).load()["start_count"] == 2
    assert restored.backup_directory is not None
    backed_up = StateStore(restored.backup_directory / "agent_state.json").load()
    assert backed_up["start_count"] == 99


def test_recovery_rejects_secret_like_keys_and_corrupt_archive(tmp_path: Path) -> None:
    root = _runtime_project(tmp_path / "secret")
    config = AppConfig.load(root)
    settings = StateStore(config.data_dir / "settings.json").load()
    settings["api_token"] = "must-not-snapshot"
    StateStore(config.data_dir / "settings.json").save(settings)
    with pytest.raises(RecoveryError, match="secret-like"):
        RecoveryManager(config).create_snapshot(tmp_path / "secret.zip")

    clean_root = _runtime_project(tmp_path / "clean")
    clean_config = AppConfig.load(clean_root)
    archive = RecoveryManager(clean_config).create_snapshot(tmp_path / "clean.zip").archive
    corrupt = tmp_path / "corrupt.zip"
    corrupt.write_bytes(archive.read_bytes()[:-10])
    before = clean_config.state_file.read_bytes()
    with pytest.raises(RecoveryError):
        RecoveryManager(clean_config).restore_snapshot(corrupt, confirmed=True)
    assert clean_config.state_file.read_bytes() == before


def test_migration_upgrades_legacy_layout_with_recovery_and_is_idempotent(
    tmp_path: Path,
) -> None:
    root = _runtime_project(tmp_path / "legacy", complete=False)
    config = AppConfig.load(root)
    manager = MigrationManager(config, clock=lambda: FIXED_TIME)

    with pytest.raises(MigrationError, match="requires confirmation"):
        manager.migrate()
    result = manager.migrate(confirmed=True)

    assert set(result.changed_files) == {
        "agent_state.json",
        "approvals.json",
        "developer_runs.json",
        "drafting_runs.json",
        "history.json",
    }
    assert result.recovery_archive is not None
    assert result.recovery_archive.is_file()
    assert StateStore(config.state_file).load()["application_version"] == __version__
    assert StateStore(config.history_file).load()["tasks"] == []
    assert manager.migrate().changed_files == ()


def test_launcher_check_and_package_commands_are_fixed_and_machine_readable(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    assert launcher.main(["check"]) == 0
    checked = json.loads(capsys.readouterr().out)
    assert checked["status"] == "ok"
    assert checked["autocad_product_files"] == len(AUTOCAD_PRODUCT_FILES)
    output = tmp_path / "launcher-package.zip"

    assert launcher.main(["package", "--output", str(output)]) == 0

    packaged = json.loads(capsys.readouterr().out)
    assert Path(packaged["archive"]) == output.resolve()
    assert output.is_file()


def test_portable_zip_self_installs_without_an_existing_checkout(tmp_path: Path) -> None:
    archive = PackageBuilder(PROJECT_ROOT).build(tmp_path / "portable.zip").archive
    destination = tmp_path / "from-zip"

    completed = subprocess.run(
        [
            sys.executable,
            str(archive),
            "install",
            str(archive),
            str(destination),
            "--confirm",
        ],
        stdin=subprocess.DEVNULL,
        capture_output=True,
        text=True,
        encoding="utf-8",
        timeout=30,
        check=False,
    )

    assert completed.returncode == 0, completed.stderr
    result = json.loads(completed.stdout)
    assert result["version"] == __version__
    assert (destination / "launcher.py").is_file()
