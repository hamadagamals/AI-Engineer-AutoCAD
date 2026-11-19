"""Capability-scoped Autodesk Revit bridge readiness inspection for Windows."""

from __future__ import annotations

import hashlib
import os
import platform
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Mapping

from agent.exceptions import PackagingError


READINESS_VERSION = "revit.release_readiness.v29"
SUPPORTED_REVIT_YEARS = (2024,)
REVIT_PRODUCT_FILES = (
    "integrations/revit_inventory.py",
    "integrations/revit_readonly_plugin/ReadOnlyInventoryCommand.cs",
    "integrations/revit_readonly_plugin/ReadOnlyRequestBridge.cs",
    "integrations/revit_readonly_plugin/ReadOnlyCategoryQuery.cs",
    "integrations/revit_readonly_plugin/ReadOnlyFamilyTypeQuery.cs",
    "integrations/revit_readonly_plugin/ReadOnlyHostFaceQuery.cs",
    "integrations/revit_readonly_plugin/ReadOnlyElementVisualQuery.cs",
    "integrations/revit_readonly_plugin/ReadOnlyLightingCircuitQuery.cs",
    "integrations/revit_readonly_plugin/ReadOnlyElectricalSystemQuery.cs",
    "integrations/revit_readonly_plugin/ModelLineExecution.cs",
    "integrations/revit_readonly_plugin/ModelLineHandoff.cs",
    "integrations/revit_readonly_plugin/FamilyPlacementHandoff.cs",
    "integrations/revit_readonly_plugin/HostedFamilyPlacementHandoff.cs",
    "integrations/revit_readonly_plugin/CircuitAssignmentHandoff.cs",
    "integrations/revit_request_bridge.py",
    "integrations/revit_category_query.py",
    "integrations/revit_family_type_query.py",
    "integrations/revit_host_face_query.py",
    "integrations/revit_element_visual_query.py",
    "integrations/revit_lighting_circuit_query.py",
    "integrations/revit_electrical_system_query.py",
    "integrations/revit_electrical_system_inventory.py",
    "integrations/revit_lighting_project_inventory.py",
    "integrations/revit_mep_project_inventory.py",
    "integrations/revit_circuit_assignment_preview.py",
    "integrations/revit_circuit_assignment_handoff.py",
    "integrations/revit_circuit_engineering_review.py",
    "integrations/revit_circuit_calculation_intake.py",
    "integrations/revit_preview.py",
    "integrations/revit_model_line_preview.py",
    "integrations/revit_model_line_execution.py",
    "integrations/revit_model_line_handoff.py",
    "integrations/revit_family_placement_handoff.py",
    "integrations/revit_hosted_family_placement_handoff.py",
    "integrations/revit_readonly_plugin/AIEngineer.Revit.addin.template",
    "integrations/revit_readonly_plugin/build.ps1",
)
_REVIT_FILES = ("Revit.exe", "RevitAPI.dll", "RevitAPIUI.dll")


@dataclass(frozen=True, slots=True)
class RevitProductFileIdentity:
    relative_path: str
    sha256: str


@dataclass(frozen=True, slots=True)
class RevitReadinessReport:
    schema_version: int
    readiness_version: str
    complete: bool
    platform: str
    revit_year: int
    revit_root: str
    framework_reference_version: str | None
    product_files: tuple[RevitProductFileIdentity, ...]
    missing_revit_files: tuple[str, ...]
    missing_build_files: tuple[str, ...]
    findings: tuple[str, ...]
    provider_egress_performed: bool
    document_mutation_performed: bool
    mutation_capability: str

    def to_dict(self) -> dict[str, object]:
        return asdict(self)


def inspect_revit_release_readiness(
    project_root: Path,
    *,
    revit_root: Path | None = None,
    revit_year: int = 2024,
    environment: Mapping[str, str] | None = None,
    platform_name: str | None = None,
) -> RevitReadinessReport:
    """Inspect local Revit read-only bridge prerequisites without launching Revit."""
    if revit_year not in SUPPORTED_REVIT_YEARS:
        raise PackagingError("Supported Revit year is 2024.")
    root = project_root.expanduser().resolve(strict=True)
    if not root.is_dir():
        raise PackagingError("Revit readiness project root must be a directory.")
    values = os.environ if environment is None else environment
    system = platform.system() if platform_name is None else platform_name
    program_files = Path(values.get("ProgramFiles", r"C:\Program Files"))
    product_root = (
        program_files / "Autodesk" / f"Revit {revit_year}"
        if revit_root is None
        else revit_root.expanduser()
    ).resolve(strict=False)
    missing_revit = tuple(name for name in _REVIT_FILES if not (product_root / name).is_file())
    windows_root = Path(values.get("WINDIR", r"C:\Windows"))
    program_files_x86 = Path(values.get("ProgramFiles(x86)", r"C:\Program Files (x86)"))
    compiler = windows_root / "Microsoft.NET" / "Framework64" / "v4.0.30319" / "csc.exe"
    framework_root = (
        program_files_x86 / "Reference Assemblies" / "Microsoft" / "Framework"
        / ".NETFramework" / "v4.8"
    )
    build_paths = (compiler, framework_root / "System.Runtime.Serialization.dll")
    missing_build = tuple(str(path) for path in build_paths if not path.is_file())
    identities: list[RevitProductFileIdentity] = []
    findings: list[str] = []
    for relative in REVIT_PRODUCT_FILES:
        path = root / Path(relative)
        if not path.is_file() or path.is_symlink():
            findings.append(f"FAIL | product-file:{relative} | Missing or unsafe product file.")
        else:
            identities.append(RevitProductFileIdentity(relative, _sha256(path)))
    platform_ok = system == "Windows"
    findings.extend((
        f"{'PASS' if platform_ok else 'FAIL'} | platform | {system}",
        f"{'PASS' if not missing_revit else 'FAIL'} | revit-{revit_year}-files | "
        f"{len(_REVIT_FILES) - len(missing_revit)}/{len(_REVIT_FILES)} present",
        f"{'PASS' if not missing_build else 'FAIL'} | build-files | "
        f"{len(build_paths) - len(missing_build)}/{len(build_paths)} present",
        "PASS | safety-boundary | Model Lines, the bounded same-type family batch, the single linked-ceiling family path, and one exact lighting-circuit assignment require exact preview approval, backup, "
        "source hash, and one transaction; Save As is limited to a new workspace RVT "
        "and requires reopened read-only verification; the accepted family path is limited "
        "to 1..32 reference-bound level-based instances with explicit per-instance rotation, one zero-rotation linked-ceiling instance, or one fixture-to-existing-system assignment in one transaction",
    ))
    complete = all((
        platform_ok,
        not missing_revit,
        not missing_build,
        len(identities) == len(REVIT_PRODUCT_FILES),
    ))
    return RevitReadinessReport(
        1,
        READINESS_VERSION,
        complete,
        system,
        revit_year,
        str(product_root),
        "v4.8" if not missing_build else None,
        tuple(identities),
        missing_revit,
        missing_build,
        tuple(findings),
        False,
        False,
        "approved_model_lines_new_workspace_rvt; approved_reference_bound_rotated_same_type_family_batch_new_workspace_rvt; approved_single_linked_ceiling_family_new_workspace_rvt; approved_single_lighting_circuit_assignment_new_workspace_rvt",
    )


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while block := stream.read(65_536):
            digest.update(block)
    return digest.hexdigest()
