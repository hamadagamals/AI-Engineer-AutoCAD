"""No-mutation orchestration for automatic whole-drawing inventory refresh."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import tempfile
from dataclasses import asdict, dataclass
from pathlib import Path

from agent.exceptions import AutoCADContextError
from integrations.autocad_drawing_index import load_drawing_index
from integrations.autocad_drawing_inventory import (
    DrawingInventory,
    DrawingInventoryDiff,
    build_drawing_inventory,
    compare_drawing_inventories,
    load_drawing_inventory,
    write_drawing_inventory,
)
from integrations.autocad_operator_channel import (
    ActiveAutoCADDocument,
    discover_active_documents,
    enqueue_operator_command,
    wait_for_operator_result,
)
from integrations.autocad_project_conventions import load_project_convention_profile
from integrations.autocad_project_topology import load_reviewed_route_membership


REFRESH_VERSION = "autocad.inventory_refresh.v1"
_MAX_REPORT_BYTES = 1024 * 1024


@dataclass(frozen=True, slots=True)
class InventoryRefreshResult:
    """Verified artifacts produced by one exact automatic refresh."""

    report_path: Path
    inventory_path: Path
    inventory: DrawingInventory
    difference: DrawingInventoryDiff | None
    operator_result_path: Path
    operator_result_sha256: str
    reviewed_semantics_applied: bool
    semantic_review_required: bool


def refresh_active_drawing_inventory(
    workspace_root: Path,
    *,
    symbol_catalog_path: Path,
    convention_profile_path: Path | None = None,
    route_membership_path: Path | None = None,
    baseline_inventory_path: Path | None = None,
    drawing_name: str | None = None,
    timeout_seconds: float = 120.0,
) -> InventoryRefreshResult:
    """Capture, validate, classify, compare, and persist without GUI selection."""
    root = workspace_root.resolve(strict=True)
    active = _select_active_document(
        discover_active_documents(root), drawing_name=drawing_name
    )
    queued = enqueue_operator_command(
        root,
        document_id=active.document_id,
        source_drawing_sha256=active.source_drawing_sha256,
        requested_action="capture_drawing_index",
    )
    operator_result, result_path, result_sha256 = wait_for_operator_result(
        root, queued=queued, timeout_seconds=timeout_seconds
    )
    if (
        operator_result.status != "completed"
        or operator_result.context_id is None
        or operator_result.context_sha256 is None
    ):
        raise AutoCADContextError(
            "Automatic drawing-index capture did not complete successfully."
        )
    index = load_drawing_index(
        root,
        index_id=operator_result.context_id,
        evidence_sha256=operator_result.context_sha256,
        expected_document_id=active.document_id,
        expected_source_drawing_sha256=active.source_drawing_sha256,
    )
    if (convention_profile_path is None) != (route_membership_path is None):
        raise AutoCADContextError(
            "Convention profile and route membership must be supplied together."
        )
    profile = None
    membership = None
    semantic_review_required = True
    if convention_profile_path is not None and route_membership_path is not None:
        candidate_profile = load_project_convention_profile(convention_profile_path)
        candidate_membership = load_reviewed_route_membership(route_membership_path)
        if (
            candidate_profile.document_id == active.document_id
            and candidate_profile.source_drawing_sha256
            == active.source_drawing_sha256
            and candidate_membership.document_id == active.document_id
            and candidate_membership.source_drawing_sha256
            == active.source_drawing_sha256
        ):
            profile = candidate_profile
            membership = candidate_membership
            semantic_review_required = False
    inventory = build_drawing_inventory(
        index,
        symbol_catalog_path=symbol_catalog_path,
        reviewed_conventions=profile,
        reviewed_route_membership=membership,
    )
    inventory_path = write_drawing_inventory(root, inventory)
    difference = None
    if baseline_inventory_path is not None:
        baseline = load_drawing_inventory(
            baseline_inventory_path,
            expected_document_id=active.document_id,
        )
        difference = compare_drawing_inventories(baseline, inventory)
    report_path = _write_refresh_report(
        root,
        active=active,
        inventory=inventory,
        inventory_path=inventory_path,
        difference=difference,
        operator_result_path=result_path,
        operator_result_sha256=result_sha256,
        reviewed_semantics_applied=profile is not None,
        semantic_review_required=semantic_review_required,
    )
    return InventoryRefreshResult(
        report_path,
        inventory_path,
        inventory,
        difference,
        result_path,
        result_sha256,
        profile is not None,
        semantic_review_required,
    )


def _select_active_document(
    documents: tuple[ActiveAutoCADDocument, ...], *, drawing_name: str | None
) -> ActiveAutoCADDocument:
    matches = documents
    if drawing_name is not None:
        expected = Path(drawing_name).name.casefold()
        matches = tuple(
            item for item in documents if item.drawing_name.casefold() == expected
        )
    if len(matches) != 1:
        detail = "no matching" if not matches else "multiple matching"
        raise AutoCADContextError(
            f"Automatic inventory refresh found {detail} active AutoCAD documents."
        )
    return matches[0]


def _write_refresh_report(
    workspace_root: Path,
    *,
    active: ActiveAutoCADDocument,
    inventory: DrawingInventory,
    inventory_path: Path,
    difference: DrawingInventoryDiff | None,
    operator_result_path: Path,
    operator_result_sha256: str,
    reviewed_semantics_applied: bool,
    semantic_review_required: bool,
) -> Path:
    report_id = hashlib.sha256(
        (
            inventory.inventory_sha256
            + ":"
            + (difference.diff_sha256 if difference is not None else "baseline")
            + ":"
            + operator_result_sha256
        ).encode("ascii")
    ).hexdigest()[:32]
    values: dict[str, object] = {
        "schema_version": 1,
        "refresh_version": REFRESH_VERSION,
        "report_id": report_id,
        "document_id": active.document_id,
        "drawing_name": active.drawing_name,
        "source_drawing_sha256": active.source_drawing_sha256,
        "operator_result_sha256": operator_result_sha256,
        "operator_result_path": _relative(operator_result_path, workspace_root),
        "inventory_id": inventory.inventory_id,
        "inventory_sha256": inventory.inventory_sha256,
        "inventory_path": _relative(inventory_path, workspace_root),
        "diff_sha256": difference.diff_sha256 if difference is not None else None,
        "added_count": len(difference.added_object_ids) if difference else None,
        "removed_count": len(difference.removed_object_ids) if difference else None,
        "changed_count": len(difference.changed_object_ids) if difference else None,
        "reclassified_count": (
            len(difference.reclassified_object_ids) if difference else None
        ),
        "unchanged_count": difference.unchanged_count if difference else None,
        "reviewed_semantics_applied": reviewed_semantics_applied,
        "semantic_review_required": semantic_review_required,
        "provider_egress_performed": False,
        "drawing_mutation_performed": False,
    }
    values["report_sha256"] = hashlib.sha256(_canonical(values)).hexdigest()
    content = _canonical(values)
    if len(content) > _MAX_REPORT_BYTES:
        raise AutoCADContextError("Inventory refresh report exceeds 1 MiB.")
    directory = (
        workspace_root / ".ai-engineer" / "autocad" / "inventory-refresh"
    )
    directory.mkdir(parents=True, exist_ok=True)
    target = directory / f"{report_id}.json"
    if target.exists():
        if target.is_symlink() or not target.is_file() or target.read_bytes() != content:
            raise AutoCADContextError("Existing inventory refresh report differs.")
        return target
    temporary_name: str | None = None
    try:
        with tempfile.NamedTemporaryFile(dir=directory, delete=False) as temporary:
            temporary_name = temporary.name
            temporary.write(content)
            temporary.flush()
            os.fsync(temporary.fileno())
        os.replace(temporary_name, target)
    finally:
        if temporary_name is not None:
            Path(temporary_name).unlink(missing_ok=True)
    return target


def _relative(path: Path, root: Path) -> str:
    resolved = path.resolve(strict=True)
    try:
        return resolved.relative_to(root.resolve(strict=True)).as_posix()
    except ValueError as exc:
        raise AutoCADContextError("Refresh artifact is outside the workspace.") from exc


def _canonical(value: object) -> bytes:
    return json.dumps(
        value,
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
        default=asdict,
    ).encode("utf-8")


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Automatically refresh one active AutoCAD drawing inventory."
    )
    parser.add_argument("workspace_root", type=Path)
    parser.add_argument("symbol_catalog", type=Path)
    parser.add_argument("--convention-profile", type=Path)
    parser.add_argument("--route-membership", type=Path)
    parser.add_argument("--baseline-inventory", type=Path)
    parser.add_argument("--drawing-name")
    parser.add_argument("--timeout-seconds", type=float, default=120.0)
    arguments = parser.parse_args()
    result = refresh_active_drawing_inventory(
        arguments.workspace_root,
        symbol_catalog_path=arguments.symbol_catalog,
        convention_profile_path=arguments.convention_profile,
        route_membership_path=arguments.route_membership,
        baseline_inventory_path=arguments.baseline_inventory,
        drawing_name=arguments.drawing_name,
        timeout_seconds=arguments.timeout_seconds,
    )
    print(result.report_path)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
