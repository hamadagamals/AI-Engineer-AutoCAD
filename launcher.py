"""Fixed-command operator launcher for runtime, packaging, migration, and recovery."""

from __future__ import annotations

import argparse
import json
import sys
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

from agent.config import AppConfig
from agent.exceptions import AIEngineerError, PackagingError
from agent.state import StateStore
from agent.version import __version__
from core.package_manager import (
    MigrationManager,
    PackageBuilder,
    PackageInstaller,
    RecoveryManager,
)
from core.autocad_release_readiness import (
    AUTOCAD_PRODUCT_FILES,
    inspect_autocad_release_readiness,
)
from core.revit_release_readiness import inspect_revit_release_readiness


def _root() -> Path:
    return Path(__file__).resolve().parent


def _default_archive(directory: str, prefix: str) -> Path:
    timestamp = datetime.now(UTC).strftime("%Y%m%dT%H%M%SZ")
    return _root() / directory / f"{prefix}-{__version__}-{timestamp}.zip"


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="AI-Engineer safe local launcher")
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("check", help="validate Python, configuration, and runtime JSON")
    readiness = commands.add_parser(
        "autocad-readiness", help="inspect read-only AutoCAD release prerequisites"
    )
    readiness.add_argument("--port", type=int, default=8765)
    readiness.add_argument("--autocad-root", type=Path)
    readiness.add_argument("--autocad-year", type=int, choices=(2021, 2023), default=2021)
    revit_readiness = commands.add_parser(
        "revit-readiness", help="inspect the read-only Revit bridge prerequisites"
    )
    revit_readiness.add_argument("--revit-root", type=Path)
    revit_readiness.add_argument("--revit-year", type=int, choices=(2024,), default=2024)
    revit_read = commands.add_parser(
        "revit-read", help="submit one correlated read-only inventory request to live Revit"
    )
    revit_read.add_argument("document_title")
    revit_read.add_argument("--timeout", type=float, default=30.0)
    revit_category = commands.add_parser(
        "revit-read-category", help="read bounded element summaries from an allowlisted Revit category"
    )
    revit_category.add_argument("document_title")
    revit_category.add_argument("category")
    revit_category.add_argument("--limit", type=int, default=32)
    revit_category.add_argument("--start-after-element-id", type=int, default=0)
    revit_category.add_argument("--hosted-only", action="store_true")
    revit_category.add_argument("--ceiling-hosted-only", action="store_true")
    revit_category.add_argument("--timeout", type=float, default=30.0)
    revit_families = commands.add_parser(
        "revit-read-family-types",
        help="read bounded loaded electrical family types and placement requirements",
    )
    revit_families.add_argument("document_title")
    revit_families.add_argument("category")
    revit_families.add_argument("--limit", type=int, default=64)
    revit_families.add_argument("--timeout", type=float, default=30.0)
    revit_face = commands.add_parser(
        "revit-read-host-face",
        help="read bounded interior candidates from one exact linked ceiling face",
    )
    revit_face.add_argument("document_title")
    revit_face.add_argument("reference_element_id", type=int)
    revit_face.add_argument("face_stable_reference")
    revit_face.add_argument("--limit", type=int, default=8)
    revit_face.add_argument("--timeout", type=float, default=30.0)
    revit_focus = commands.add_parser(
        "revit-focus-element",
        help="focus and inspect one exact Revit lighting fixture without model mutation",
    )
    revit_focus.add_argument("document_title")
    revit_focus.add_argument("element_id", type=int)
    revit_focus.add_argument("--timeout", type=float, default=30.0)
    revit_circuits = commands.add_parser(
        "revit-read-lighting-circuits",
        help="read bounded lighting-fixture electrical-system relationships",
    )
    revit_circuits.add_argument("document_title")
    revit_circuits.add_argument("--element-id", type=int, default=0)
    revit_circuits.add_argument("--start-after-element-id", type=int, default=0)
    revit_circuits.add_argument("--limit", type=int, default=32)
    revit_circuits.add_argument("--timeout", type=float, default=30.0)
    revit_systems = commands.add_parser(
        "revit-read-electrical-systems",
        help="read one bounded ordered page of all Revit electrical systems",
    )
    revit_systems.add_argument("document_title")
    revit_systems.add_argument("--start-after-element-id", type=int, default=0)
    revit_systems.add_argument("--limit", type=int, default=32)
    revit_systems.add_argument("--timeout", type=float, default=30.0)
    revit_project_inventory = commands.add_parser(
        "revit-build-lighting-project-inventory",
        help="merge complete ordered Revit lighting-circuit pages into one source-bound inventory",
    )
    revit_project_inventory.add_argument("source_model", type=Path)
    revit_project_inventory.add_argument("output_inventory", type=Path)
    revit_project_inventory.add_argument("page", type=Path, nargs="+")
    revit_mep_inventory = commands.add_parser(
        "revit-build-mep-project-inventory",
        help="merge complete ordered Revit MEP category pages into one source-bound inventory",
    )
    revit_mep_inventory.add_argument("source_model", type=Path)
    revit_mep_inventory.add_argument("output_inventory", type=Path)
    revit_mep_inventory.add_argument("page_root", type=Path)
    revit_mep_inventory.add_argument("page", type=Path, nargs="+")
    revit_system_inventory = commands.add_parser(
        "revit-build-electrical-system-inventory",
        help="merge complete ordered Revit electrical-system pages into one inventory",
    )
    revit_system_inventory.add_argument("source_model", type=Path)
    revit_system_inventory.add_argument("output_inventory", type=Path)
    revit_system_inventory.add_argument("page_root", type=Path)
    revit_system_inventory.add_argument("page", type=Path, nargs="+")
    revit_circuit_preview = commands.add_parser(
        "revit-preview-circuit-assignment",
        help="create a non-executable exact Revit circuit-assignment review preview",
    )
    revit_circuit_preview.add_argument("source_model", type=Path)
    revit_circuit_preview.add_argument("fixture_query", type=Path)
    revit_circuit_preview.add_argument("reference_query", type=Path)
    revit_circuit_preview.add_argument("target_system_id", type=int)
    revit_circuit_preview.add_argument("output_preview", type=Path)
    revit_circuit_review = commands.add_parser(
        "revit-review-lighting-circuit",
        help="create a deterministic non-compliance Revit circuit engineering review",
    )
    revit_circuit_review.add_argument("source_model", type=Path)
    revit_circuit_review.add_argument("approved_preview", type=Path)
    revit_circuit_review.add_argument("post_assignment_query", type=Path)
    revit_circuit_review.add_argument("output_review", type=Path)
    revit_circuit_intake = commands.add_parser(
        "revit-build-circuit-calculation-intake",
        help="build a fail-closed reviewed-input intake from a Revit circuit review",
    )
    revit_circuit_intake.add_argument("engineering_review", type=Path)
    revit_circuit_intake.add_argument("output_intake", type=Path)
    revit_circuit_handoff = commands.add_parser(
        "revit-assign-lighting-circuit",
        help="assign one approved fixture to one existing circuit in a new workspace RVT",
    )
    revit_circuit_handoff.add_argument("source_model", type=Path)
    revit_circuit_handoff.add_argument("preview", type=Path)
    revit_circuit_handoff.add_argument("output_model", type=Path)
    revit_circuit_handoff.add_argument("--confirm-preview-sha256", required=True)
    revit_circuit_handoff.add_argument("--timeout", type=float, default=60.0)
    revit_circuit_verify = commands.add_parser(
        "revit-verify-lighting-circuit",
        help="verify a reopened lighting-circuit handoff without mutation",
    )
    revit_circuit_verify.add_argument("handoff_report", type=Path)
    revit_circuit_verify.add_argument("--timeout", type=float, default=30.0)
    revit_handoff = commands.add_parser(
        "revit-save-model-lines",
        help="execute one exact approved Model Lines preview and Save As a new workspace RVT",
    )
    revit_handoff.add_argument("source_model", type=Path)
    revit_handoff.add_argument("preview", type=Path)
    revit_handoff.add_argument("output_model", type=Path)
    revit_handoff.add_argument("--confirm-preview-sha256", required=True)
    revit_handoff.add_argument("--timeout", type=float, default=60.0)
    revit_verify = commands.add_parser(
        "revit-verify-model-lines",
        help="verify the reopened Model Lines handoff without mutation",
    )
    revit_verify.add_argument("handoff_report", type=Path)
    revit_verify.add_argument("--timeout", type=float, default=30.0)
    revit_family_handoff = commands.add_parser(
        "revit-place-family",
        help="place 1..32 approved same-type level-based families in a new workspace RVT",
    )
    revit_family_handoff.add_argument("source_model", type=Path)
    revit_family_handoff.add_argument("preview", type=Path)
    revit_family_handoff.add_argument("output_model", type=Path)
    revit_family_handoff.add_argument("--confirm-preview-sha256", required=True)
    revit_family_handoff.add_argument("--timeout", type=float, default=60.0)
    revit_family_verify = commands.add_parser(
        "revit-verify-family-placement",
        help="verify a reopened family placement handoff without mutation",
    )
    revit_family_verify.add_argument("handoff_report", type=Path)
    revit_family_verify.add_argument("--timeout", type=float, default=30.0)
    revit_hosted_handoff = commands.add_parser(
        "revit-place-hosted-family",
        help="place one approved linked-ceiling family in a new workspace RVT",
    )
    revit_hosted_handoff.add_argument("source_model", type=Path)
    revit_hosted_handoff.add_argument("preview", type=Path)
    revit_hosted_handoff.add_argument("output_model", type=Path)
    revit_hosted_handoff.add_argument("--confirm-preview-sha256", required=True)
    revit_hosted_handoff.add_argument("--timeout", type=float, default=60.0)
    revit_hosted_verify = commands.add_parser(
        "revit-verify-hosted-family-placement",
        help="verify a reopened linked-ceiling family handoff without mutation",
    )
    revit_hosted_verify.add_argument("handoff_report", type=Path)
    revit_hosted_verify.add_argument("--timeout", type=float, default=30.0)
    benchmark = commands.add_parser(
        "autocad-benchmark", help="measure deterministic local AutoCAD-core performance"
    )
    benchmark.add_argument("--runs", type=int, default=5)
    benchmark.add_argument("--output", type=Path)
    commands.add_parser("run", help="run the local lifecycle entry point")
    api = commands.add_parser("api", help="run or validate the protected localhost API")
    api.add_argument("--check", action="store_true")
    operator = commands.add_parser(
        "operator", help="run the local AI operator bridge for IDE/CAD sidecar clients"
    )
    operator.add_argument("--check", action="store_true")
    operator.add_argument("--allow-project-root", action="store_true")
    package = commands.add_parser("package", help="build a new portable ZIP package")
    package.add_argument("--output", type=Path)
    snapshot = commands.add_parser("snapshot", help="create a runtime recovery snapshot")
    snapshot.add_argument("--output", type=Path)
    restore = commands.add_parser("restore", help="restore a validated recovery snapshot")
    restore.add_argument("archive", type=Path)
    restore.add_argument("--confirm", action="store_true")
    migrate = commands.add_parser("migrate", help="migrate the runtime data layout")
    migrate.add_argument("--confirm", action="store_true")
    install = commands.add_parser("install", help="install a portable archive")
    install.add_argument("archive", type=Path)
    install.add_argument("destination", type=Path)
    install.add_argument("--confirm", action="store_true")
    return parser


