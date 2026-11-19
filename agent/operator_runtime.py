"""Opt-in operator bridge runtime for local IDE/CAD sidecar clients."""

from __future__ import annotations

import json
import os
from dataclasses import dataclass
from pathlib import Path
from typing import Mapping

from agent.exceptions import ApiConfigurationError
from integrations.codex_provider import CodexProviderConfig, create_codex_providers
from integrations.gemini_provider import GeminiProviderConfig, create_gemini_providers
from integrations.claude_provider import ClaudeProviderConfig, create_claude_providers
from integrations.hybrid_provider import create_hybrid_providers
from integrations.ollama_provider import OllamaProviderConfig, create_ollama_providers
from integrations.openai_provider import OpenAIProviderConfig, create_openai_providers
from integrations.openai_manufacturer_search import create_openai_manufacturer_provider


_TRUE_VALUES = frozenset({"1", "true", "yes", "on"})
_PROVIDER_MODES = frozenset(
    {"none", "auto", "ollama", "codex", "hybrid", "openai", "gemini", "claude"}
)


@dataclass(frozen=True, slots=True)
class OperatorRuntimeConfig:
    """Validated operator-mode scope assembled from explicit runtime inputs."""

    workspace_roots: tuple[Path, ...]
    backup_root: Path
    provider_mode: str
    ollama_enabled: bool
    codex_enabled: bool
    openai_enabled: bool
    gemini_enabled: bool
    claude_enabled: bool = False

    @classmethod
    def from_environment(
        cls,
        project_root: Path,
        *,
        allow_project_root: bool = False,
        environment: Mapping[str, str] | None = None,
    ) -> "OperatorRuntimeConfig":
        values = os.environ if environment is None else environment
        root = project_root.resolve(strict=True)
        configured: list[Path] = []
        raw_roots = values.get("AI_ENGINEER_OPERATOR_ROOTS", "")
        if raw_roots.strip():
            for value in raw_roots.split(os.pathsep):
                if not value.strip():
                    continue
                candidate = Path(value).expanduser().resolve(strict=True)
                _validate_workspace_root(candidate)
                configured.append(candidate)
        if allow_project_root:
            _validate_workspace_root(root)
            configured.append(root)
        if not configured:
            configured.append((root / "workspace").resolve(strict=False))
        unique: list[Path] = []
        for candidate in configured:
            if candidate not in unique:
                unique.append(candidate)
        backup_root = _operator_backup_root(values).resolve(strict=False)
        if backup_root.parent == backup_root:
            raise ApiConfigurationError("Operator backup root cannot be a filesystem root.")
        if any(_overlaps(backup_root, workspace) for workspace in unique):
            raise ApiConfigurationError(
                "Operator backup root must be outside every operator workspace root."
            )

        ollama_enabled = _enabled(values, "AI_ENGINEER_OLLAMA_ENABLE")
        codex_enabled = _enabled(values, "AI_ENGINEER_CODEX_ENABLE")
        openai_enabled = _enabled(values, "AI_ENGINEER_OPENAI_ENABLE")
        gemini_enabled = _enabled(values, "AI_ENGINEER_GEMINI_ENABLE")
        claude_enabled = _enabled(values, "AI_ENGINEER_CLAUDE_ENABLE")
        requested = values.get("AI_ENGINEER_PROVIDER", "auto").strip().casefold() or "auto"
        if requested not in _PROVIDER_MODES:
            raise ApiConfigurationError(
                "AI_ENGINEER_PROVIDER must be none, auto, ollama, codex, hybrid, openai, gemini, or claude."
            )
        provider_mode = _resolve_provider_mode(
            requested,
            ollama_enabled=ollama_enabled,
            codex_enabled=codex_enabled,
            openai_enabled=openai_enabled,
            gemini_enabled=gemini_enabled,
            claude_enabled=claude_enabled,
        )
        _validate_selected_provider(provider_mode, values)
        return cls(
            tuple(unique),
            backup_root,
            provider_mode,
            ollama_enabled,
            codex_enabled,
            openai_enabled,
            gemini_enabled,
            claude_enabled,
        )


