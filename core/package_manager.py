"""Portable packaging, installation, recovery, and runtime-data migration."""

from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import stat
import tempfile
import uuid
import zipfile
from dataclasses import asdict, dataclass
from datetime import UTC, datetime
from pathlib import Path, PurePosixPath
from typing import Any, Callable, Iterable, Mapping

from agent.config import AppConfig
from agent.exceptions import MigrationError, PackagingError, RecoveryError
from agent.state import StateStore
from agent.version import __version__


_SOURCE_DIRECTORIES = ("agent", "api", "clients", "core", "docs", "integrations", "plugins")
_ROOT_FILES = (
    ".gitignore",
    "AGENTS.md",
    "GEMINI.md",
    "AI_ENGINEER_PROJECT_BRIEF.md",
    "README.md",
    "__main__.py",
    "ai-engineer.cmd",
    "launcher.py",
    "main.py",
    "requirements.txt",
    "start-operator.ps1",
    "START-AI-ENGINEER-PROVIDER.ps1",
    "START-AI-ENGINEER-LOCAL.bat",
    "START-AI-ENGINEER-CODEX.bat",
    "START-AI-ENGINEER-HYBRID.bat",
    "START-AI-ENGINEER-WITH-OPENAI.bat",
    "START-AI-ENGINEER-WITH-OPENAI.ps1",
    "START-AI-ENGINEER-WITH-GEMINI.bat",
    "START-AI-ENGINEER-WITH-GEMINI.ps1",
    "START-AI-ENGINEER-AUTOCAD-PREVIEW.ps1",
    "CHECK-AI-ENGINEER-AUTOCAD-2023.ps1",
    "START-AI-ENGINEER-AUTOCAD-2023.ps1",
    "START-AI-ENGINEER-AUTOCAD-COMPLIANCE.ps1",
    "START-AI-ENGINEER-AUTOCAD-MANUFACTURER.ps1",
    "START-AI-ENGINEER-AUTOCAD-DISCOVERY.ps1",
    "START-AI-ENGINEER-MANUFACTURER-ASSETS.ps1",
)
_RUNTIME_DATA_FILES = (
    "agent_state.json",
    "approvals.json",
    "developer_runs.json",
    "drafting_runs.json",
    "history.json",
    "mission.json",
    "settings.json",
)
_RUNTIME_DIRECTORIES = ("workspace", "logs", "backup")
_REQUIRED_RECOVERY_FILES = frozenset({"agent_state.json", "mission.json", "settings.json"})
_SECRET_KEY_FRAGMENTS = ("api_key", "credential", "password", "secret", "token")
_MAX_ARCHIVE_ENTRIES = 2_000
_MAX_ARCHIVE_BYTES = 134_217_728
_POST_BUILD_EVIDENCE_PREFIX = "docs/evidence/autocad-beta-package-candidate-"


@dataclass(frozen=True, slots=True)
class PackageBuildResult:
    archive: Path
    version: str
    file_count: int
    bytes_written: int

    def to_dict(self) -> dict[str, object]:
        value = asdict(self)
        value["archive"] = str(self.archive)
        return value


@dataclass(frozen=True, slots=True)
class PackageInstallResult:
    destination: Path
    version: str
    file_count: int

    def to_dict(self) -> dict[str, object]:
        value = asdict(self)
        value["destination"] = str(self.destination)
        return value


@dataclass(frozen=True, slots=True)
class RecoveryResult:
    archive: Path
    file_count: int
    backup_directory: Path | None = None

    def to_dict(self) -> dict[str, object]:
        value = asdict(self)
        value["archive"] = str(self.archive)
        value["backup_directory"] = (
            str(self.backup_directory) if self.backup_directory else None
        )
        return value


@dataclass(frozen=True, slots=True)
class MigrationResult:
    changed_files: tuple[str, ...]
    recovery_archive: Path | None

    def to_dict(self) -> dict[str, object]:
        return {
            "changed_files": list(self.changed_files),
            "recovery_archive": (
                str(self.recovery_archive) if self.recovery_archive else None
            ),
        }


