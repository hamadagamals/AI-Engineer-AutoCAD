"""Build a bounded continuation bundle around the sanitized portable distribution."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import tempfile
import zipfile
from pathlib import Path

from agent.exceptions import PackagingError
from core.package_manager import PackageBuilder

_PANEL_FOLDER = "hutchison-ports-panel-ga-20260914"
_ASSET_SUFFIXES = frozenset({".dwg", ".lsp", ".csv", ".json", ".md"})
_MAX_BYTES = 134_217_728


def build_bundle(project_root: Path, destination: Path) -> dict[str, object]:
    """Package current source/tests and only the reviewed panel project; no sessions."""
    root = project_root.resolve(strict=True)
    target = destination.resolve(strict=False)
    if target.exists():
        raise PackagingError("Transfer destination already exists; refusing overwrite.")
    target.parent.mkdir(parents=True, exist_ok=True)
    entries: dict[str, bytes] = {}

    def add(source: Path, name: str) -> None:
        if source.is_symlink() or not source.resolve(strict=True).is_relative_to(root):
            raise PackagingError("Transfer source escapes project root or is linked.")
        if not source.is_file() or source.stat().st_size > _MAX_BYTES:
            raise PackagingError("Transfer source is missing or exceeds its size limit.")
        if name in entries:
            raise PackagingError("Duplicate transfer entry.")
        entries[name] = source.read_bytes()

    guide = root / "docs" / "transfer-20260917"
    add(guide / "START-HERE-AR.md", "START-HERE-AR.md")
    add(guide / "INSTALL-TRANSFER.ps1", "INSTALL-TRANSFER.ps1")
    test_root = root / "tests"
    for source in sorted(test_root.rglob("*")):
        if source.is_file() and "__pycache__" not in source.parts and source.suffix not in {".pyc", ".pyo"}:
            add(source, "tests/" + source.relative_to(test_root).as_posix())
    assets = root / "workspace" / "reports" / _PANEL_FOLDER
    if not (assets / "HUTCHISON_PANEL_GA_PREVIEW_R03.dwg").is_file():
        raise PackagingError("Current reviewed R03 drawing is missing.")
    for source in sorted(assets.rglob("*")):
        if source.is_file() and source.suffix.casefold() in _ASSET_SUFFIXES:
            add(source, "project-assets/" + _PANEL_FOLDER + "/" + source.relative_to(assets).as_posix())
    if not entries or len(entries) > 2_000:
        raise PackagingError("Transfer bundle exceeds its entry limit.")
    with tempfile.TemporaryDirectory(prefix="ai-engineer-transfer-", dir=target.parent) as temporary:
        staging = Path(temporary).resolve(strict=True)
        if not staging.is_relative_to(target.parent):
            raise PackagingError("Invalid transfer staging directory.")
        portable = staging / "AI-Engineer-portable-r50.zip"
        PackageBuilder(root).build(portable)
        entries[portable.name] = portable.read_bytes()
        if sum(map(len, entries.values())) > _MAX_BYTES:
            raise PackagingError("Transfer bundle exceeds its byte limit.")
        manifest = {
            "schema_version": 1,
            "bundle_type": "ai-engineer-continuation-r50",
            "checkpoint_date": "2026-09-19",
            "files": {name: hashlib.sha256(content).hexdigest() for name, content in sorted(entries.items())},
            "excluded": ["provider credentials", "runtime session history", "unrelated workspace projects", "Git history", "machine-built DLL"],
            "component_move_execution_enabled": False,
        }
        entries["TRANSFER-MANIFEST.json"] = json.dumps(manifest, ensure_ascii=False, indent=2).encode("utf-8")
        pending = staging / "transfer.zip"
        with zipfile.ZipFile(pending, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
            for name, content in sorted(entries.items()):
                archive.writestr(name, content)
        # Windows rename refuses an existing destination, including a concurrent creation.
        os.rename(pending, target)
    with zipfile.ZipFile(target) as archive:
        if archive.testzip() is not None:
            raise PackagingError("Transfer ZIP integrity check failed.")
    return {"archive": str(target), "bytes": target.stat().st_size,
            "files": len(entries), "sha256": hashlib.sha256(target.read_bytes()).hexdigest()}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    arguments = parser.parse_args()
    # Keep CLI output compatible with legacy Windows console code pages even when
    # the destination path contains Arabic or other non-ASCII characters.
    print(json.dumps(build_bundle(Path(__file__).resolve().parents[1], arguments.output), ensure_ascii=True))


if __name__ == "__main__":
    main()