def _check() -> dict[str, Any]:
    if sys.version_info[:2] != (3, 13):
        raise PackagingError("AI-Engineer requires Python 3.13.x.")
    config = AppConfig.load(_root())
    files = (
        config.state_file,
        config.mission_file,
        config.history_file,
        config.approvals_file,
        config.developer_runs_file,
        config.drafting_runs_file,
        config.data_dir / "settings.json",
    )
    for path in files:
        document = StateStore(path).load()
        if document.get("schema_version") != 1:
            raise PackagingError(f"Runtime JSON has an unsupported schema: {path.name}")
    for relative in AUTOCAD_PRODUCT_FILES:
        path = _root() / relative
        if not path.is_file() or path.is_symlink():
            raise PackagingError(f"Required AutoCAD product file is missing or unsafe: {relative}")
    return {
        "status": "ok",
        "version": __version__,
        "python": ".".join(str(item) for item in sys.version_info[:3]),
        "runtime_documents": len(files),
        "autocad_product_files": len(AUTOCAD_PRODUCT_FILES),
    }


def main(argv: list[str] | None = None) -> int:
    """Dispatch one allowlisted operation and return a process exit code."""
    arguments = _parser().parse_args(argv)
    try:
        if arguments.command == "check":
            result: object = _check()
        elif arguments.command == "autocad-readiness":
            result = inspect_autocad_release_readiness(
                _root(), autocad_root=arguments.autocad_root,
                autocad_year=arguments.autocad_year, port=arguments.port
            ).to_dict()
        elif arguments.command == "revit-readiness":
            result = inspect_revit_release_readiness(
                _root(), revit_root=arguments.revit_root,
                revit_year=arguments.revit_year,
            ).to_dict()
        elif arguments.command == "revit-read":
            from integrations.revit_inventory import load_revit_inventory
            from integrations.revit_request_bridge import (
                default_bridge_root,
                submit_inventory_request,
                wait_for_response,
            )

            request_id, response_path = submit_inventory_request(arguments.document_title)
            response = wait_for_response(
                request_id, response_path, timeout_seconds=arguments.timeout
            )
            if response.status != "completed":
                raise PackagingError(
                    "Live Revit read-only request failed: " + response.error
                )
            root = default_bridge_root()
            inventory = load_revit_inventory(root / response.output_file, allowed_root=root)
            result = {
                "request_id": response.request_id,
                "status": response.status,
                "document_title": inventory.document_title,
                "counts": inventory.counts,
                "provider_egress_performed": inventory.provider_egress_performed,
                "document_mutation_performed": inventory.document_mutation_performed,
            }
        elif arguments.command == "revit-read-category":
            from integrations.revit_category_query import load_category_query
            from integrations.revit_request_bridge import (
                default_bridge_root,
                submit_category_request,
                wait_for_response,
            )

            request_id, response_path = submit_category_request(
                arguments.document_title,
                arguments.category,
                maximum_elements=arguments.limit,
                hosted_only=arguments.hosted_only,
                ceiling_hosted_only=arguments.ceiling_hosted_only,
                start_after_element_id=arguments.start_after_element_id,
            )
            response = wait_for_response(
                request_id,
                response_path,
                timeout_seconds=arguments.timeout,
                expected_action="category_query",
            )
            if response.status != "completed":
                raise PackagingError("Live Revit category request failed: " + response.error)
            root = default_bridge_root()
            query = load_category_query(root / response.output_file, allowed_root=root)
            result = {
                "request_id": request_id,
                "status": response.status,
                "document_title": query.document_title,
                "category": query.category_key,
                "host_filter": query.host_filter,
                "total_count": query.total_count,
                "returned_count": query.returned_count,
                "truncated": query.truncated,
                "start_after_element_id": query.start_after_element_id,
                "next_start_after_element_id": query.next_start_after_element_id,
                "elements": query.elements,
                "provider_egress_performed": False,
                "document_mutation_performed": False,
            }
        elif arguments.command == "revit-save-model-lines":
            from integrations.revit_model_line_handoff import (
                submit_approved_model_lines_save_as,
                wait_for_handoff,
            )
            from integrations.revit_request_bridge import default_bridge_root

            root = _root()
            source = arguments.source_model
            preview = arguments.preview
            output = arguments.output_model
            if not source.is_absolute():
                source = root / source
            if not preview.is_absolute():
                preview = root / preview
            if not output.is_absolute():
                output = root / output
            request_id, response_path, backup = submit_approved_model_lines_save_as(
                workspace_root=root,
                preview_path=preview,
                source_model=source,
                output_model=output,
                confirmed_preview_sha256=arguments.confirm_preview_sha256,
            )
            report = wait_for_handoff(
                request_id,
                response_path,
                bridge_root=default_bridge_root(),
                expected_output=output,
                timeout_seconds=arguments.timeout,
            )
            result = {"request_id": request_id, "backup": str(backup), **report}
        elif arguments.command == "revit-read-family-types":
            from integrations.revit_family_type_query import (
                load_family_type_query,
                submit_family_type_request,
            )
            from integrations.revit_request_bridge import (
                default_bridge_root,
                wait_for_response,
            )

            request_id, response_path = submit_family_type_request(
                arguments.document_title,
                arguments.category,
                maximum_elements=arguments.limit,
            )
            response = wait_for_response(
                request_id,
                response_path,
                timeout_seconds=arguments.timeout,
                expected_action="family_type_query",
            )
            if response.status != "completed":
                raise PackagingError("Live Revit family-type query failed: " + response.error)
            root = default_bridge_root()
            query = load_family_type_query(root / response.output_file, allowed_root=root)
            result = {
                "request_id": request_id,
                "status": response.status,
                "document_title": query.document_title,
                "category": query.category_key,
                "total_count": query.total_count,
                "returned_count": query.returned_count,
                "truncated": query.truncated,
                "types": query.types,
                "provider_egress_performed": False,
                "document_mutation_performed": False,
            }
        elif arguments.command == "revit-read-host-face":
            from integrations.revit_host_face_query import load_host_face_query
            from integrations.revit_request_bridge import (
                default_bridge_root,
                submit_host_face_request,
                wait_for_response,
            )

            request_id, response_path = submit_host_face_request(
                arguments.document_title,
                arguments.reference_element_id,
                arguments.face_stable_reference,
                maximum_candidates=arguments.limit,
            )
            response = wait_for_response(
                request_id,
                response_path,
                timeout_seconds=arguments.timeout,
                expected_action="host_face_query",
            )
            if response.status != "completed":
                raise PackagingError("Live Revit host-face request failed: " + response.error)
            root = default_bridge_root()
            query = load_host_face_query(root / response.output_file, allowed_root=root)
            result = {
                "request_id": request_id,
                "status": response.status,
                "document_title": query.document_title,
                "reference_element_id": query.reference_element_id,
                "link_instance_id": query.link_instance_id,
                "linked_element_id": query.linked_element_id,
                "linked_document_title": query.linked_document_title,
                "face_stable_reference": query.face_stable_reference,
                "face_area_mm2": query.face_area_mm2,
                "mesh_vertices": query.mesh_vertices,
                "mesh_triangles": query.mesh_triangles,
                "candidates": query.candidates,
                "provider_egress_performed": False,
                "document_mutation_performed": False,
            }
        elif arguments.command == "revit-verify-model-lines":
            from integrations.revit_model_line_handoff import (
                submit_handoff_verification,
                wait_for_handoff_verification,
            )
            from integrations.revit_request_bridge import default_bridge_root

            root = _root()
            handoff = arguments.handoff_report
            if not handoff.is_absolute():
                handoff = default_bridge_root() / handoff
            handoff_id = handoff.stem
            request_id, response_path = submit_handoff_verification(
                workspace_root=root,
                handoff_report=handoff,
            )
            result = wait_for_handoff_verification(
                request_id,
                response_path,
                bridge_root=default_bridge_root(),
                handoff_request_id=handoff_id,
                timeout_seconds=arguments.timeout,
            )
        elif arguments.command == "revit-focus-element":
            from integrations.revit_element_visual_query import load_element_visual_query
            from integrations.revit_request_bridge import (
                default_bridge_root,
                submit_element_visual_request,
                wait_for_response,
            )

            root = default_bridge_root()
            query = None
            response = None
            request_id = ""
            for _ in range(2):
                request_id, response_path = submit_element_visual_request(
                    arguments.document_title, arguments.element_id
                )
                response = wait_for_response(
                    request_id,
                    response_path,
                    timeout_seconds=arguments.timeout,
                    expected_action="element_visual_query",
                )
                if response.status != "completed":
                    raise PackagingError("Live Revit visual request failed: " + response.error)
                query = load_element_visual_query(root / response.output_file, allowed_root=root)
            if query is None or response is None:
                raise PackagingError("Live Revit visual request did not complete its two-stage focus.")
            result = {
                "request_id": request_id,
                "status": response.status,
                "document_title": query.document_title,
                "element_id": query.element_id,
                "family_name": query.family_name,
                "type_name": query.type_name,
                "active_view_id": query.active_view_id,
                "active_view_name": query.active_view_name,
                "active_view_type": query.active_view_type,
                "temporary_hide_isolate_was_active": query.temporary_hide_isolate_was_active,
                "visible_in_view": query.visible_in_view,
                "model_bounding_box_mm": query.model_bounding_box_mm,
                "view_bounding_box_mm": query.view_bounding_box_mm,
                "image_file": query.image_file,
                "facing_orientation": query.facing_orientation,
                "hand_orientation": query.hand_orientation,
                "mirrored": query.mirrored,
                "facing_flipped": query.facing_flipped,
                "hand_flipped": query.hand_flipped,
                "hidden_in_view": query.hidden_in_view,
                "category_hidden_in_view": query.category_hidden_in_view,
                "visible_geometry_objects": query.visible_geometry_objects,
                "location_point_mm": query.location_point_mm,
                "elevation_from_level_mm": query.elevation_from_level_mm,
                "matching_view_filters": query.matching_view_filters,
                "element_projection_line_color": query.element_projection_line_color,
                "provider_egress_performed": False,
                "document_mutation_performed": False,
            }
        elif arguments.command == "revit-read-lighting-circuits":
            from integrations.revit_lighting_circuit_query import (
                load_lighting_circuit_query,
            )
            from integrations.revit_request_bridge import (
                default_bridge_root,
                submit_lighting_circuit_request,
                wait_for_response,
            )

            request_id, response_path = submit_lighting_circuit_request(
                arguments.document_title,
                maximum_elements=arguments.limit,
                element_id=arguments.element_id,
                start_after_element_id=arguments.start_after_element_id,
            )
            response = wait_for_response(
                request_id,
                response_path,
                timeout_seconds=arguments.timeout,
                expected_action="lighting_circuit_query",
            )
            if response.status != "completed":
                raise PackagingError("Live Revit lighting circuit request failed: " + response.error)
            root = default_bridge_root()
            query = load_lighting_circuit_query(root / response.output_file, allowed_root=root)
            result = {
                "request_id": request_id,
                "status": response.status,
                "document_title": query.document_title,
                "requested_element_id": query.requested_element_id,
                "start_after_element_id": query.start_after_element_id,
                "next_start_after_element_id": query.next_start_after_element_id,
                "total_fixture_count": query.total_fixture_count,
                "returned_fixture_count": query.returned_fixture_count,
                "truncated": query.truncated,
                "fixtures": query.fixtures,
                "provider_egress_performed": False,
                "document_mutation_performed": False,
            }
        elif arguments.command == "revit-build-lighting-project-inventory":
            from integrations.revit_lighting_project_inventory import (
                create_lighting_project_inventory,
            )
            from integrations.revit_request_bridge import default_bridge_root

            workspace = _root()
            source = arguments.source_model
            output = arguments.output_inventory
            if not source.is_absolute():
                source = workspace / source
            if not output.is_absolute():
                output = workspace / output
            bridge = default_bridge_root()
            pages = [item if item.is_absolute() else bridge / item for item in arguments.page]
            result = create_lighting_project_inventory(
                workspace_root=workspace,
                bridge_root=bridge,
                source_model=source,
                page_paths=pages,
                output_path=output,
            )
        elif arguments.command == "revit-read-electrical-systems":
            from integrations.revit_electrical_system_query import load_electrical_system_query
            from integrations.revit_request_bridge import (
                default_bridge_root,
                submit_electrical_system_request,
                wait_for_response,
            )

            request_id, response_path = submit_electrical_system_request(
                arguments.document_title,
                maximum_elements=arguments.limit,
                start_after_element_id=arguments.start_after_element_id,
            )
            response = wait_for_response(
                request_id,
                response_path,
                timeout_seconds=arguments.timeout,
                expected_action="electrical_system_query",
            )
            if response.status != "completed":
                raise PackagingError("Live Revit electrical-system request failed: " + response.error)
            root = default_bridge_root()
            query = load_electrical_system_query(root / response.output_file, allowed_root=root)
            result = {
                "request_id": request_id,
                "status": response.status,
                "document_title": query.document_title,
                "start_after_element_id": query.start_after_element_id,
                "next_start_after_element_id": query.next_start_after_element_id,
                "total_system_count": query.total_system_count,
                "returned_system_count": query.returned_system_count,
                "truncated": query.truncated,
                "systems": query.systems,
                "provider_egress_performed": False,
                "document_mutation_performed": False,
            }
        elif arguments.command == "revit-build-mep-project-inventory":
            from integrations.revit_mep_project_inventory import create_mep_project_inventory

            workspace = _root()
            source = arguments.source_model
            output = arguments.output_inventory
            page_root = arguments.page_root
            if not source.is_absolute():
                source = workspace / source
            if not output.is_absolute():
                output = workspace / output
            if not page_root.is_absolute():
                page_root = workspace / page_root
            resolved_page_root = page_root.resolve(strict=True)
            if not resolved_page_root.is_relative_to(workspace.resolve(strict=True)):
                raise PackagingError("Revit MEP page root must stay inside the project workspace.")
            pages = [
                item if item.is_absolute() else resolved_page_root / item
                for item in arguments.page
            ]
            result = create_mep_project_inventory(
                workspace_root=workspace,
                bridge_root=resolved_page_root,
                source_model=source,
                page_paths=pages,
                output_path=output,
            )
        elif arguments.command == "revit-build-electrical-system-inventory":
            from integrations.revit_electrical_system_inventory import (
                create_electrical_system_inventory,
            )

            workspace = _root()
            source = arguments.source_model
            output = arguments.output_inventory
            page_root = arguments.page_root
            if not source.is_absolute():
                source = workspace / source
            if not output.is_absolute():
                output = workspace / output
            if not page_root.is_absolute():
                page_root = workspace / page_root
            resolved_page_root = page_root.resolve(strict=True)
            if not resolved_page_root.is_relative_to(workspace.resolve(strict=True)):
                raise PackagingError("Revit electrical-system page root must stay inside the workspace.")
            pages = [
                item if item.is_absolute() else resolved_page_root / item
                for item in arguments.page
            ]
            result = create_electrical_system_inventory(
                workspace_root=workspace,
                bridge_root=resolved_page_root,
                source_model=source,
                page_paths=pages,
                output_path=output,
            )
        elif arguments.command == "revit-preview-circuit-assignment":
            from integrations.revit_circuit_assignment_preview import (
                create_circuit_assignment_preview,
            )
            from integrations.revit_request_bridge import default_bridge_root

            workspace = _root()
            source = arguments.source_model
            output = arguments.output_preview
            if not source.is_absolute():
                source = workspace / source
            if not output.is_absolute():
                output = workspace / output
            bridge = default_bridge_root()
            fixture_query = arguments.fixture_query
            reference_query = arguments.reference_query
            if not fixture_query.is_absolute():
                fixture_query = bridge / fixture_query
            if not reference_query.is_absolute():
                reference_query = bridge / reference_query
            result = create_circuit_assignment_preview(
                workspace_root=workspace,
                bridge_root=bridge,
                source_model=source,
                fixture_query_path=fixture_query,
                reference_query_path=reference_query,
                target_system_id=arguments.target_system_id,
                output_path=output,
            )
        elif arguments.command == "revit-review-lighting-circuit":
            from integrations.revit_circuit_engineering_review import (
                create_circuit_engineering_review,
            )
            from integrations.revit_request_bridge import default_bridge_root

            root = _root()
            source = arguments.source_model
            preview = arguments.approved_preview
            query = arguments.post_assignment_query
            output = arguments.output_review
            if not source.is_absolute(): source = root / source
            if not preview.is_absolute(): preview = root / preview
            if not query.is_absolute(): query = default_bridge_root() / query
            if not output.is_absolute(): output = root / output
            result = create_circuit_engineering_review(
                workspace_root=root,
                bridge_root=default_bridge_root(),
                source_model=source,
                approved_preview_path=preview,
                post_assignment_query_path=query,
                output_path=output,
            )
        elif arguments.command == "revit-build-circuit-calculation-intake":
            from integrations.revit_circuit_calculation_intake import (
                create_circuit_calculation_intake,
            )

            root = _root()
            review = arguments.engineering_review
            output = arguments.output_intake
            if not review.is_absolute():
                review = root / review
            if not output.is_absolute():
                output = root / output
            result = create_circuit_calculation_intake(
                workspace_root=root,
                engineering_review_path=review,
                output_path=output,
            )
        elif arguments.command == "revit-assign-lighting-circuit":
            from integrations.revit_circuit_assignment_handoff import (
                submit_approved_circuit_assignment_save_as,
                wait_for_circuit_assignment_handoff,
            )
            from integrations.revit_request_bridge import default_bridge_root

            root = _root()
            source, preview, output = arguments.source_model, arguments.preview, arguments.output_model
            if not source.is_absolute(): source = root / source
            if not preview.is_absolute(): preview = root / preview
            if not output.is_absolute(): output = root / output
            request_id, response_path, backup = submit_approved_circuit_assignment_save_as(
                workspace_root=root,
                preview_path=preview,
                source_model=source,
                output_model=output,
                confirmed_preview_sha256=arguments.confirm_preview_sha256,
            )
            report = wait_for_circuit_assignment_handoff(
                request_id,
                response_path,
                bridge_root=default_bridge_root(),
                expected_output=output,
                timeout_seconds=arguments.timeout,
            )
            result = {"request_id": request_id, "backup": str(backup), **report}
        elif arguments.command == "revit-verify-lighting-circuit":
            from integrations.revit_circuit_assignment_handoff import (
                submit_circuit_assignment_verification,
                wait_for_circuit_assignment_verification,
            )
            from integrations.revit_request_bridge import default_bridge_root

            root = _root()
            handoff = arguments.handoff_report
            if not handoff.is_absolute(): handoff = default_bridge_root() / handoff
            handoff_id = handoff.stem
            request_id, response_path = submit_circuit_assignment_verification(
                workspace_root=root, handoff_report=handoff,
            )
            result = wait_for_circuit_assignment_verification(
                request_id,
                response_path,
                bridge_root=default_bridge_root(),
                handoff_request_id=handoff_id,
                timeout_seconds=arguments.timeout,
            )
        elif arguments.command == "revit-place-family":
            from integrations.revit_family_placement_handoff import (
                submit_approved_family_placement_save_as,
                wait_for_family_handoff,
            )
            from integrations.revit_request_bridge import default_bridge_root

            root = _root()
            source, preview, output = arguments.source_model, arguments.preview, arguments.output_model
            if not source.is_absolute(): source = root / source
            if not preview.is_absolute(): preview = root / preview
            if not output.is_absolute(): output = root / output
            request_id, response_path, backup = submit_approved_family_placement_save_as(
                workspace_root=root, preview_path=preview, source_model=source,
                output_model=output, confirmed_preview_sha256=arguments.confirm_preview_sha256,
            )
            report = wait_for_family_handoff(
                request_id, response_path, bridge_root=default_bridge_root(),
                expected_output=output, timeout_seconds=arguments.timeout,
            )
            result = {"request_id": request_id, "backup": str(backup), **report}
        elif arguments.command == "revit-verify-family-placement":
            from integrations.revit_family_placement_handoff import (
                submit_family_handoff_verification,
                wait_for_family_handoff_verification,
            )
            from integrations.revit_request_bridge import default_bridge_root

            root = _root()
            handoff = arguments.handoff_report
            if not handoff.is_absolute(): handoff = default_bridge_root() / handoff
            handoff_id = handoff.stem
            request_id, response_path = submit_family_handoff_verification(
                workspace_root=root, handoff_report=handoff,
            )
            result = wait_for_family_handoff_verification(
                request_id, response_path, bridge_root=default_bridge_root(),
                handoff_request_id=handoff_id, timeout_seconds=arguments.timeout,
            )
        elif arguments.command == "revit-place-hosted-family":
            from integrations.revit_hosted_family_placement_handoff import (
                submit_approved_hosted_family_placement_save_as,
                wait_for_hosted_family_handoff,
            )
            from integrations.revit_request_bridge import default_bridge_root

            root = _root()
            source, preview, output = arguments.source_model, arguments.preview, arguments.output_model
            if not source.is_absolute(): source = root / source
            if not preview.is_absolute(): preview = root / preview
            if not output.is_absolute(): output = root / output
            request_id, response_path, backup = submit_approved_hosted_family_placement_save_as(
                workspace_root=root, preview_path=preview, source_model=source,
                output_model=output, confirmed_preview_sha256=arguments.confirm_preview_sha256,
            )
            report = wait_for_hosted_family_handoff(
                request_id, response_path, bridge_root=default_bridge_root(),
                expected_output=output, timeout_seconds=arguments.timeout,
            )
            result = {"request_id": request_id, "backup": str(backup), **report}
        elif arguments.command == "revit-verify-hosted-family-placement":
            from integrations.revit_hosted_family_placement_handoff import (
                submit_hosted_family_handoff_verification,
                wait_for_hosted_family_handoff_verification,
            )
            from integrations.revit_request_bridge import default_bridge_root

            root = _root()
            handoff = arguments.handoff_report
            if not handoff.is_absolute(): handoff = default_bridge_root() / handoff
            handoff_id = handoff.stem
            request_id, response_path = submit_hosted_family_handoff_verification(
                workspace_root=root, handoff_report=handoff,
            )
            result = wait_for_hosted_family_handoff_verification(
                request_id, response_path, bridge_root=default_bridge_root(),
                handoff_request_id=handoff_id, timeout_seconds=arguments.timeout,
            )
        elif arguments.command == "autocad-benchmark":
            from core.autocad_performance import (
                run_autocad_core_benchmark,
                write_autocad_core_benchmark,
            )

            if arguments.output is None:
                result = run_autocad_core_benchmark(runs=arguments.runs)
            else:
                workspace = (_root() / "workspace").resolve(strict=False)
                workspace.mkdir(parents=True, exist_ok=True)
                output = arguments.output
                if not output.is_absolute():
                    output = workspace / output
                output = output.resolve(strict=False)
                if not output.is_relative_to(workspace):
                    raise PackagingError("Performance report must stay inside workspace.")
                result = write_autocad_core_benchmark(output, runs=arguments.runs)
        elif arguments.command == "run":
            from main import main as run_main

            return run_main()
        elif arguments.command == "api":
            from api.server import main as api_main

            return api_main(["--check"] if arguments.check else [])
        elif arguments.command == "operator":
            from agent.operator_server import main as operator_main

            operator_args: list[str] = []
            if arguments.check:
                operator_args.append("--check")
            if arguments.allow_project_root:
                operator_args.append("--allow-project-root")
            return operator_main(operator_args, project_root=_root())
        elif arguments.command == "package":
            output = arguments.output or _default_archive("dist", "ai-engineer")
            result = PackageBuilder(_root()).build(output).to_dict()
        elif arguments.command == "snapshot":
            config = AppConfig.load(_root())
            output = arguments.output or _default_archive(
                "backup/snapshots", "ai-engineer-recovery"
            )
            result = RecoveryManager(config).create_snapshot(output).to_dict()
        elif arguments.command == "restore":
            config = AppConfig.load(_root())
            result = RecoveryManager(config).restore_snapshot(
                arguments.archive, confirmed=arguments.confirm
            ).to_dict()
        elif arguments.command == "migrate":
            config = AppConfig.load(_root())
            result = MigrationManager(config).migrate(
                confirmed=arguments.confirm
            ).to_dict()
        elif arguments.command == "install":
            result = PackageInstaller().install(
                arguments.archive,
                arguments.destination,
                confirmed=arguments.confirm,
            ).to_dict()
        else:
            raise PackagingError("Launcher command is not supported.")
    except (AIEngineerError, OSError) as exc:
        print(f"AI-Engineer launcher failed: {exc}", file=sys.stderr)
        return 1
    # ASCII JSON remains machine-readable when a Windows console uses a legacy code page.
    print(json.dumps(result, ensure_ascii=True, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