class PackageBuilder:
    """Build a bounded portable source distribution with an integrity manifest."""

    def __init__(
        self,
        project_root: Path,
        *,
        clock: Callable[[], datetime] | None = None,
    ) -> None:
        self.project_root = project_root.resolve(strict=True)
        self._clock = clock or (lambda: datetime.now(UTC))

    def build(self, destination: Path) -> PackageBuildResult:
        """Atomically build a new archive and refuse to overwrite any existing path."""
        target = destination.expanduser().resolve(strict=False)
        if target.exists():
            raise PackagingError(f"Package destination already exists: {target}")
        target.parent.mkdir(parents=True, exist_ok=True)
        entries = self._source_entries()
        generated = _portable_data_templates(self.project_root)
        for name, content in generated.items():
            entries[name] = content
        hashes = {name: _sha256(content) for name, content in entries.items()}
        manifest = {
            "schema_version": 1,
            "package_type": "ai-engineer-portable",
            "application_version": __version__,
            "python_requires": ">=3.13,<3.14",
            "created_at": _timestamp(self._clock, PackagingError),
            "files": hashes,
        }
        entries["package_manifest.json"] = _json_bytes(manifest)
        temporary: Path | None = None
        try:
            descriptor, name = tempfile.mkstemp(
                dir=target.parent, prefix=f".{target.name}.", suffix=".tmp"
            )
            os.close(descriptor)
            temporary = Path(name)
            with zipfile.ZipFile(
                temporary, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9
            ) as archive:
                for entry_name in sorted(entries):
                    _write_zip_entry(archive, entry_name, entries[entry_name])
            _flush_file(temporary)
            os.replace(temporary, target)
        except (OSError, zipfile.BadZipFile) as exc:
            raise PackagingError(
                f"Cannot build portable package: {type(exc).__name__}."
            ) from exc
        finally:
            if temporary is not None and temporary.exists():
                temporary.unlink()
        return PackageBuildResult(target, __version__, len(entries), target.stat().st_size)

    def _source_entries(self) -> dict[str, bytes]:
        entries: dict[str, bytes] = {}
        for name in _ROOT_FILES:
            path = self.project_root / name
            if not path.is_file():
                raise PackagingError(f"Required package source is missing: {name}")
            _validate_source_path(path, self.project_root)
            entries[name] = _read_bounded(path, PackagingError)
        for directory_name in _SOURCE_DIRECTORIES:
            directory = self.project_root / directory_name
            if not directory.is_dir():
                raise PackagingError(
                    f"Required package directory is missing: {directory_name}"
                )
            for path in sorted(directory.rglob("*")):
                if not path.is_file() or "__pycache__" in path.parts:
                    continue
                _validate_source_path(path, self.project_root)
                relative = path.relative_to(self.project_root).as_posix()
                if relative.startswith(_POST_BUILD_EVIDENCE_PREFIX):
                    continue
                if path.suffix.casefold() in {".pyc", ".pyo"}:
                    continue
                entries[relative] = _read_bounded(path, PackagingError)
        if len(entries) > _MAX_ARCHIVE_ENTRIES:
            raise PackagingError("Portable package exceeds its file-count limit.")
        if sum(len(content) for content in entries.values()) > _MAX_ARCHIVE_BYTES:
            raise PackagingError("Portable package exceeds its source byte limit.")
        return entries


class PackageInstaller:
    """Validate and atomically install a portable package into a new directory."""

    def install(
        self, archive_path: Path, destination: Path, *, confirmed: bool = False
    ) -> PackageInstallResult:
        if not confirmed:
            raise PackagingError("Portable package installation requires confirmation.")
        source = archive_path.expanduser().resolve(strict=True)
        target = destination.expanduser().resolve(strict=False)
        if target.exists():
            raise PackagingError("Install destination must not already exist.")
        if target == target.parent:
            raise PackagingError("Filesystem roots cannot be installation destinations.")
        entries, manifest = _validated_archive(
            source,
            manifest_name="package_manifest.json",
            expected_type="ai-engineer-portable",
            error_type=PackagingError,
        )
        target.parent.mkdir(parents=True, exist_ok=True)
        # Create the final root with ordinary inherited permissions. On Windows,
        # renaming a restrictive tempfile.mkdtemp directory into place preserves
        # the staging ACL and can make the installation unreadable to the
        # interactive user even when the parent directory is accessible.
        target.mkdir()
        try:
            for name, content in entries.items():
                output = target / Path(*PurePosixPath(name).parts)
                output.parent.mkdir(parents=True, exist_ok=True)
                _atomic_write_bytes(output, content)
            _atomic_write_bytes(
                target / "package_manifest.json", _json_bytes(manifest)
            )
            for name in _RUNTIME_DIRECTORIES:
                (target / name).mkdir(exist_ok=False)
        except OSError as exc:
            shutil.rmtree(target, ignore_errors=True)
            raise PackagingError(
                f"Cannot install portable package: {type(exc).__name__}."
            ) from exc
        return PackageInstallResult(
            target,
            str(manifest["application_version"]),
            len(entries),
        )