def build_operator_providers(
    *,
    environment: Mapping[str, str] | None = None,
):
    """Build only the explicitly selected provider pair; never silently enable paid API use."""
    values = os.environ if environment is None else environment
    ollama_enabled = _enabled(values, "AI_ENGINEER_OLLAMA_ENABLE")
    codex_enabled = _enabled(values, "AI_ENGINEER_CODEX_ENABLE")
    openai_enabled = _enabled(values, "AI_ENGINEER_OPENAI_ENABLE")
    gemini_enabled = _enabled(values, "AI_ENGINEER_GEMINI_ENABLE")
    claude_enabled = _enabled(values, "AI_ENGINEER_CLAUDE_ENABLE")
    requested = values.get("AI_ENGINEER_PROVIDER", "auto").strip().casefold() or "auto"
    if requested not in _PROVIDER_MODES:
        raise ApiConfigurationError(
            "AI_ENGINEER_PROVIDER must be none, auto, ollama, codex, hybrid, openai, gemini, or claude."
        )
    mode = _resolve_provider_mode(
        requested,
        ollama_enabled=ollama_enabled,
        codex_enabled=codex_enabled,
        openai_enabled=openai_enabled,
        gemini_enabled=gemini_enabled,
        claude_enabled=claude_enabled,
    )
    if mode == "none":
        return None, None
    if mode == "ollama":
        return create_ollama_providers(OllamaProviderConfig.from_environment(values))
    if mode == "codex":
        return create_codex_providers(
            CodexProviderConfig.from_environment(values), environment=values
        )
    if mode == "openai":
        return create_openai_providers(OpenAIProviderConfig.from_environment(values))
    if mode == "gemini":
        return create_gemini_providers(GeminiProviderConfig.from_environment(values))
    if mode == "claude":
        return create_claude_providers(ClaudeProviderConfig.from_environment(values))
    if mode == "hybrid":
        local_developer, local_copilot = create_ollama_providers(
            OllamaProviderConfig.from_environment(values)
        )
        codex_developer, codex_copilot = create_codex_providers(
            CodexProviderConfig.from_environment(values), environment=values
        )
        return create_hybrid_providers(
            local_developer,
            local_copilot,
            codex_developer,
            codex_copilot,
        )
    raise ApiConfigurationError("Resolved operator provider mode is invalid.")


def build_manufacturer_provider(
    *, project_root: Path | None = None, environment: Mapping[str, str] | None = None
):
    """Build paid official search only after its own explicit runtime opt-in."""
    values = os.environ if environment is None else environment
    if not _enabled(values, "AI_ENGINEER_MANUFACTURER_DISCOVERY_ENABLE"):
        return None
    fallback: dict[str, object] = {}
    if _enabled(values, "AI_ENGINEER_MANUFACTURER_SAVED_FALLBACK"):
        if project_root is None:
            raise ApiConfigurationError(
                "Saved manufacturer evidence fallback requires the project root."
            )
        fallback = _saved_manufacturer_fallback(project_root)
    return create_openai_manufacturer_provider(
        OpenAIProviderConfig.from_environment(values), **fallback
    )


