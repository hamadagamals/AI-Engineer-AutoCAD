"""Drawing-wide structural inventory and deterministic incremental comparison."""

from __future__ import annotations

import hashlib
import json
import os
import re
import tempfile
from collections import Counter
from dataclasses import asdict, dataclass
from pathlib import Path

from agent.exceptions import AutoCADContextError
from integrations.autocad_context import ContextLimits, parse_autocad_context
from integrations.autocad_drawing_delta import DrawingDeltaLoadResult
from integrations.autocad_drawing_index import DrawingIndexLoadResult
from integrations.autocad_project_conventions import ProjectConventionProfile
from integrations.autocad_project_topology import ReviewedRouteMembership
from integrations.electrical_symbols import ElectricalSymbolCatalog


INVENTORY_VERSION = "autocad.drawing_inventory.v1"
SELECTIVE_INVENTORY_VERSION = "autocad.drawing_inventory.v2"
DIFF_VERSION = "autocad.drawing_inventory_diff.v1"
_ID = re.compile(r"[0-9a-f]{32}")
_GUID = re.compile(
    r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}"
)
_SHA = re.compile(r"[0-9a-f]{64}")
_HANDLE = re.compile(r"[0-9A-F]{1,32}")
_MAX_ENTITIES = 200_000
_MAX_INVENTORY_BYTES = 64 * 1024 * 1024


@dataclass(frozen=True, slots=True)
class DrawingInventoryEntity:
    """One stable AutoCAD handle with raw and reviewed semantic fingerprints."""

    object_id: str
    context_id: str
    context_sha256: str
    entity_sha256: str
    entity_type: str
    original_type: str
    layer: str
    layout: str
    block_name: str | None
    semantic_kind: str
    semantic_id: str | None

    def __post_init__(self) -> None:
        if _HANDLE.fullmatch(self.object_id) is None:
            raise AutoCADContextError("Drawing inventory object handle is invalid.")
        if _ID.fullmatch(self.context_id) is None:
            raise AutoCADContextError("Drawing inventory context ID is invalid.")
        for value in (self.context_sha256, self.entity_sha256):
            if _SHA.fullmatch(value) is None:
                raise AutoCADContextError("Drawing inventory entity SHA-256 is invalid.")
        if self.semantic_kind not in {
            "unclassified",
            "catalog_device",
            "reviewed_device",
            "catalog_route_candidate",
            "reviewed_route_candidate",
            "reviewed_route",
            "reviewed_annotation",
        }:
            raise AutoCADContextError("Drawing inventory semantic kind is invalid.")
        if (self.semantic_kind == "unclassified") != (self.semantic_id is None):
            raise AutoCADContextError("Drawing inventory semantic identity is inconsistent.")


@dataclass(frozen=True, slots=True)
class DrawingInventory:
    """One immutable drawing-wide snapshot derived from a complete index."""

    schema_version: int
    inventory_version: str
    inventory_id: str
    index_id: str
    index_sha256: str
    document_id: str
    source_drawing_sha256: str
    catalog_sha256: str
    convention_profile_sha256: str | None
    route_membership_sha256: str | None
    entity_count: int
    supported_observation_count: int
    unsupported_observation_count: int
    raw_block_count: int
    raw_route_primitive_count: int
    classified_device_count: int
    classified_route_count: int
    route_candidate_count: int
    reviewed_annotation_count: int
    unclassified_count: int
    layout_counts: tuple[tuple[str, int], ...]
    layer_counts: tuple[tuple[str, int], ...]
    entity_type_counts: tuple[tuple[str, int], ...]
    semantic_counts: tuple[tuple[str, int], ...]
    entities: tuple[DrawingInventoryEntity, ...]
    read_only: bool
    provider_egress_performed: bool
    drawing_mutation_performed: bool
    inventory_sha256: str

    def __post_init__(self) -> None:
        _validate_inventory(self, 1, INVENTORY_VERSION)

    def to_dict(self) -> dict[str, object]:
        return asdict(self)