class RecoveryManager:
    """Create and restore bounded runtime-data snapshots with pre-restore backups."""

    def __init__(
        self,
        config: AppConfig,
        *,
        clock: Callable[[], datetime] | None = None,
    ) -> None:
        self.config = config
        self._clock = clock or (lambda: datetime.now(UTC))

    def create_snapshot(self, destination: Path) -> RecoveryResult:
        """Atomically snapshot allowlisted non-secret JSON runtime documents."""
        target = destination.expanduser().resolve(strict=False)
        if target.exists():
            raise RecoveryError(f"Recovery snapshot already exists: {target}")
        documents = self._load_documents(require_essential=True)
        target.parent.mkdir(parents=True, exist_ok=True)
        contents = {
            f"data/{name}": _json_bytes(document)
            for name, document in documents.items()
        }
        manifest = {
            "schema_version": 1,
            "package_type": "ai-engineer-recovery",
            "application_version": __version__,
            "created_at": _timestamp(self._clock, RecoveryError),
            "files": {name: _sha256(content) for name, content in contents.items()},
        }
        contents["recovery_manifest.json"] = _json_bytes(manifest)
        _write_archive_atomic(target, contents, RecoveryError)
        return RecoveryResult(target, len(documents))

    def restore_snapshot(
        self, archive_path: Path, *, confirmed: bool = False
    ) -> RecoveryResult:
        """Validate fully, back up current data, then atomically restore each document."""
        if not confirmed:
            raise RecoveryError("Recovery restore requires explicit confirmation.")
        source = archive_path.expanduser().resolve(strict=True)
        entries, _ = _validated_archive(
            source,
            manifest_name="recovery_manifest.json",
            expected_type="ai-engineer-recovery",
            error_type=RecoveryError,
        )
        documents: dict[str, dict[str, Any]] = {}
        for entry_name, content in entries.items():
            parts = PurePosixPath(entry_name).parts
            if len(parts) != 2 or parts[0] != "data" or parts[1] not in _RUNTIME_DATA_FILES:
                raise RecoveryError(f"Recovery archive contains an invalid entry: {entry_name}")
            documents[parts[1]] = _decode_json_document(content, RecoveryError)
        if not _REQUIRED_RECOVERY_FILES.issubset(documents):
            raise RecoveryError("Recovery archive is missing essential runtime documents.")
        _validate_runtime_documents(documents, RecoveryError)
        backup_directory = (
            self.config.backup_dir
            / "recovery"
            / f"{_compact_timestamp(self._clock, RecoveryError)}-{uuid.uuid4().hex}"
        )
        backup_directory.mkdir(parents=True, exist_ok=False)
        existing: dict[str, bytes] = {}
        for name in _RUNTIME_DATA_FILES:
            current = self.config.data_dir / name
            if current.is_file():
                content = _read_bounded(current, RecoveryError)
                existing[name] = content
                _atomic_write_bytes(backup_directory / name, content)
        written: list[str] = []
        try:
            for name, document in documents.items():
                StateStore(self.config.data_dir / name).save(document)
                written.append(name)
        except Exception as exc:
            for name in written:
                current = self.config.data_dir / name
                if name in existing:
                    _atomic_write_bytes(current, existing[name])
                elif current.exists():
                    current.unlink()
            raise RecoveryError(
                f"Recovery restore failed and was rolled back: {type(exc).__name__}."
            ) from exc
        return RecoveryResult(source, len(documents), backup_directory)

    def _load_documents(self, *, require_essential: bool) -> dict[str, dict[str, Any]]:
        documents: dict[str, dict[str, Any]] = {}
        for name in _RUNTIME_DATA_FILES:
            path = self.config.data_dir / name
            if path.is_file():
                documents[name] = _decode_json_document(
                    _read_bounded(path, RecoveryError), RecoveryError
                )
        if require_essential and not _REQUIRED_RECOVERY_FILES.issubset(documents):
            raise RecoveryError("Runtime data is missing essential snapshot documents.")
        _validate_runtime_documents(documents, RecoveryError)
        return documents