def _saved_manufacturer_fallback(project_root: Path) -> dict[str, object]:
    """Load the fixed candidate map and expose only its exact workspace evidence files."""
    root = project_root.resolve(strict=True)
    template = (
        root / "integrations" / "manufacturer_templates" / "free-candidates.template.json"
    )
    if not template.is_file() or template.is_symlink():
        raise ApiConfigurationError("Saved manufacturer candidate template is unavailable.")
    try:
        draft = json.loads(template.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise ApiConfigurationError("Saved manufacturer candidate template is invalid.") from exc
    raw_candidates = draft.get("candidates") if isinstance(draft, dict) else None
    if not isinstance(raw_candidates, list) or not raw_candidates:
        raise ApiConfigurationError("Saved manufacturer candidate template is empty.")
    allowed: dict[str, Path] = {}
    for item in raw_candidates:
        path = item.get("evidence_path") if isinstance(item, dict) else None
        if not isinstance(path, str) or path in allowed:
            raise ApiConfigurationError("Saved manufacturer evidence path is invalid.")
        target = (root / path).resolve(strict=False)
        if not target.is_relative_to(root):
            raise ApiConfigurationError("Saved manufacturer evidence escapes the project root.")
        allowed[path] = target

    def read_evidence(path: str) -> bytes:
        target = allowed.get(path)
        if target is None or not target.is_file() or target.is_symlink():
            raise ApiConfigurationError("Approved saved manufacturer evidence is unavailable.")
        content = target.read_bytes()
        if not 1 <= len(content) <= 2_097_152:
            raise ApiConfigurationError("Saved manufacturer evidence exceeds its safe limit.")
        return content

    return {"saved_candidate_draft": draft, "evidence_read": read_evidence}


def _resolve_provider_mode(
    requested: str,
    *,
    ollama_enabled: bool,
    codex_enabled: bool,
    openai_enabled: bool,
    gemini_enabled: bool,
    claude_enabled: bool = False,
) -> str:
    if requested == "none":
        return "none"
    if requested == "ollama":
        if not ollama_enabled:
            raise ApiConfigurationError("Ollama mode requires AI_ENGINEER_OLLAMA_ENABLE=1.")
        return "ollama"
    if requested == "codex":
        if not codex_enabled:
            raise ApiConfigurationError("Codex mode requires AI_ENGINEER_CODEX_ENABLE=1.")
        return "codex"
    if requested == "openai":
        if not openai_enabled:
            raise ApiConfigurationError("OpenAI mode requires AI_ENGINEER_OPENAI_ENABLE=1.")
        return "openai"
    if requested == "gemini":
        if not gemini_enabled:
            raise ApiConfigurationError("Gemini mode requires AI_ENGINEER_GEMINI_ENABLE=1.")
        return "gemini"
    if requested == "claude":
        if not claude_enabled:
            raise ApiConfigurationError("Claude mode requires AI_ENGINEER_CLAUDE_ENABLE=1.")
        return "claude"
    if requested == "hybrid":
        if not ollama_enabled or not codex_enabled:
            raise ApiConfigurationError(
                "Hybrid mode requires both AI_ENGINEER_OLLAMA_ENABLE=1 and "
                "AI_ENGINEER_CODEX_ENABLE=1."
            )
        return "hybrid"
    # auto deliberately favors free/local capacity and never combines OpenAI with another mode.
    if ollama_enabled and codex_enabled:
        return "hybrid"
    if ollama_enabled:
        return "ollama"
    if codex_enabled:
        return "codex"
    if gemini_enabled:
        return "gemini"
    if claude_enabled:
        return "claude"
    if openai_enabled:
        return "openai"
    return "none"


def _validate_selected_provider(mode: str, values: Mapping[str, str]) -> None:
    if mode == "ollama":
        OllamaProviderConfig.from_environment(values)
    elif mode == "codex":
        CodexProviderConfig.from_environment(values)
    elif mode == "openai":
        OpenAIProviderConfig.from_environment(values)
    elif mode == "gemini":
        GeminiProviderConfig.from_environment(values)
    elif mode == "claude":
        ClaudeProviderConfig.from_environment(values)
    elif mode == "hybrid":
        OllamaProviderConfig.from_environment(values)
        CodexProviderConfig.from_environment(values)


def _enabled(values: Mapping[str, str], key: str) -> bool:
    return values.get(key, "").strip().casefold() in _TRUE_VALUES


def _operator_backup_root(values: Mapping[str, str]) -> Path:
    configured = values.get("AI_ENGINEER_OPERATOR_BACKUP_ROOT")
    if configured and configured.strip():
        return Path(configured).expanduser()
    local = values.get("LOCALAPPDATA")
    if local and local.strip():
        return Path(local) / "AI-Engineer" / "operator-backup"
    home = values.get("HOME") or values.get("USERPROFILE")
    if home and home.strip():
        return Path(home) / ".ai-engineer" / "operator-backup"
    return Path.home() / ".ai-engineer" / "operator-backup"


def _validate_workspace_root(path: Path) -> None:
    if not path.is_absolute():
        raise ApiConfigurationError("Operator workspace roots must be absolute paths.")
    if path.exists() and not path.is_dir():
        raise ApiConfigurationError("Operator workspace roots must be directories.")
    if path.parent == path:
        raise ApiConfigurationError("Filesystem roots cannot be operator workspace roots.")


def _overlaps(first: Path, second: Path) -> bool:
    try:
        first.relative_to(second)
        return True
    except ValueError:
        pass
    try:
        second.relative_to(first)
        return True
    except ValueError:
        return False