@dataclass(frozen=True, slots=True)
class SelectiveDrawingInventory(DrawingInventory):
    """Immutable v2 snapshot produced by one exact selective delta."""

    baseline_inventory_sha256: str
    delta_id: str
    delta_sha256: str
    semantic_review_required: bool

    def __post_init__(self) -> None:
        _validate_inventory(self, 2, SELECTIVE_INVENTORY_VERSION)
        if (
            _SHA.fullmatch(self.baseline_inventory_sha256) is None
            or _ID.fullmatch(self.delta_id) is None
            or _SHA.fullmatch(self.delta_sha256) is None
            or self.semantic_review_required is not True
            or self.convention_profile_sha256 is not None
            or self.route_membership_sha256 is not None
        ):
            raise AutoCADContextError("Selective inventory lineage or review state is invalid.")


def _validate_inventory(
    inventory: DrawingInventory, expected_schema: int, expected_version: str
) -> None:
    """Validate fields shared by complete-index and selective inventories."""
    if (
        inventory.schema_version != expected_schema
        or inventory.inventory_version != expected_version
    ):
        raise AutoCADContextError("Drawing inventory version is unsupported.")
    if (
        _ID.fullmatch(inventory.inventory_id) is None
        or _ID.fullmatch(inventory.index_id) is None
    ):
        raise AutoCADContextError("Drawing inventory identity is invalid.")
    if _GUID.fullmatch(inventory.document_id) is None:
        raise AutoCADContextError("Drawing inventory document ID is invalid.")
    for value in (
        inventory.index_sha256,
        inventory.source_drawing_sha256,
        inventory.catalog_sha256,
        inventory.inventory_sha256,
    ):
        if _SHA.fullmatch(value) is None:
            raise AutoCADContextError("Drawing inventory SHA-256 is invalid.")
    if (
        inventory.convention_profile_sha256 is not None
        and _SHA.fullmatch(inventory.convention_profile_sha256) is None
    ):
        raise AutoCADContextError("Drawing inventory convention SHA-256 is invalid.")
    if (
        inventory.route_membership_sha256 is not None
        and _SHA.fullmatch(inventory.route_membership_sha256) is None
    ):
        raise AutoCADContextError("Drawing inventory route-membership SHA-256 is invalid.")
    if (
        not 0 <= inventory.entity_count <= _MAX_ENTITIES
        or len(inventory.entities) != inventory.entity_count
    ):
        raise AutoCADContextError("Drawing inventory entity count is inconsistent.")
    handles = [item.object_id for item in inventory.entities]
    if len(handles) != len(set(handles)):
        raise AutoCADContextError("Drawing inventory handles must be unique.")
    if (
        inventory.supported_observation_count + inventory.unsupported_observation_count
        != inventory.entity_count
        or inventory.classified_device_count + inventory.classified_route_count
        + inventory.route_candidate_count + inventory.reviewed_annotation_count
        + inventory.unclassified_count != inventory.entity_count
    ):
        raise AutoCADContextError("Drawing inventory summary counts are inconsistent.")
    if (
        inventory.read_only is not True
        or inventory.provider_egress_performed is not False
        or inventory.drawing_mutation_performed is not False
    ):
        raise AutoCADContextError("Drawing inventory safety flags are invalid.")
    values = asdict(inventory)
    digest = values.pop("inventory_sha256")
    if digest != hashlib.sha256(_canonical(values)).hexdigest():
        raise AutoCADContextError("Drawing inventory SHA-256 does not match content.")