class MigrationManager:
    """Migrate older schema-v1 layouts to the complete current runtime layout."""

    def __init__(
        self,
        config: AppConfig,
        *,
        clock: Callable[[], datetime] | None = None,
    ) -> None:
        self.config = config
        self._clock = clock or (lambda: datetime.now(UTC))

    def migrate(self, *, confirmed: bool = False) -> MigrationResult:
        defaults = _runtime_defaults(self.config.project_root, MigrationError)
        documents: dict[str, dict[str, Any]] = {}
        changed: list[str] = []
        for name in _RUNTIME_DATA_FILES:
            path = self.config.data_dir / name
            if path.is_file():
                documents[name] = _decode_json_document(
                    _read_bounded(path, MigrationError), MigrationError
                )
            else:
                documents[name] = defaults[name]
                changed.append(name)
        _validate_runtime_documents(documents, MigrationError)
        state = dict(documents["agent_state.json"])
        if state.get("application_version") != __version__:
            state["application_version"] = __version__
            documents["agent_state.json"] = state
            if "agent_state.json" not in changed:
                changed.append("agent_state.json")
        if not changed:
            return MigrationResult((), None)
        if not confirmed:
            raise MigrationError(
                "Runtime migration has pending changes and requires confirmation."
            )
        recovery_directory = self.config.backup_dir / "migrations"
        recovery_directory.mkdir(parents=True, exist_ok=True)
        timestamp = _compact_timestamp(self._clock, MigrationError)
        recovery_archive = (
            recovery_directory
            / f"pre-{__version__}-{timestamp}-{uuid.uuid4().hex}.zip"
        )
        RecoveryManager(self.config, clock=self._clock).create_snapshot(recovery_archive)
        original = {
            name: (
                _read_bounded(self.config.data_dir / name, MigrationError)
                if (self.config.data_dir / name).is_file()
                else None
            )
            for name in changed
        }
        written: list[str] = []
        try:
            for name in changed:
                StateStore(self.config.data_dir / name).save(documents[name])
                written.append(name)
        except Exception as exc:
            for name in written:
                target = self.config.data_dir / name
                if original[name] is None:
                    if target.exists():
                        target.unlink()
                else:
                    _atomic_write_bytes(target, original[name] or b"")
            raise MigrationError(
                f"Runtime migration failed and was rolled back: {type(exc).__name__}."
            ) from exc
        return MigrationResult(tuple(sorted(changed)), recovery_archive)


def _portable_data_templates(project_root: Path) -> dict[str, bytes]:
    defaults = _runtime_defaults(project_root, PackagingError)
    return {f"data/{name}": _json_bytes(value) for name, value in defaults.items()}


def _runtime_defaults(
    project_root: Path, error_type: type[Exception]
) -> dict[str, dict[str, Any]]:
    settings_path = project_root / "data" / "settings.json"
    if settings_path.is_file():
        settings = _decode_json_document(_read_bounded(settings_path, error_type), error_type)
    else:
        settings = {"schema_version": 1, "log_level": "INFO"}
    mission_path = project_root / "data" / "mission.json"
    if mission_path.is_file():
        mission = _decode_json_document(_read_bounded(mission_path, error_type), error_type)
    else:
        mission = {
            "schema_version": 1,
            "project": "AI-Engineer",
            "mission": "Safe local agent",
            "current_milestone": 23,
            "status": "active",
        }
    defaults = {
        "agent_state.json": {
            "schema_version": 1,
            "application_version": __version__,
            "status": "stopped",
            "start_count": 0,
            "last_started_at": None,
            "last_stopped_at": None,
        },
        "approvals.json": {"schema_version": 1, "approvals": []},
        "developer_runs.json": {"schema_version": 1, "runs": [], "events": []},
        "drafting_runs.json": {"schema_version": 1, "runs": []},
        "history.json": {"schema_version": 1, "tasks": [], "events": []},
        "mission.json": mission,
        "settings.json": settings,
    }
    _validate_runtime_documents(defaults, error_type)
    return defaults


