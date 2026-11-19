"""Supported, workspace-confined Visual Studio Code CLI integration."""

from __future__ import annotations

import logging
import os
import re
import shutil
import subprocess
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Callable

from agent.exceptions import ConfirmationRequiredError, VSCodeIntegrationError
from core.file_manager import FileManager


_SAFE_ENVIRONMENT_KEYS = frozenset(
    {
        "APPDATA",
        "HOME",
        "LOCALAPPDATA",
        "PATH",
        "PATHEXT",
        "SYSTEMROOT",
        "TEMP",
        "TMP",
        "USERPROFILE",
        "VSCODE_IPC_HOOK_CLI",
        "WINDIR",
    }
)
_VERSION_PATTERN = re.compile(r"\d+\.\d+\.\d+(?:[-+][A-Za-z0-9.-]+)?")
_COMMIT_PATTERN = re.compile(r"[0-9a-fA-F]{7,64}")
_ARCHITECTURES = frozenset({"arm64", "x64", "x86"})
CommandLocator = Callable[[str], str | None]


@dataclass(frozen=True, slots=True)
class VSCodeStatus:
    """Non-secret installation metadata returned to callers."""

    installed: bool
    version: str | None
    commit: str | None
    architecture: str | None
    cli_kind: str | None

    def to_dict(self) -> dict[str, object]:
        return asdict(self)


@dataclass(frozen=True, slots=True)
class VSCodeOpenResult:
    """Verified result of one approved CLI open request."""

    target: Path
    line: int | None
    column: int | None
    return_code: int

    def to_dict(self) -> dict[str, object]:
        value = asdict(self)
        value["target"] = str(self.target)
        return value


@dataclass(frozen=True, slots=True)
class _LaunchCommand:
    arguments: tuple[str, ...]
    cli_kind: str


class VSCodeManager:
    """Detect and invoke only the supported ``code`` CLI for workspace paths."""

    def __init__(
        self,
        files: FileManager,
        *,
        locator: CommandLocator | None = None,
        timeout_seconds: float = 15.0,
        logger: logging.Logger | None = None,
    ) -> None:
        if (
            not isinstance(timeout_seconds, (int, float))
            or isinstance(timeout_seconds, bool)
            or not 1 <= timeout_seconds <= 60
        ):
            raise VSCodeIntegrationError("VS Code CLI timeout must be from 1 through 60 seconds.")
        self._files = files
        self._locator = locator or shutil.which
        self.timeout_seconds = float(timeout_seconds)
        self._logger = logger or logging.getLogger("ai_engineer.integrations.vscode")

    def detect(self) -> VSCodeStatus:
        """Return validated non-secret CLI version metadata, or not-installed state."""
        launch = self._locate_launch_command()
        if launch is None:
            return VSCodeStatus(False, None, None, None, None)
        result = self._invoke(
            (*launch.arguments, "--version"),
            electron_cli=launch.cli_kind == "windows-wrapper",
        )
        if result.returncode != 0:
            raise VSCodeIntegrationError(
                f"VS Code CLI version check returned exit code {result.returncode}."
            )
        lines = tuple(line.strip() for line in result.stdout.splitlines() if line.strip())
        if len(lines) < 3:
            raise VSCodeIntegrationError("VS Code CLI returned incomplete version metadata.")
        version, commit, architecture = lines[:3]
        if _VERSION_PATTERN.fullmatch(version) is None:
            raise VSCodeIntegrationError("VS Code CLI returned an invalid version value.")
        if _COMMIT_PATTERN.fullmatch(commit) is None:
            raise VSCodeIntegrationError("VS Code CLI returned an invalid commit value.")
        if architecture.casefold() not in _ARCHITECTURES:
            raise VSCodeIntegrationError("VS Code CLI returned an unsupported architecture.")
        self._logger.info(
            "vscode_detected",
            extra={"version": version, "architecture": architecture.casefold()},
        )
        return VSCodeStatus(
            True,
            version,
            commit.casefold(),
            architecture.casefold(),
            launch.cli_kind,
        )

    def open_path(
        self,
        target: str | Path,
        *,
        line: int | None = None,
        column: int | None = None,
        confirmed: bool = False,
    ) -> VSCodeOpenResult:
        """Open one existing workspace file/directory after explicit confirmation."""
        if not confirmed:
            raise ConfirmationRequiredError(
                "Opening Visual Studio Code requires explicit confirmation."
            )
        resolved = self._files.resolve_path(target)
        if not resolved.exists():
            raise VSCodeIntegrationError(f"VS Code target does not exist: {target}")
        if not resolved.is_file() and not resolved.is_dir():
            raise VSCodeIntegrationError("VS Code target must be a regular file or directory.")
        _validate_position(resolved, line, column)
        launch = self._locate_launch_command()
        if launch is None:
            raise VSCodeIntegrationError("VS Code CLI is not installed or not available on PATH.")
        arguments = [*launch.arguments, "--reuse-window"]
        if line is not None:
            location = f"{resolved}:{line}"
            if column is not None:
                location += f":{column}"
            arguments.extend(("--goto", location))
        else:
            arguments.append(str(resolved))
        result = self._invoke(
            tuple(arguments), electron_cli=launch.cli_kind == "windows-wrapper"
        )
        if result.returncode != 0:
            raise VSCodeIntegrationError(
                f"VS Code CLI open request returned exit code {result.returncode}."
            )
        self._logger.info(
            "vscode_path_opened",
            extra={"target_type": "file" if resolved.is_file() else "directory"},
        )
        return VSCodeOpenResult(resolved, line, column, result.returncode)

    def _locate_launch_command(self) -> _LaunchCommand | None:
        located = self._locator("code")
        if not located:
            return None
        cli_path = Path(located).resolve(strict=True)
        if cli_path.suffix.casefold() not in {".bat", ".cmd"}:
            return _LaunchCommand((str(cli_path),), "native")
        return _resolve_windows_wrapper(cli_path)

    def _invoke(
        self, arguments: tuple[str, ...], *, electron_cli: bool
    ) -> subprocess.CompletedProcess[str]:
        environment = {
            key: value
            for key, value in os.environ.items()
            if key.upper() in _SAFE_ENVIRONMENT_KEYS
        }
        if electron_cli:
            environment["ELECTRON_RUN_AS_NODE"] = "1"
        if len(arguments) > 128 or sum(len(item) for item in arguments) > 32_768:
            raise VSCodeIntegrationError("VS Code CLI arguments exceed the safe limit.")
        creation_flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
        try:
            return subprocess.run(
                arguments,
                stdin=subprocess.DEVNULL,
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                timeout=self.timeout_seconds,
                shell=False,
                env=environment,
                creationflags=creation_flags,
                check=False,
            )
        except subprocess.TimeoutExpired as exc:
            raise VSCodeIntegrationError("VS Code CLI request timed out.") from exc
        except OSError as exc:
            raise VSCodeIntegrationError(
                f"Cannot start the validated VS Code CLI: {type(exc).__name__}."
            ) from exc


