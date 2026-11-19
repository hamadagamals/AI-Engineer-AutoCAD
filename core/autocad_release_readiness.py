"""Read-only AutoCAD product-release readiness inspection for Windows."""

from __future__ import annotations

import hashlib
import os
import platform
import socket
import sys
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Callable, Mapping

from agent.exceptions import PackagingError


READINESS_VERSION = "autocad.release_readiness.v2"
SUPPORTED_AUTOCAD_YEARS = (2021, 2023)
_AUTOCAD_RELEASES = {2021: "24.0", 2023: "24.2"}
AUTOCAD_PRODUCT_FILES = (
    "CHECK-AI-ENGINEER-AUTOCAD-2023.ps1",
    "START-AI-ENGINEER-AUTOCAD-2023.ps1",
    "START-AI-ENGINEER-AUTOCAD-PREVIEW.ps1",
    "START-AI-ENGINEER-AUTOCAD-COMPLIANCE.ps1",
    "START-AI-ENGINEER-AUTOCAD-MANUFACTURER.ps1",
    "START-AI-ENGINEER-AUTOCAD-DISCOVERY.ps1",
    "integrations/autocad_context_plugin/build.ps1",
    "integrations/autocad_context_plugin/ContextCommands.cs",
    "integrations/autocad_context_plugin/ConnectionReviewChannel.cs",
    "integrations/autocad_context_plugin/DrawingChangeTracker.cs",
    "integrations/autocad_context_plugin/OperatorCommandChannel.cs",
    "integrations/autocad_context_plugin/PaletteCommands.cs",
    "integrations/autocad_drawing_delta.py",
    "integrations/autocad_drawing_index.py",
    "integrations/autocad_drawing_inventory.py",
    "integrations/autocad_inventory_delta.py",
    "integrations/autocad_inventory_refresh.py",
    "integrations/autocad_operator_channel.py",
    "integrations/autocad_review_channel.py",
    "integrations/manufacturer_templates/free-candidates.template.json",
)
_AUTOCAD_FILES = (
    "acad.exe",
    "accoreconsole.exe",
    "acmgd.dll",
    "acdbmgd.dll",
    "accoremgd.dll",
    "AcWindows.dll",
)
_FRAMEWORK_VERSIONS = {2021: ("v4.7.2",), 2023: ("v4.8", "v4.7.2")}
PortProbe = Callable[[int], bool]


@dataclass(frozen=True, slots=True)
class ProductFileIdentity:
    relative_path: str
    sha256: str


@dataclass(frozen=True, slots=True)
class AutoCADReadinessReport:
    schema_version: int
    readiness_version: str
    complete: bool
    platform: str
    python_version: str
    autocad_year: int
    expected_acadver: str
    autocad_root: str
    framework_reference_version: str | None
    port: int
    port_available: bool
    product_files: tuple[ProductFileIdentity, ...]
    missing_autocad_files: tuple[str, ...]
    missing_build_files: tuple[str, ...]
    findings: tuple[str, ...]
    provider_egress_performed: bool
    drawing_mutation_performed: bool

    def to_dict(self) -> dict[str, object]:
        return asdict(self)