@dataclass(frozen=True, slots=True)
class DrawingInventoryDiff:
    """Exact handle-level delta between two complete inventories."""

    schema_version: int
    diff_version: str
    diff_id: str
    document_id: str
    before_inventory_sha256: str
    after_inventory_sha256: str
    before_source_drawing_sha256: str
    after_source_drawing_sha256: str
    added_object_ids: tuple[str, ...]
    removed_object_ids: tuple[str, ...]
    changed_object_ids: tuple[str, ...]
    reclassified_object_ids: tuple[str, ...]
    unchanged_count: int
    drawing_mutation_performed: bool
    diff_sha256: str

    def __post_init__(self) -> None:
        if self.schema_version != 1 or self.diff_version != DIFF_VERSION:
            raise AutoCADContextError("Drawing inventory diff version is unsupported.")
        if _ID.fullmatch(self.diff_id) is None or _GUID.fullmatch(self.document_id) is None:
            raise AutoCADContextError("Drawing inventory diff identity is invalid.")
        for value in (
            self.before_inventory_sha256,
            self.after_inventory_sha256,
            self.before_source_drawing_sha256,
            self.after_source_drawing_sha256,
            self.diff_sha256,
        ):
            if _SHA.fullmatch(value) is None:
                raise AutoCADContextError("Drawing inventory diff SHA-256 is invalid.")
        groups = (
            self.added_object_ids,
            self.removed_object_ids,
            self.changed_object_ids,
            self.reclassified_object_ids,
        )
        if any(not isinstance(group, tuple) for group in groups):
            raise AutoCADContextError("Drawing inventory diff handle groups are invalid.")
        flattened = [value for group in groups for value in group]
        if any(_HANDLE.fullmatch(value) is None for value in flattened):
            raise AutoCADContextError("Drawing inventory diff contains an invalid handle.")
        if len(flattened) != len(set(flattened)) or not 0 <= self.unchanged_count <= _MAX_ENTITIES:
            raise AutoCADContextError("Drawing inventory diff partitions overlap or exceed bounds.")
        if self.drawing_mutation_performed is not False:
            raise AutoCADContextError("Drawing inventory diff claims drawing mutation.")
        values = asdict(self)
        digest = values.pop("diff_sha256")
        if digest != hashlib.sha256(_canonical(values)).hexdigest():
            raise AutoCADContextError("Drawing inventory diff SHA-256 does not match content.")

    def to_dict(self) -> dict[str, object]:
        return asdict(self)


