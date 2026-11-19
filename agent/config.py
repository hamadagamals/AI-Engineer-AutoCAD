"""Typed configuration loading for the local application."""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from agent.exceptions import ConfigurationError


@dataclass(frozen=True, slots=True)
class AppConfig:
    """Validated paths and runtime settings resolved from the project root."""

    project_root: Path
    data_dir: Path
    log_dir: Path
    workspace_dir: Path
    backup_dir: Path
    state_file: Path
    mission_file: Path
    history_file: Path
    approvals_file: Path
    developer_runs_file: Path
    drafting_runs_file: Path
    verified_lessons_file: Path
    lesson_candidates_file: Path
    log_file: Path
    log_level: str

    @classmethod
    def load(cls, project_root: Path | None = None) -> AppConfig:
        """Load settings from ``data/settings.json`` beneath *project_root*."""
        root = (project_root or Path(__file__).resolve().parents[1]).resolve()
        settings_file = root / "data" / "settings.json"
        settings = _read_json_object(settings_file)

        log_level = settings.get("log_level", "INFO")
        if not isinstance(log_level, str) or log_level.upper() not in {
            "DEBUG",
            "INFO",
            "WARNING",
            "ERROR",
            "CRITICAL",
        }:
            raise ConfigurationError(
                f"Invalid log_level in {settings_file}: expected a standard logging level."
            )

        data_dir = root / "data"
        log_dir = root / "logs"
        return cls(
            project_root=root,
            data_dir=data_dir,
            log_dir=log_dir,
            workspace_dir=root / "workspace",
            backup_dir=root / "backup",
            state_file=data_dir / "agent_state.json",
            mission_file=data_dir / "mission.json",
            history_file=data_dir / "history.json",
            approvals_file=data_dir / "approvals.json",
            developer_runs_file=data_dir / "developer_runs.json",
            drafting_runs_file=data_dir / "drafting_runs.json",
            verified_lessons_file=data_dir / "verified_lessons.json",
            lesson_candidates_file=data_dir / "lesson_candidates.json",
            log_file=log_dir / "ai-engineer.jsonl",
            log_level=log_level.upper(),
        )

    def ensure_runtime_directories(self) -> None:
        """Create local runtime directories used by the foundation."""
        for directory in (
            self.data_dir,
            self.log_dir,
            self.workspace_dir,
            self.backup_dir,
        ):
            directory.mkdir(parents=True, exist_ok=True)


def _read_json_object(path: Path) -> dict[str, Any]:
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise ConfigurationError(f"Configuration file does not exist: {path}") from exc
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise ConfigurationError(f"Cannot read valid JSON configuration from {path}: {exc}") from exc

    if not isinstance(payload, dict):
        raise ConfigurationError(f"Configuration root must be a JSON object: {path}")
    return payload
