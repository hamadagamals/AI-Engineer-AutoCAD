"""Compose one validated AutoCAD delta into an immutable v2 inventory artifact."""

from __future__ import annotations

import argparse
import json
from dataclasses import dataclass
from pathlib import Path

from agent.exceptions import AutoCADContextError
from integrations.autocad_drawing_delta import load_drawing_delta
from integrations.autocad_drawing_inventory import (
    SelectiveDrawingInventory,
    compose_drawing_inventory_delta,
    load_drawing_inventory,
    write_drawing_inventory,
)


@dataclass(frozen=True, slots=True)
class ComposedInventoryArtifact:
    """One independently reloadable selective inventory and its local path."""

    inventory: SelectiveDrawingInventory
    path: Path


def compose_inventory_delta_artifact(
    workspace_root: Path,
    *,
    baseline_inventory_path: Path,
    delta_id: str,
    delta_sha256: str,
    symbol_catalog_path: Path,
) -> ComposedInventoryArtifact:
    """Load exact evidence, compose it, persist atomically, and reload it."""
    root = workspace_root.resolve(strict=True)
    baseline_path = baseline_inventory_path.resolve(strict=True)
    if (
        baseline_inventory_path.is_symlink()
        or not baseline_path.is_file()
        or not baseline_path.is_relative_to(root)
    ):
        raise AutoCADContextError(
            "Baseline drawing inventory must be a contained regular workspace file."
        )
    catalog_path = symbol_catalog_path.resolve(strict=True)
    if symbol_catalog_path.is_symlink() or not catalog_path.is_file():
        raise AutoCADContextError("Symbol catalog must be a regular non-link file.")
    baseline = load_drawing_inventory(baseline_path)
    delta = load_drawing_delta(
        root,
        delta_id=delta_id,
        evidence_sha256=delta_sha256,
        expected_document_id=baseline.document_id,
        expected_baseline_index_id=baseline.index_id,
        expected_baseline_index_sha256=baseline.index_sha256,
    )
    inventory = compose_drawing_inventory_delta(
        baseline, delta, symbol_catalog_path=catalog_path
    )
    path = write_drawing_inventory(root, inventory)
    reloaded = load_drawing_inventory(
        path,
        expected_inventory_sha256=inventory.inventory_sha256,
        expected_document_id=inventory.document_id,
        expected_source_drawing_sha256=inventory.source_drawing_sha256,
    )
    if not isinstance(reloaded, SelectiveDrawingInventory) or reloaded != inventory:
        raise AutoCADContextError(
            "Selective drawing inventory failed independent reload verification."
        )
    return ComposedInventoryArtifact(inventory, path)


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Compose one exact AutoCAD delta into a v2 drawing inventory."
    )
    parser.add_argument("workspace_root", type=Path)
    parser.add_argument("baseline_inventory", type=Path)
    parser.add_argument("delta_id")
    parser.add_argument("delta_sha256")
    parser.add_argument("symbol_catalog", type=Path)
    arguments = parser.parse_args()
    result = compose_inventory_delta_artifact(
        arguments.workspace_root,
        baseline_inventory_path=arguments.baseline_inventory,
        delta_id=arguments.delta_id,
        delta_sha256=arguments.delta_sha256,
        symbol_catalog_path=arguments.symbol_catalog,
    )
    print(json.dumps({
        "inventory_id": result.inventory.inventory_id,
        "inventory_sha256": result.inventory.inventory_sha256,
        "inventory_version": result.inventory.inventory_version,
        "entity_count": result.inventory.entity_count,
        "baseline_inventory_sha256": result.inventory.baseline_inventory_sha256,
        "delta_id": result.inventory.delta_id,
        "delta_sha256": result.inventory.delta_sha256,
        "semantic_review_required": result.inventory.semantic_review_required,
        "path": str(result.path),
        "drawing_mutation_performed": False,
    }, ensure_ascii=False, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