def build_drawing_inventory(
    index: DrawingIndexLoadResult,
    *,
    symbol_catalog_path: Path,
    reviewed_conventions: ProjectConventionProfile | None = None,
    reviewed_route_membership: ReviewedRouteMembership | None = None,
    context_limits: ContextLimits | None = None,
) -> DrawingInventory:
    """Build a stable local inventory without inferring unsupported semantics."""
    manifest = index.manifest
    if index.unique_entity_count != manifest.entity_count:
        raise AutoCADContextError("Drawing index was not fully validated before inventory.")
    if reviewed_conventions is not None and (
        reviewed_conventions.document_id != manifest.document_id
        or reviewed_conventions.source_drawing_sha256
        != manifest.source_drawing_sha256
    ):
        raise AutoCADContextError(
            "Reviewed conventions do not match the indexed document and source SHA-256."
        )
    if reviewed_route_membership is not None:
        if reviewed_conventions is None:
            raise AutoCADContextError(
                "Reviewed route membership requires its reviewed convention profile."
            )
        if (
            reviewed_route_membership.document_id != manifest.document_id
            or reviewed_route_membership.source_drawing_sha256
            != manifest.source_drawing_sha256
            or reviewed_route_membership.convention_profile_sha256
            != reviewed_conventions.profile_sha256
        ):
            raise AutoCADContextError(
                "Reviewed route membership does not match the indexed drawing evidence."
            )
    catalog_path = symbol_catalog_path.resolve(strict=True)
    catalog_bytes = catalog_path.read_bytes()
    catalog = ElectricalSymbolCatalog.load(catalog_path)
    catalog_sha256 = hashlib.sha256(catalog_bytes).hexdigest()
    catalog_blocks = {
        item.block_name.casefold(): item.symbol_id for item in catalog.symbols
    }
    convention_blocks = (
        {
            (item.layer_name.casefold(), item.block_name.casefold()): item.symbol_id
            for item in reviewed_conventions.block_mappings
        }
        if reviewed_conventions is not None
        else {}
    )
    convention_routes = (
        {item.layer_name.casefold(): item.route_kind for item in reviewed_conventions.route_mappings}
        if reviewed_conventions is not None
        else {}
    )
    reviewed_route_ids = (
        set(reviewed_route_membership.route_object_ids)
        if reviewed_route_membership is not None
        else set()
    )
    reviewed_annotation_ids = (
        set(reviewed_route_membership.annotation_route_object_ids)
        if reviewed_route_membership is not None
        else set()
    )
    chunks = {item.context_id: item for item in manifest.chunks}
    records: list[DrawingInventoryEntity] = []
    limits = context_limits or ContextLimits()
    for path in index.context_paths:
        context_id = path.stem
        chunk = chunks.get(context_id)
        if chunk is None:
            raise AutoCADContextError("Inventory context is absent from its index manifest.")
        raw = path.read_bytes()
        if hashlib.sha256(raw).hexdigest() != chunk.context_sha256:
            raise AutoCADContextError("Inventory context changed after index validation.")
        try:
            context = parse_autocad_context(
                json.loads(raw.decode("utf-8")), expected_limits=limits
            )
        except (UnicodeError, json.JSONDecodeError) as exc:
            raise AutoCADContextError("Inventory context is not UTF-8 JSON.") from exc
        for entity in context.entities:
            semantic_kind, semantic_id = _semantic_identity(
                object_id=entity.object_id,
                entity_type=entity.entity_type,
                layer=entity.layer,
                block_name=entity.block_name,
                catalog_blocks=catalog_blocks,
                convention_blocks=convention_blocks,
                convention_routes=convention_routes,
                reviewed_route_ids=reviewed_route_ids,
                reviewed_annotation_ids=reviewed_annotation_ids,
                route_membership_id=(
                    reviewed_route_membership.membership_id
                    if reviewed_route_membership is not None
                    else None
                ),
            )
            entity_sha256 = hashlib.sha256(
                _canonical(entity.to_dict())
            ).hexdigest()
            records.append(
                DrawingInventoryEntity(
                    object_id=entity.object_id,
                    context_id=context.context_id,
                    context_sha256=chunk.context_sha256,
                    entity_sha256=entity_sha256,
                    entity_type=entity.entity_type,
                    original_type=entity.original_type,
                    layer=entity.layer,
                    layout=entity.layout,
                    block_name=entity.block_name,
                    semantic_kind=semantic_kind,
                    semantic_id=semantic_id,
                )
            )
    records.sort(key=lambda item: (len(item.object_id), item.object_id))
    handles = [item.object_id for item in records]
    if len(records) != manifest.entity_count or len(handles) != len(set(handles)):
        raise AutoCADContextError("Drawing inventory entity coverage is inconsistent.")
    records_by_handle = {item.object_id: item for item in records}
    reviewed_ids = reviewed_route_ids | reviewed_annotation_ids
    if not reviewed_ids.issubset(records_by_handle):
        raise AutoCADContextError(
            "Reviewed route membership contains handles absent from the complete index."
        )
    if any(
        records_by_handle[handle].entity_type not in {"LINE", "LWPOLYLINE"}
        for handle in reviewed_ids
    ):
        raise AutoCADContextError(
            "Reviewed route membership contains a non-route primitive."
        )
    type_counts = Counter(item.entity_type for item in records)
    semantic_counts = Counter(item.semantic_kind for item in records)
    values: dict[str, object] = {
        "schema_version": 1,
        "inventory_version": INVENTORY_VERSION,
        "inventory_id": hashlib.sha256(
            _canonical(
                {
                    "index_sha256": index.manifest_sha256,
                    "catalog_sha256": catalog_sha256,
                    "convention_profile_sha256": (
                        reviewed_conventions.profile_sha256
                        if reviewed_conventions is not None
                        else None
                    ),
                    "route_membership_sha256": (
                        reviewed_route_membership.membership_sha256
                        if reviewed_route_membership is not None
                        else None
                    ),
                }
            )
        ).hexdigest()[:32],
        "index_id": manifest.index_id,
        "index_sha256": index.manifest_sha256,
        "document_id": manifest.document_id,
        "source_drawing_sha256": manifest.source_drawing_sha256,
        "catalog_sha256": catalog_sha256,
        "convention_profile_sha256": (
            reviewed_conventions.profile_sha256
            if reviewed_conventions is not None
            else None
        ),
        "route_membership_sha256": (
            reviewed_route_membership.membership_sha256
            if reviewed_route_membership is not None
            else None
        ),
        "entity_count": len(records),
        "supported_observation_count": len(records) - type_counts["UNSUPPORTED"],
        "unsupported_observation_count": type_counts["UNSUPPORTED"],
        "raw_block_count": type_counts["INSERT"],
        "raw_route_primitive_count": type_counts["LINE"] + type_counts["LWPOLYLINE"],
        "classified_device_count": (
            semantic_counts["catalog_device"] + semantic_counts["reviewed_device"]
        ),
        "classified_route_count": semantic_counts["reviewed_route"],
        "route_candidate_count": (
            semantic_counts["catalog_route_candidate"]
            + semantic_counts["reviewed_route_candidate"]
        ),
        "reviewed_annotation_count": semantic_counts["reviewed_annotation"],
        "unclassified_count": semantic_counts["unclassified"],
        "layout_counts": _counts(item.layout for item in records),
        "layer_counts": _counts(item.layer for item in records),
        "entity_type_counts": _counts(item.entity_type for item in records),
        "semantic_counts": _counts(item.semantic_kind for item in records),
        "entities": tuple(records),
        "read_only": True,
        "provider_egress_performed": False,
        "drawing_mutation_performed": False,
    }
    digest = hashlib.sha256(_canonical(values)).hexdigest()
    return DrawingInventory(**values, inventory_sha256=digest)