def inspect_autocad_release_readiness(
    project_root: Path,
    *,
    autocad_root: Path | None = None,
    autocad_year: int = 2021,
    port: int = 8765,
    environment: Mapping[str, str] | None = None,
    platform_name: str | None = None,
    python_version: tuple[int, int, int] | None = None,
    port_probe: PortProbe | None = None,
) -> AutoCADReadinessReport:
    """Inspect fixed local prerequisites without starting services or AutoCAD."""
    if autocad_year not in SUPPORTED_AUTOCAD_YEARS:
        raise PackagingError("Supported AutoCAD years are 2021 and 2023.")
    if not isinstance(port, int) or isinstance(port, bool) or not 1024 <= port <= 65535:
        raise PackagingError("AutoCAD readiness port must be from 1024 through 65535.")
    root = project_root.expanduser().resolve(strict=True)
    if not root.is_dir():
        raise PackagingError("AutoCAD readiness project root must be a directory.")
    values = os.environ if environment is None else environment
    system = platform.system() if platform_name is None else platform_name
    version = sys.version_info[:3] if python_version is None else python_version
    if len(version) != 3 or any(not isinstance(item, int) for item in version):
        raise PackagingError("AutoCAD readiness Python version is invalid.")

    program_files = Path(values.get("ProgramFiles", r"C:\Program Files"))
    acad_root = (
        program_files / "Autodesk" / f"AutoCAD {autocad_year}"
        if autocad_root is None
        else autocad_root.expanduser()
    ).resolve(strict=False)
    missing_autocad = tuple(
        name for name in _AUTOCAD_FILES if not (acad_root / name).is_file()
    )

    windows_root = Path(values.get("WINDIR", r"C:\Windows"))
    program_files_x86 = Path(values.get("ProgramFiles(x86)", r"C:\Program Files (x86)"))
    compiler = windows_root / "Microsoft.NET" / "Framework64" / "v4.0.30319" / "csc.exe"
    framework_root = (
        program_files_x86 / "Reference Assemblies" / "Microsoft" / "Framework" / ".NETFramework"
    )
    framework_version = next(
        (
            candidate
            for candidate in _FRAMEWORK_VERSIONS[autocad_year]
            if (framework_root / candidate / "PresentationCore.dll").is_file()
            and (framework_root / candidate / "WindowsBase.dll").is_file()
        ),
        None,
    )
    expected_framework = _FRAMEWORK_VERSIONS[autocad_year][0]
    reference_root = framework_root / (framework_version or expected_framework)
    build_paths = (
        compiler,
        reference_root / "PresentationCore.dll",
        reference_root / "WindowsBase.dll",
    )
    missing_build = tuple(str(path) for path in build_paths if not path.is_file())

    identities: list[ProductFileIdentity] = []
    findings: list[str] = []
    for relative in AUTOCAD_PRODUCT_FILES:
        path = root / Path(relative)
        if not path.is_file() or path.is_symlink():
            findings.append(f"FAIL | product-file:{relative} | Missing or unsafe product file.")
            continue
        identities.append(ProductFileIdentity(relative, _sha256(path)))
    platform_ok = system == "Windows"
    python_ok = version[:2] == (3, 13)
    port_available = (port_probe or _port_available)(port)
    findings.extend((
        f"{'PASS' if platform_ok else 'FAIL'} | platform | {system}",
        f"{'PASS' if python_ok else 'FAIL'} | python | {'.'.join(map(str, version))}",
        f"{'PASS' if not missing_autocad else 'FAIL'} | autocad-{autocad_year}-files | "
        f"{len(_AUTOCAD_FILES) - len(missing_autocad)}/{len(_AUTOCAD_FILES)} present",
        f"{'PASS' if not missing_build else 'FAIL'} | build-files | "
        f"{len(build_paths) - len(missing_build)}/{len(build_paths)} present",
        f"{'PASS' if port_available else 'FAIL'} | localhost-port | {port} "
        f"{'available' if port_available else 'unavailable'}",
    ))
    complete = all((
        platform_ok,
        python_ok,
        not missing_autocad,
        not missing_build,
        len(identities) == len(AUTOCAD_PRODUCT_FILES),
        port_available,
    ))
    return AutoCADReadinessReport(
        1,
        READINESS_VERSION,
        complete,
        system,
        ".".join(map(str, version)),
        autocad_year,
        _AUTOCAD_RELEASES[autocad_year],
        str(acad_root),
        framework_version,
        port,
        port_available,
        tuple(identities),
        missing_autocad,
        missing_build,
        tuple(findings),
        False,
        False,
    )


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while block := stream.read(65_536):
            digest.update(block)
    return digest.hexdigest()


def _port_available(port: int) -> bool:
    probe = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        probe.bind(("127.0.0.1", port))
        return True
    except OSError:
        return False
    finally:
        probe.close()