def _validate_runtime_documents(
    documents: Mapping[str, Mapping[str, Any]], error_type: type[Exception]
) -> None:
    expected_collections = {
        "approvals.json": ("approvals",),
        "developer_runs.json": ("runs", "events"),
        "drafting_runs.json": ("runs",),
        "history.json": ("tasks", "events"),
    }
    for name, document in documents.items():
        if name not in _RUNTIME_DATA_FILES:
            raise error_type(f"Runtime data filename is not allowlisted: {name}")
        if document.get("schema_version") != 1:
            raise error_type(f"Runtime data must use schema_version 1: {name}")
        if _contains_secret_key(document):
            raise error_type(f"Runtime data contains a secret-like key: {name}")
        for key in expected_collections.get(name, ()):
            if not isinstance(document.get(key), list):
                raise error_type(f"Runtime data {name} must contain a {key} list.")
    state = documents.get("agent_state.json")
    if state is not None:
        start_count = state.get("start_count")
        if (
            not isinstance(state.get("application_version"), str)
            or not state["application_version"]
            or not isinstance(state.get("status"), str)
            or not state["status"]
            or not isinstance(start_count, int)
            or isinstance(start_count, bool)
            or start_count < 0
        ):
            raise error_type("Runtime agent_state fields are invalid.")
    settings = documents.get("settings.json")
    if settings is not None:
        log_level = settings.get("log_level")
        if not isinstance(log_level, str) or log_level.upper() not in {
            "DEBUG",
            "INFO",
            "WARNING",
            "ERROR",
            "CRITICAL",
        }:
            raise error_type("Runtime settings log_level is invalid.")
    mission = documents.get("mission.json")
    if mission is not None:
        milestone = mission.get("current_milestone")
        if (
            not isinstance(mission.get("project"), str)
            or not mission["project"]
            or not isinstance(mission.get("mission"), str)
            or not mission["mission"]
            or not isinstance(milestone, int)
            or isinstance(milestone, bool)
            or milestone < 0
            or not isinstance(mission.get("status"), str)
            or not mission["status"]
        ):
            raise error_type("Runtime mission fields are invalid.")


def _contains_secret_key(value: object) -> bool:
    if isinstance(value, Mapping):
        for key, item in value.items():
            normalized = str(key).casefold().replace("-", "_")
            if any(fragment in normalized for fragment in _SECRET_KEY_FRAGMENTS):
                return True
            if _contains_secret_key(item):
                return True
    elif isinstance(value, list):
        return any(_contains_secret_key(item) for item in value)
    return False


def _validated_archive(
    path: Path,
    *,
    manifest_name: str,
    expected_type: str,
    error_type: type[Exception],
) -> tuple[dict[str, bytes], dict[str, Any]]:
    try:
        with zipfile.ZipFile(path, "r") as archive:
            infos = archive.infolist()
            if not 1 <= len(infos) <= _MAX_ARCHIVE_ENTRIES:
                raise error_type("Archive file count is outside the safe limit.")
            names = [info.filename for info in infos]
            if len(names) != len(set(names)) or manifest_name not in names:
                raise error_type("Archive entries or manifest are invalid.")
            total = 0
            contents: dict[str, bytes] = {}
            for info in infos:
                _validate_archive_name(info.filename, error_type)
                mode = info.external_attr >> 16
                if stat.S_ISLNK(mode) or info.is_dir():
                    raise error_type("Archive links and directory entries are not allowed.")
                total += info.file_size
                if total > _MAX_ARCHIVE_BYTES:
                    raise error_type("Archive exceeds its uncompressed byte limit.")
                contents[info.filename] = archive.read(info)
    except (OSError, zipfile.BadZipFile, RuntimeError) as exc:
        if isinstance(exc, error_type):
            raise
        raise error_type(f"Cannot read validated archive: {type(exc).__name__}.") from exc
    manifest = _decode_json_document(contents.pop(manifest_name), error_type)
    if (
        manifest.get("schema_version") != 1
        or manifest.get("package_type") != expected_type
        or not isinstance(manifest.get("application_version"), str)
        or not manifest["application_version"]
        or not isinstance(manifest.get("files"), dict)
    ):
        raise error_type("Archive manifest type or schema is invalid.")
    hashes = manifest["files"]
    if any(
        not isinstance(name, str)
        or not isinstance(digest, str)
        or re.fullmatch(r"[0-9a-f]{64}", digest) is None
        for name, digest in hashes.items()
    ):
        raise error_type("Archive manifest contains invalid SHA-256 records.")
    if set(hashes) != set(contents):
        raise error_type("Archive manifest file list does not match its contents.")
    for name, content in contents.items():
        if hashes.get(name) != _sha256(content):
            raise error_type(f"Archive content hash mismatch: {name}")
    return contents, manifest