def _resolve_windows_wrapper(path: Path) -> _LaunchCommand:
    """Resolve the official wrapper without executing a command shell."""
    try:
        content = path.read_text(encoding="utf-8")
    except (OSError, UnicodeError) as exc:
        raise VSCodeIntegrationError("Cannot read the VS Code CLI wrapper safely.") from exc
    if len(content.encode("utf-8")) > 65_536:
        raise VSCodeIntegrationError("VS Code CLI wrapper exceeds the safe size limit.")
    launch_line = next(
        (
            line.strip()
            for line in content.splitlines()
            if "Code.exe" in line and "cli.js" in line and "%*" in line
        ),
        None,
    )
    if launch_line is None:
        raise VSCodeIntegrationError("VS Code CLI wrapper format is not recognized.")
    quoted = re.findall(r'"([^"]+)"', launch_line)
    if len(quoted) != 2 or any(not item.startswith("%~dp0..") for item in quoted):
        raise VSCodeIntegrationError("VS Code CLI wrapper contains unexpected launch paths.")
    install_root = path.parent.parent.resolve(strict=True)
    resolved: list[Path] = []
    for token in quoted:
        relative = token[len("%~dp0..") :].lstrip("\\/").replace("\\", os.sep)
        candidate = (install_root / relative).resolve(strict=True)
        try:
            candidate.relative_to(install_root)
        except ValueError as exc:
            raise VSCodeIntegrationError("VS Code CLI wrapper escapes its installation root.") from exc
        resolved.append(candidate)
    executable, script = resolved
    if executable.name.casefold() != "code.exe" or script.name.casefold() != "cli.js":
        raise VSCodeIntegrationError("VS Code CLI wrapper launch targets are invalid.")
    return _LaunchCommand((str(executable), str(script)), "windows-wrapper")


def _validate_position(path: Path, line: int | None, column: int | None) -> None:
    for name, value in (("line", line), ("column", column)):
        if value is not None and (
            not isinstance(value, int) or isinstance(value, bool) or not 1 <= value <= 1_000_000
        ):
            raise VSCodeIntegrationError(
                f"VS Code {name} must be an integer from 1 through 1000000."
            )
    if column is not None and line is None:
        raise VSCodeIntegrationError("VS Code column requires a line number.")
    if line is not None and not path.is_file():
        raise VSCodeIntegrationError("VS Code line navigation requires a regular file.")