def compose_drawing_inventory_delta(
    baseline: DrawingInventory,
    delta: DrawingDeltaLoadResult,
    *,
    symbol_catalog_path: Path,
    context_limits: ContextLimits | None = None,
) -> SelectiveDrawingInventory:
    """Apply one validated in-session delta without retaining stale reviewed meaning."""
    manifest = delta.manifest
    candidate_ids = (
        set(manifest.added_object_ids)
        | set(manifest.modified_object_ids)
        | set(manifest.removed_object_ids)
    )
    live_ids = set(manifest.added_object_ids) | set(manifest.modified_object_ids)
    if (
        delta.read_only is not True
        or delta.provider_egress_performed is not False
        or delta.drawing_mutation_performed is not False
        or manifest.candidate_count != len(candidate_ids)
        or set(delta.captured_object_ids) != live_ids
        or len(delta.context_paths) != len(manifest.chunks)
    ):
        raise AutoCADContextError(
            "Selective inventory requires a complete validated delta result."
        )
    if (
        baseline.schema_version != 1
        or baseline.inventory_version != INVENTORY_VERSION
        or baseline.document_id != manifest.document_id
        or baseline.source_drawing_sha256
        != manifest.baseline_source_drawing_sha256
        or baseline.index_id != manifest.baseline_index_id
        or baseline.index_sha256 != manifest.baseline_index_sha256
        or baseline.entity_count != manifest.baseline_entity_count
    ):
        raise AutoCADContextError(
            "Selective inventory delta does not match its complete baseline inventory."
        )
    catalog_path = symbol_catalog_path.resolve(strict=True)
    catalog_bytes = catalog_path.read_bytes()
    catalog_sha256 = hashlib.sha256(catalog_bytes).hexdigest()
    if baseline.catalog_sha256 != catalog_sha256:
        raise AutoCADContextError(
            "Selective inventory requires the exact baseline symbol catalog."
        )
    catalog = ElectricalSymbolCatalog.load(catalog_path)
    catalog_blocks = {
        item.block_name.casefold(): item.symbol_id for item in catalog.symbols
    }
    baseline_by_handle = {item.object_id: item for item in baseline.entities}
    added = set(manifest.added_object_ids)
    modified = set(manifest.modified_object_ids)
    removed = set(manifest.removed_object_ids)
    if (
        not modified.issubset(baseline_by_handle)
        or not removed.issubset(baseline_by_handle)
        or bool(added & set(baseline_by_handle))
    ):
        raise AutoCADContextError(
            "Selective inventory candidate handles conflict with the baseline."
        )
    records: dict[str, DrawingInventoryEntity] = {}
    for object_id, item in baseline_by_handle.items():
        if object_id in modified or object_id in removed:
            continue
        semantic_kind, semantic_id = _catalog_semantic_identity(
            item.entity_type, item.layer, item.block_name, catalog_blocks
        )
        records[object_id] = DrawingInventoryEntity(
            object_id=item.object_id,
            context_id=item.context_id,
            context_sha256=item.context_sha256,
            entity_sha256=item.entity_sha256,
            entity_type=item.entity_type,
            original_type=item.original_type,
            layer=item.layer,
            layout=item.layout,
            block_name=item.block_name,
            semantic_kind=semantic_kind,
            semantic_id=semantic_id,
        )
    chunks = {item.context_id: item for item in manifest.chunks}
    observed: set[str] = set()
    limits = context_limits or ContextLimits()
    for path in delta.context_paths:
        if path.is_symlink() or not path.resolve(strict=True).is_file():
            raise AutoCADContextError(
                "Selective inventory context must be a regular non-link file."
            )
        context_id = path.stem
        chunk = chunks.get(context_id)
        if chunk is None:
            raise AutoCADContextError(
                "Selective inventory context is absent from the delta manifest."
            )
        raw = path.read_bytes()
        if hashlib.sha256(raw).hexdigest() != chunk.context_sha256:
            raise AutoCADContextError(
                "Selective inventory context changed after delta validation."
            )
        try:
            context = parse_autocad_context(
                json.loads(raw.decode("utf-8")), expected_limits=limits
            )
        except (UnicodeError, json.JSONDecodeError) as exc:
            raise AutoCADContextError(
                "Selective inventory context is not UTF-8 JSON."
            ) from exc
        if context.context_id != context_id or len(context.entities) != chunk.entity_count:
            raise AutoCADContextError(
                "Selective inventory context identity or count is invalid."
            )
        for entity in context.entities:
            if entity.object_id in observed:
                raise AutoCADContextError(
                    "Selective inventory repeats a changed entity handle."
                )
            observed.add(entity.object_id)
            semantic_kind, semantic_id = _catalog_semantic_identity(
                entity.entity_type, entity.layer, entity.block_name, catalog_blocks
            )
            records[entity.object_id] = DrawingInventoryEntity(
                object_id=entity.object_id,
                context_id=context.context_id,
                context_sha256=chunk.context_sha256,
                entity_sha256=hashlib.sha256(_canonical(entity.to_dict())).hexdigest(),
                entity_type=entity.entity_type,
                original_type=entity.original_type,
                layer=entity.layer,
                layout=entity.layout,
                block_name=entity.block_name,
                semantic_kind=semantic_kind,
                semantic_id=semantic_id,
            )
    if observed != added | modified:
        raise AutoCADContextError(
            "Selective inventory contexts do not cover exact live candidates."
        )
    ordered = tuple(sorted(records.values(), key=lambda item: _handle_key(item.object_id)))
    expected_count = baseline.entity_count + len(added) - len(removed)
    if len(ordered) != expected_count or len(ordered) > _MAX_ENTITIES:
        raise AutoCADContextError("Selective inventory entity coverage is inconsistent.")
    summary = _inventory_summary(ordered)
    values: dict[str, object] = {
        "schema_version": 2,
        "inventory_version": SELECTIVE_INVENTORY_VERSION,
        "inventory_id": hashlib.sha256(
            _canonical({
                "baseline_inventory_sha256": baseline.inventory_sha256,
                "delta_sha256": delta.manifest_sha256,
                "catalog_sha256": catalog_sha256,
            })
        ).hexdigest()[:32],
        "index_id": baseline.index_id,
        "index_sha256": baseline.index_sha256,
        "document_id": baseline.document_id,
        "source_drawing_sha256": manifest.current_source_drawing_sha256,
        "catalog_sha256": catalog_sha256,
        "convention_profile_sha256": None,
        "route_membership_sha256": None,
        **summary,
        "entities": ordered,
        "read_only": True,
        "provider_egress_performed": False,
        "drawing_mutation_performed": False,
        "baseline_inventory_sha256": baseline.inventory_sha256,
        "delta_id": manifest.delta_id,
        "delta_sha256": delta.manifest_sha256,
        "semantic_review_required": True,
    }
    digest = hashlib.sha256(_canonical(values)).hexdigest()
    return SelectiveDrawingInventory(**values, inventory_sha256=digest)


