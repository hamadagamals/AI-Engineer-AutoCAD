"""Codex CLI reasoning adapters that keep AI-Engineer as the sole local executor.

Codex runs non-interactively in an empty temporary directory with a read-only sandbox.
It never receives the project directory as its working directory and it inherits only a
small environment allowlist needed for CLI authentication/runtime operation.
"""

from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import tempfile
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable, Mapping

from agent.copilot_provider import CopilotProviderAdapter, CopilotProviderRequest
from agent.exceptions import ApiConfigurationError, CopilotProviderError, ProviderError
from agent.provider import ProviderAdapter, ProviderPhase, ProviderRequest
from integrations.provider_contracts import (
    copilot_instructions,
    copilot_json_schema,
    copilot_payload,
    developer_instructions,
    developer_json_schema,
    developer_payload,
)

_ENABLE_VALUES = frozenset({"1", "true", "yes", "on"})
_COMMAND_PATTERN = re.compile(r"codex(?:\.exe|\.cmd)?", re.IGNORECASE)
_MAX_PROMPT_BYTES = 524_288
_MAX_RESPONSE_BYTES = 1_048_576
_SAFE_ENVIRONMENT_KEYS = frozenset(
    {
        "APPDATA",
        "CODEX_HOME",
        "COMSPEC",
        "HOME",
        "HOMEDRIVE",
        "HOMEPATH",
        "LANG",
        "LC_ALL",
        "LOCALAPPDATA",
        "PATH",
        "PATHEXT",
        "SYSTEMDRIVE",
        "SYSTEMROOT",
        "TEMP",
        "TMP",
        "TMPDIR",
        "USERPROFILE",
        "USERNAME",
        "WINDIR",
    }
)
RunProcess = Callable[..., subprocess.CompletedProcess[str]]


@dataclass(frozen=True, slots=True)
class CodexProviderConfig:
    """Validated ChatGPT-authenticated Codex CLI settings."""

    command: str = "codex"
    timeout_seconds: float = 180.0

    def __post_init__(self) -> None:
        command_path = Path(self.command)
        if command_path.name != self.command and not command_path.is_absolute():
            raise ApiConfigurationError("Codex command must be a name or an absolute path.")
        if _COMMAND_PATTERN.fullmatch(command_path.name) is None:
            raise ApiConfigurationError("Codex command must resolve to codex, codex.exe, or codex.cmd.")
        if (
            not isinstance(self.timeout_seconds, (int, float))
            or isinstance(self.timeout_seconds, bool)
            or not 15 <= float(self.timeout_seconds) <= 900
        ):
            raise ApiConfigurationError("Codex timeout must be from 15 through 900 seconds.")

    @classmethod
    def from_environment(
        cls, environment: Mapping[str, str] | None = None
    ) -> "CodexProviderConfig":
        values = os.environ if environment is None else environment
        if values.get("AI_ENGINEER_CODEX_ENABLE", "").strip().casefold() not in _ENABLE_VALUES:
            raise ApiConfigurationError(
                "Codex provider is disabled. Set AI_ENGINEER_CODEX_ENABLE=1 to opt in."
            )
        if (
            values.get("AI_ENGINEER_CODEX_EGRESS_CONFIRMED", "").strip().casefold()
            not in _ENABLE_VALUES
        ):
            raise ApiConfigurationError(
                "Codex data egress is not confirmed. Set AI_ENGINEER_CODEX_EGRESS_CONFIRMED=1 "
                "only after reviewing the boundary."
            )
        try:
            timeout = float(values.get("AI_ENGINEER_CODEX_TIMEOUT_SECONDS", "180"))
        except ValueError as exc:
            raise ApiConfigurationError("Codex timeout must be numeric.") from exc
        return cls(
            command=values.get("AI_ENGINEER_CODEX_COMMAND", "codex").strip() or "codex",
            timeout_seconds=timeout,
        )