def _validate_archive_name(name: str, error_type: type[Exception]) -> None:
    path = PurePosixPath(name)
    if (
        not name
        or "\\" in name
        or path.is_absolute()
        or ".." in path.parts
        or any(not part or part in {".", ".."} for part in path.parts)
    ):
        raise error_type(f"Archive entry path is unsafe: {name}")


def _validate_source_path(path: Path, project_root: Path) -> None:
    try:
        resolved = path.resolve(strict=True)
        resolved.relative_to(project_root)
    except (OSError, ValueError) as exc:
        raise PackagingError(f"Package source escapes the project root: {path.name}") from exc
    if path.is_symlink() or (
        hasattr(path, "is_junction") and path.is_junction()
    ):
        raise PackagingError(f"Package source links are not allowed: {path.name}")


def _write_archive_atomic(
    target: Path, entries: Mapping[str, bytes], error_type: type[Exception]
) -> None:
    temporary: Path | None = None
    try:
        descriptor, name = tempfile.mkstemp(
            dir=target.parent, prefix=f".{target.name}.", suffix=".tmp"
        )
        os.close(descriptor)
        temporary = Path(name)
        with zipfile.ZipFile(
            temporary, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9
        ) as archive:
            for entry_name in sorted(entries):
                _write_zip_entry(archive, entry_name, entries[entry_name])
        _flush_file(temporary)
        os.replace(temporary, target)
    except OSError as exc:
        raise error_type(f"Cannot write atomic archive: {type(exc).__name__}.") from exc
    finally:
        if temporary is not None and temporary.exists():
            temporary.unlink()


def _write_zip_entry(archive: zipfile.ZipFile, name: str, content: bytes) -> None:
    _validate_archive_name(name, PackagingError)
    info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
    info.compress_type = zipfile.ZIP_DEFLATED
    info.external_attr = (stat.S_IFREG | 0o644) << 16
    archive.writestr(info, content)


def _read_bounded(
    path: Path,
    error_type: type[Exception],
    maximum_bytes: int = 8_388_608,
) -> bytes:
    try:
        size = path.stat().st_size
        if size < 0 or size > maximum_bytes:
            raise error_type(f"File exceeds its byte limit: {path.name}")
        return path.read_bytes()
    except OSError as exc:
        raise error_type(
            f"Cannot read bounded file {path.name}: {type(exc).__name__}."
        ) from exc


def _decode_json_document(content: bytes, error_type: type[Exception]) -> dict[str, Any]:
    try:
        value = json.loads(content.decode("utf-8"))
    except (UnicodeError, json.JSONDecodeError) as exc:
        raise error_type("Runtime document must contain valid UTF-8 JSON.") from exc
    if not isinstance(value, dict):
        raise error_type("Runtime document root must be a JSON object.")
    return value


def _json_bytes(value: Mapping[str, Any]) -> bytes:
    return (json.dumps(dict(value), indent=2, ensure_ascii=False) + "\n").encode("utf-8")


def _atomic_write_bytes(path: Path, content: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, name = tempfile.mkstemp(
        dir=path.parent, prefix=f".{path.name}.", suffix=".tmp"
    )
    temporary = Path(name)
    try:
        with os.fdopen(descriptor, "wb") as handle:
            handle.write(content)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary, path)
    finally:
        if temporary.exists():
            temporary.unlink()


def _flush_file(path: Path) -> None:
    descriptor = os.open(path, os.O_RDWR)
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


def _sha256(content: bytes) -> str:
    return hashlib.sha256(content).hexdigest()


def _timestamp(
    clock: Callable[[], datetime], error_type: type[Exception]
) -> str:
    value = clock()
    if value.tzinfo is None:
        raise error_type("Operation clock must return timezone-aware values.")
    return value.astimezone(UTC).isoformat()


def _compact_timestamp(
    clock: Callable[[], datetime], error_type: type[Exception]
) -> str:
    value = clock()
    if value.tzinfo is None:
        raise error_type("Operation clock must return timezone-aware values.")
    return value.astimezone(UTC).strftime("%Y%m%dT%H%M%S%fZ")