def compare_drawing_inventories(
    before: DrawingInventory, after: DrawingInventory
) -> DrawingInventoryDiff:
    """Compare complete-index or selective immutable inventory snapshots."""
    if before.document_id != after.document_id:
        raise AutoCADContextError("Incremental comparison requires one AutoCAD document ID.")
    before_entities = {item.object_id: item for item in before.entities}
    after_entities = {item.object_id: item for item in after.entities}
    before_ids = set(before_entities)
    after_ids = set(after_entities)
    added = tuple(sorted(after_ids - before_ids, key=_handle_key))
    removed = tuple(sorted(before_ids - after_ids, key=_handle_key))
    changed: list[str] = []
    reclassified: list[str] = []
    unchanged = 0
    for object_id in sorted(before_ids & after_ids, key=_handle_key):
        left = before_entities[object_id]
        right = after_entities[object_id]
        if left.entity_sha256 != right.entity_sha256:
            changed.append(object_id)
        elif (left.semantic_kind, left.semantic_id) != (
            right.semantic_kind,
            right.semantic_id,
        ):
            reclassified.append(object_id)
        else:
            unchanged += 1
    values: dict[str, object] = {
        "schema_version": 1,
        "diff_version": DIFF_VERSION,
        "diff_id": hashlib.sha256(
            f"{before.inventory_sha256}:{after.inventory_sha256}".encode("ascii")
        ).hexdigest()[:32],
        "document_id": before.document_id,
        "before_inventory_sha256": before.inventory_sha256,
        "after_inventory_sha256": after.inventory_sha256,
        "before_source_drawing_sha256": before.source_drawing_sha256,
        "after_source_drawing_sha256": after.source_drawing_sha256,
        "added_object_ids": added,
        "removed_object_ids": removed,
        "changed_object_ids": tuple(changed),
        "reclassified_object_ids": tuple(reclassified),
        "unchanged_count": unchanged,
        "drawing_mutation_performed": False,
    }
    return DrawingInventoryDiff(
        **values, diff_sha256=hashlib.sha256(_canonical(values)).hexdigest()
    )


