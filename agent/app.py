"""Application lifecycle for the Milestone 0 local foundation."""

from __future__ import annotations

import logging
import sys
from datetime import UTC, datetime
from enum import Enum
from pathlib import Path
from typing import Any, Callable

from agent.config import AppConfig
from agent.command_engine import CommandEngine, CommandRegistry
from agent.copilot_service import CopilotSessionManager
from agent.copilot_provider import CopilotProviderAdapter
from agent.manufacturer_provider import ManufacturerProviderAdapter
from agent.drafting_service import DraftingLifecycleService
from agent.drafting_planner import DraftingPlanCoordinator
from agent.core_commands import CORE_PERMISSIONS, CoreServices, register_core_commands
from agent.developer_agent import DeveloperAgent
from agent.exceptions import LifecycleError
from agent.logger import configure_logging
from agent.permissions import ApprovalManager, PermissionPolicy
from agent.provider import ProviderAdapter
from agent.state import StateStore
from agent.task_engine import TaskEngine
from agent.version import __version__
from core.file_manager import FileManager
from core.process_manager import ProcessManager
from core.project_scanner import ProjectScanner
from core.system_info import SystemInfo
from core.terminal_manager import CommandPolicy, TerminalManager
from core.verified_lessons import OperationalIncidentJournal, VerifiedLessonRegistry
from integrations.vscode import VSCodeManager
from integrations.autocad import AutoCADManager


class AppStatus(str, Enum):
    """Supported lifecycle states."""

    CREATED = "created"
    RUNNING = "running"
    STOPPED = "stopped"


class Application:
    """Own configuration, persistence, logging, and lifecycle transitions."""

    def __init__(
        self,
        project_root: Path | None = None,
        *,
        clock: Callable[[], datetime] | None = None,
        developer_provider: ProviderAdapter | None = None,
        copilot_provider: CopilotProviderAdapter | None = None,
        manufacturer_provider: ManufacturerProviderAdapter | None = None,
        workspace_roots: tuple[Path, ...] | None = None,
        backup_root: Path | None = None,
    ) -> None:
        self.config = AppConfig.load(project_root)
        self._clock = clock or (lambda: datetime.now(UTC))
        self._developer_provider = developer_provider
        self._copilot_provider = copilot_provider
        self.manufacturer_provider = manufacturer_provider
        self._workspace_roots = workspace_roots
        self._backup_root = backup_root
        self._status = AppStatus.CREATED
        self._logger: logging.Logger | None = None
        self._state_store = StateStore(self.config.state_file)
        self.command_registry: CommandRegistry | None = None
        self.approvals: ApprovalManager | None = None
        self.task_engine: TaskEngine | None = None
        self.developer_agent: DeveloperAgent | None = None
        self.autocad_manager: AutoCADManager | None = None
        self.copilot_sessions: CopilotSessionManager | None = None
        self.drafting_lifecycle: DraftingLifecycleService | None = None
        self.drafting_planner: DraftingPlanCoordinator | None = None
        self.verified_lessons: VerifiedLessonRegistry | None = None
        self.lesson_candidates: OperationalIncidentJournal | None = None
        self.files: FileManager | None = None

    @property
    def status(self) -> AppStatus:
        """Return the current in-memory lifecycle status."""
        return self._status

    def start(self) -> dict[str, Any]:
        """Initialize runtime resources and persist a running status."""
        if self._status is not AppStatus.CREATED:
            raise LifecycleError(f"Cannot start application while status is {self._status.value}.")

        self.config.ensure_runtime_directories()
        self._logger = configure_logging(self.config.log_file, self.config.log_level)
        self.verified_lessons = VerifiedLessonRegistry(self.config.verified_lessons_file)
        self.verified_lessons.load()
        self.lesson_candidates = OperationalIncidentJournal(
            self.config.lesson_candidates_file
        )
        self.lesson_candidates.load()
        self._initialize_command_services()
        state = self._state_store.load()
        starts = state.get("start_count", 0)
        if not isinstance(starts, int) or isinstance(starts, bool) or starts < 0:
            raise LifecycleError("Persistent start_count must be a non-negative integer.")

        now = self._timestamp()
        state.update(
            {
                "schema_version": 1,
                "application_version": __version__,
                "status": AppStatus.RUNNING.value,
                "start_count": starts + 1,
                "last_started_at": now,
            }
        )
        self._state_store.save(state)
        self._status = AppStatus.RUNNING
        self._logger.info("application_started", extra={"version": __version__})
        return state

    def _initialize_command_services(self) -> None:
        roots = self._workspace_roots or (self.config.workspace_dir,)
        backup_root = self._backup_root or self.config.backup_dir
        files = FileManager(roots, backup_root)
        self.files = files
        autocad = AutoCADManager(
            files,
            symbol_catalog_path=(
                self.config.project_root
                / "plugins"
                / "electrical_symbols"
                / "catalog.json"
            ),
            symbol_catalog_backup_dir=(backup_root / "symbol-catalog"),
            drawing_handoff_backup_dir=(backup_root / "autocad-handoff"),
        )
        terminal = TerminalManager(
            files,
            CommandPolicy(
                allowed_prefixes=(
                    (sys.executable, "-m", "pytest"),
                    (sys.executable, "-m", "compileall"),
                ),
                confirmation_prefixes=((sys.executable, "-m", "pytest"),),
            ),
        )
        services = CoreServices(
            files=files,
            scanner=ProjectScanner(files),
            system=SystemInfo(files),
            processes=ProcessManager(),
            vscode=VSCodeManager(files),
            autocad=autocad,
            terminal=terminal,
            operator_workspace_root=self.config.workspace_dir,
        )
        registry = CommandRegistry()
        register_core_commands(registry, services)
        approvals = ApprovalManager(self.config.approvals_file)
        command_engine = CommandEngine(
            registry,
            PermissionPolicy(CORE_PERMISSIONS),
            approvals,
        )
        self.command_registry = registry
        self.approvals = approvals
        self.task_engine = TaskEngine(command_engine, self.config.history_file)
        self.autocad_manager = autocad
        self.copilot_sessions = CopilotSessionManager(
            clock=self._clock,
            logger=self._logger,
            provider=self._copilot_provider,
        )
        self.drafting_lifecycle = DraftingLifecycleService(
            self.config.drafting_runs_file,
            clock=self._clock,
            logger=self._logger,
        )
        self.drafting_planner = DraftingPlanCoordinator(
            autocad,
            self.drafting_lifecycle,
            clock=self._clock,
        )
        if self._developer_provider is not None:
            self.developer_agent = DeveloperAgent(
                self._developer_provider,
                self.task_engine,
                self.config.developer_runs_file,
            )

    def stop(self) -> dict[str, Any]:
        """Persist a clean stopped status and close logging handlers."""
        if self._status is not AppStatus.RUNNING:
            raise LifecycleError(f"Cannot stop application while status is {self._status.value}.")

        state = self._state_store.load()
        state.update({"status": AppStatus.STOPPED.value, "last_stopped_at": self._timestamp()})
        self._state_store.save(state)
        self._status = AppStatus.STOPPED
        if self._logger is not None:
            self._logger.info("application_stopped")
            for handler in tuple(self._logger.handlers):
                handler.flush()
                handler.close()
                self._logger.removeHandler(handler)
        return state

    def _timestamp(self) -> str:
        value = self._clock()
        if value.tzinfo is None:
            raise LifecycleError("Lifecycle clock must return a timezone-aware datetime.")
        return value.astimezone(UTC).isoformat()