class CodexExecClient:
    """Use ``codex exec`` as a bounded reasoning service, never as the project executor."""

    def __init__(
        self,
        config: CodexProviderConfig,
        *,
        run_process: RunProcess | None = None,
        environment: Mapping[str, str] | None = None,
    ) -> None:
        self.config = config
        self._run_process = run_process or subprocess.run
        self._environment = dict(os.environ if environment is None else environment)

    def generate_developer(self, request: ProviderRequest) -> str:
        if not isinstance(request, ProviderRequest):
            raise ProviderError("Codex developer provider requires a typed request.")
        schema = _codex_developer_schema(request.phase)
        prompt = (
            developer_instructions(request)
            + "\nDo not use tools or inspect the temporary environment. Reason only from this "
            "AI-Engineer request JSON:\n"
            + developer_payload(request)
        )
        raw = self._exec(prompt, schema, ProviderError, float(self.config.timeout_seconds))
        return _normalize_developer_output(raw, request.phase)

    def generate_copilot(self, request: CopilotProviderRequest) -> str:
        if not isinstance(request, CopilotProviderRequest):
            raise CopilotProviderError("Codex copilot provider requires a typed request.")
        prompt = (
            copilot_instructions(request)
            + "\nDo not use tools or inspect the temporary environment. Reason only from these "
            "approved egress fields:\n"
            + copilot_payload(request)
        )
        return self._exec(
            prompt,
            copilot_json_schema(request),
            CopilotProviderError,
            min(55.0, float(self.config.timeout_seconds)),
        )

    def _exec(
        self,
        prompt: str,
        schema: dict[str, Any],
        error_type: type[Exception],
        timeout_seconds: float,
    ) -> str:
        try:
            prompt_size = len(prompt.encode("utf-8"))
        except UnicodeEncodeError as exc:
            raise error_type("Codex prompt contains invalid UTF-8 text.") from exc
        if not 1 <= prompt_size <= _MAX_PROMPT_BYTES:
            raise error_type("Codex provider request exceeds its safe byte limit.")
        executable = _resolve_codex_command(self.config.command)
        safe_environment = _codex_environment(self._environment)
        try:
            with tempfile.TemporaryDirectory(prefix="ai-engineer-codex-") as directory:
                root = Path(directory)
                schema_path = root / "output-schema.json"
                schema_path.write_text(
                    json.dumps(schema, ensure_ascii=False, separators=(",", ":")),
                    encoding="utf-8",
                )
                argv = [
                    executable,
                    "exec",
                    "--ephemeral",
                    "--sandbox",
                    "read-only",
                    "--ignore-user-config",
                    "--ignore-rules",
                    "--skip-git-repo-check",
                    "--output-schema",
                    str(schema_path),
                    "-",
                ]
                completed = self._run_process(
                    argv,
                    input=prompt,
                    cwd=str(root),
                    env=safe_environment,
                    text=True,
                    capture_output=True,
                    timeout=timeout_seconds,
                    check=False,
                    shell=False,
                )
        except subprocess.TimeoutExpired as exc:
            raise error_type("Codex provider timed out.") from exc
        except OSError as exc:
            raise error_type(f"Codex CLI could not be started: {type(exc).__name__}.") from exc
        if not isinstance(completed, subprocess.CompletedProcess):
            raise error_type("Codex runner returned an invalid process result.")
        if completed.returncode != 0:
            raise error_type("Codex CLI returned a non-success status.")
        stdout = completed.stdout
        if not isinstance(stdout, str):
            raise error_type("Codex CLI returned invalid text output.")
        encoded = stdout.encode("utf-8")
        if not 1 <= len(encoded) <= _MAX_RESPONSE_BYTES:
            raise error_type("Codex provider response exceeds its safe byte limit.")
        return stdout.strip()


def create_codex_providers(
    config: CodexProviderConfig,
    *,
    run_process: RunProcess | None = None,
    environment: Mapping[str, str] | None = None,
) -> tuple[ProviderAdapter, CopilotProviderAdapter]:
    client = CodexExecClient(config, run_process=run_process, environment=environment)
    developer = ProviderAdapter("codex", client.generate_developer)
    copilot = CopilotProviderAdapter(
        "codex",
        client.generate_copilot,
        timeout_seconds=min(60, max(15, int(config.timeout_seconds))),
        maximum_concurrent_calls=1,
    )
    return developer, copilot


def _resolve_codex_command(command: str) -> str:
    path = Path(command)
    if path.is_absolute():
        if not path.is_file():
            raise ProviderError("Configured Codex CLI does not exist.")
        return str(path)
    resolved = shutil.which(command)
    if resolved is None:
        raise ProviderError("Codex CLI was not found on PATH.")
    if _COMMAND_PATTERN.fullmatch(Path(resolved).name) is None:
        raise ProviderError("Resolved Codex executable name is not allowlisted.")
    return resolved


def _codex_environment(environment: Mapping[str, str]) -> dict[str, str]:
    """Return a minimal child environment; notably excludes API/bridge credentials."""
    safe = {key: value for key, value in environment.items() if key.upper() in _SAFE_ENVIRONMENT_KEYS}
    # Some platforms require PATH even in test/minimal environments.
    safe.setdefault("PATH", environment.get("PATH", os.defpath))
    return safe


def _codex_developer_schema(phase: ProviderPhase) -> dict[str, Any]:
    if phase is not ProviderPhase.ACT:
        return developer_json_schema(phase)
    # A flat nullable envelope is friendlier to strict structured-output implementations.
    # It is normalized back to AI-Engineer's exact ACT contract before local parsing.
    return {
        "type": "object",
        "properties": {
            "kind": {"type": "string", "enum": ["command", "complete"]},
            "command_name": {"type": ["string", "null"]},
            "payload": {"type": ["object", "null"]},
            "rationale": {"type": ["string", "null"]},
            "summary": {"type": ["string", "null"]},
        },
        "required": ["kind", "command_name", "payload", "rationale", "summary"],
        "additionalProperties": False,
    }


def _normalize_developer_output(raw: str, phase: ProviderPhase) -> str:
    if phase is not ProviderPhase.ACT:
        return raw
    try:
        value = json.loads(raw)
    except json.JSONDecodeError as exc:
        raise ProviderError("Codex provider returned invalid JSON.") from exc
    if not isinstance(value, dict):
        raise ProviderError("Codex ACT output must be an object.")
    kind = value.get("kind")
    if kind == "command":
        command_name = value.get("command_name")
        payload = value.get("payload")
        rationale = value.get("rationale")
        if not isinstance(command_name, str) or not command_name.strip():
            raise ProviderError("Codex ACT command name is invalid.")
        if not isinstance(payload, dict):
            raise ProviderError("Codex ACT command payload is invalid.")
        if not isinstance(rationale, str) or not rationale.strip():
            raise ProviderError("Codex ACT rationale is invalid.")
        normalized = {
            "kind": "command",
            "command_name": command_name,
            "payload": payload,
            "rationale": rationale,
        }
    elif kind == "complete":
        summary = value.get("summary")
        if not isinstance(summary, str) or not summary.strip():
            raise ProviderError("Codex ACT completion summary is invalid.")
        normalized = {"kind": "complete", "summary": summary}
    else:
        raise ProviderError("Codex ACT kind is invalid.")
    return json.dumps(normalized, ensure_ascii=False, separators=(",", ":"))