def write_drawing_inventory(workspace_root: Path, inventory: DrawingInventory) -> Path:
    """Atomically create or verify one immutable local inventory artifact."""
    root = workspace_root.resolve(strict=True)
    directory = root / ".ai-engineer" / "autocad" / "drawing-inventory"
    directory.mkdir(parents=True, exist_ok=True)
    target = directory / f"{inventory.inventory_id}.json"
    content = _canonical(inventory.to_dict())
    if not content or len(content) > _MAX_INVENTORY_BYTES:
        raise AutoCADContextError("Drawing inventory artifact exceeds 64 MiB.")
    if target.exists():
        if target.is_symlink() or not target.is_file() or target.read_bytes() != content:
            raise AutoCADContextError("Existing drawing inventory differs from exact evidence.")
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


def load_drawing_inventory(
    path: Path,
    *,
    expected_inventory_sha256: str | None = None,
    expected_document_id: str | None = None,
    expected_source_drawing_sha256: str | None = None,
) -> DrawingInventory:
    """Load and independently revalidate one immutable inventory artifact."""
    resolved = path.resolve(strict=True)
    if path.is_symlink() or not resolved.is_file():
        raise AutoCADContextError(
            "Drawing inventory must be a regular non-link file."
        )
    raw = resolved.read_bytes()
    if not raw or len(raw) > _MAX_INVENTORY_BYTES:
        raise AutoCADContextError("Drawing inventory is empty or exceeds 64 MiB.")
    try:
        value = json.loads(raw.decode("utf-8"))
    except (UnicodeError, json.JSONDecodeError) as exc:
        raise AutoCADContextError("Drawing inventory must be valid UTF-8 JSON.") from exc
    if not isinstance(value, dict):
        raise AutoCADContextError("Drawing inventory must use the exact schema.")
    fields = set(value)
    if fields == set(DrawingInventory.__dataclass_fields__):
        inventory_class: type[DrawingInventory] = DrawingInventory
    elif fields == set(SelectiveDrawingInventory.__dataclass_fields__):
        inventory_class = SelectiveDrawingInventory
    else:
        raise AutoCADContextError("Drawing inventory must use the exact schema.")
    values = dict(value)
    entity_values = values["entities"]
    if not isinstance(entity_values, list):
        raise AutoCADContextError("Drawing inventory entities must be a list.")
    entities: list[DrawingInventoryEntity] = []
    expected_entity_fields = set(DrawingInventoryEntity.__dataclass_fields__)
    for item in entity_values:
        if not isinstance(item, dict) or set(item) != expected_entity_fields:
            raise AutoCADContextError("Drawing inventory entity must use the exact schema.")
        entities.append(DrawingInventoryEntity(**item))
    values["entities"] = tuple(entities)
    for name in (
        "layout_counts",
        "layer_counts",
        "entity_type_counts",
        "semantic_counts",
    ):
        values[name] = _count_tuple(values[name], name)
    inventory = inventory_class(**values)
    if (
        expected_inventory_sha256 is not None
        and inventory.inventory_sha256 != expected_inventory_sha256
    ):
        raise AutoCADContextError("Drawing inventory SHA-256 is not the expected evidence.")
    if expected_document_id is not None and inventory.document_id != expected_document_id:
        raise AutoCADContextError("Drawing inventory document ID is not the expected drawing.")
    if (
        expected_source_drawing_sha256 is not None
        and inventory.source_drawing_sha256 != expected_source_drawing_sha256
    ):
        raise AutoCADContextError("Drawing inventory source SHA-256 is not the expected drawing.")
    return inventory


def _semantic_identity(
    *,
    object_id: str,
    entity_type: str,
    layer: str,
    block_name: str | None,
    catalog_blocks: dict[str, str],
    convention_blocks: dict[tuple[str, str], str],
    convention_routes: dict[str, str],
    reviewed_route_ids: set[str],
    reviewed_annotation_ids: set[str],
    route_membership_id: str | None,
) -> tuple[str, str | None]:
    layer_key = layer.casefold()
    if entity_type == "INSERT" and block_name is not None:
        convention = convention_blocks.get((layer_key, block_name.casefold()))
        if convention is not None:
            return "reviewed_device", convention
        catalog = catalog_blocks.get(block_name.casefold())
        if catalog is not None:
            return "catalog_device", catalog
    if entity_type in {"LINE", "LWPOLYLINE"}:
        if object_id in reviewed_route_ids:
            return "reviewed_route", route_membership_id
        if object_id in reviewed_annotation_ids:
            return "reviewed_annotation", route_membership_id
        if layer_key in convention_routes:
            return "reviewed_route_candidate", convention_routes[layer_key]
        if layer_key == "e-wire":
            return "catalog_route_candidate", "electrical_wire_route"
    return "unclassified", None


def _catalog_semantic_identity(
    entity_type: str,
    layer: str,
    block_name: str | None,
    catalog_blocks: dict[str, str],
) -> tuple[str, str | None]:
    """Return only catalog-stable meaning after a drawing source change."""
    return _semantic_identity(
        object_id="0",
        entity_type=entity_type,
        layer=layer,
        block_name=block_name,
        catalog_blocks=catalog_blocks,
        convention_blocks={},
        convention_routes={},
        reviewed_route_ids=set(),
        reviewed_annotation_ids=set(),
        route_membership_id=None,
    )


def _inventory_summary(
    records: tuple[DrawingInventoryEntity, ...],
) -> dict[str, object]:
    """Build exact inventory counts from one ordered entity tuple."""
    type_counts = Counter(item.entity_type for item in records)
    semantic_counts = Counter(item.semantic_kind for item in records)
    return {
        "entity_count": len(records),
        "supported_observation_count": len(records) - type_counts["UNSUPPORTED"],
        "unsupported_observation_count": type_counts["UNSUPPORTED"],
        "raw_block_count": type_counts["INSERT"],
        "raw_route_primitive_count": type_counts["LINE"] + type_counts["LWPOLYLINE"],
        "classified_device_count": (
            semantic_counts["catalog_device"] + semantic_counts["reviewed_device"]
        ),
        "classified_route_count": semantic_counts["reviewed_route"],
        "route_candidate_count": (
            semantic_counts["catalog_route_candidate"]
            + semantic_counts["reviewed_route_candidate"]
        ),
        "reviewed_annotation_count": semantic_counts["reviewed_annotation"],
        "unclassified_count": semantic_counts["unclassified"],
        "layout_counts": _counts(item.layout for item in records),
        "layer_counts": _counts(item.layer for item in records),
        "entity_type_counts": _counts(item.entity_type for item in records),
        "semantic_counts": _counts(item.semantic_kind for item in records),
    }


def _counts(values: object) -> tuple[tuple[str, int], ...]:
    counter = Counter(values)  # type: ignore[arg-type]
    return tuple(sorted(counter.items(), key=lambda item: (item[0].casefold(), item[0])))


def _count_tuple(value: object, name: str) -> tuple[tuple[str, int], ...]:
    if not isinstance(value, list):
        raise AutoCADContextError(f"Drawing inventory {name} must be a list.")
    result: list[tuple[str, int]] = []
    for item in value:
        if (
            not isinstance(item, list)
            or len(item) != 2
            or not isinstance(item[0], str)
            or not isinstance(item[1], int)
            or isinstance(item[1], bool)
            or item[1] < 1
        ):
            raise AutoCADContextError(f"Drawing inventory {name} entry is invalid.")
        result.append((item[0], item[1]))
    return tuple(result)


def _handle_key(value: str) -> tuple[int, str]:
    return len(value), value


def _canonical(value: object) -> bytes:
    return json.dumps(
        value,
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
        default=asdict,
    ).encode("utf-8")
