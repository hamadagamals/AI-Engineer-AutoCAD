"""Strict validation for one-use, in-session AutoCAD drawing-change journals."""

from __future__ import annotations

import hashlib
import argparse
import json
import re
from dataclasses import dataclass
from pathlib import Path

from agent.exceptions import AutoCADContextError
from integrations.autocad_context import ContextLimits, parse_autocad_context
from integrations.autocad_drawing_index import DrawingIndexChunk
from integrations.autocad_operator_channel import (
    ActiveAutoCADDocument,
    discover_active_documents,
    enqueue_operator_command,
    wait_for_operator_result,
)


DELTA_VERSION = "autocad.drawing_delta.v1"
_ID = re.compile(r"[0-9a-f]{32}")
_GUID = re.compile(
    r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}"
)
_SHA = re.compile(r"[0-9a-f]{64}")
_HANDLE = re.compile(r"[0-9A-F]{1,32}")
_MAX_BYTES = 1_048_576


@dataclass(frozen=True, slots=True)
class DrawingDeltaManifest:
    delta_id: str
    captured_at: str
    document_id: str
    identity_source: str
    drawing_name: str
    drawing_units: int
    baseline_source_drawing_sha256: str
    current_source_drawing_sha256: str
    database_modified_before: int
    database_modified_after: int
    baseline_index_id: str
    baseline_index_sha256: str
    baseline_entity_count: int
    maximum_candidates: int
    added_object_ids: tuple[str, ...]
    modified_object_ids: tuple[str, ...]
    removed_object_ids: tuple[str, ...]
    candidate_count: int
    context_entity_count: int
    chunks: tuple[DrawingIndexChunk, ...]


@dataclass(frozen=True, slots=True)
class DrawingDeltaLoadResult:
    manifest: DrawingDeltaManifest
    manifest_sha256: str
    context_paths: tuple[Path, ...]
    captured_object_ids: tuple[str, ...]
    read_only: bool = True
    provider_egress_performed: bool = False
    drawing_mutation_performed: bool = False


@dataclass(frozen=True, slots=True)
class CapturedDrawingDelta:
    """One operator result bound to its independently validated delta manifest."""

    active_document: ActiveAutoCADDocument
    delta: DrawingDeltaLoadResult
    operator_result_path: Path
    operator_result_sha256: str


def capture_active_drawing_delta(
    workspace_root: Path,
    *,
    baseline_index_id: str,
    baseline_index_sha256: str,
    drawing_name: str | None = None,
    timeout_seconds: float = 120.0,
) -> CapturedDrawingDelta:
    """Request and validate one exact in-session delta without GUI interaction."""
    root = workspace_root.resolve(strict=True)
    documents = discover_active_documents(root)
    if drawing_name is not None:
        expected_name = Path(drawing_name).name.casefold()
        documents = tuple(
            item for item in documents if item.drawing_name.casefold() == expected_name
        )
    if len(documents) != 1:
        detail = "no matching" if not documents else "multiple matching"
        raise AutoCADContextError(
            f"Drawing-delta capture found {detail} active AutoCAD documents."
        )
    active = documents[0]
    queued = enqueue_operator_command(
        root,
        document_id=active.document_id,
        source_drawing_sha256=active.source_drawing_sha256,
        requested_action="capture_drawing_delta",
    )
    result, result_path, result_sha = wait_for_operator_result(
        root, queued=queued, timeout_seconds=timeout_seconds
    )
    if (
        result.status != "completed"
        or result.context_id is None
        or result.context_sha256 is None
    ):
        raise AutoCADContextError("AutoCAD drawing-delta capture did not complete.")
    delta = load_drawing_delta(
        root,
        delta_id=result.context_id,
        evidence_sha256=result.context_sha256,
        expected_document_id=active.document_id,
        expected_current_source_sha256=active.source_drawing_sha256,
        expected_baseline_index_id=baseline_index_id,
        expected_baseline_index_sha256=baseline_index_sha256,
    )
    return CapturedDrawingDelta(active, delta, result_path, result_sha)


def load_drawing_delta(
    workspace_root: Path,
    *,
    delta_id: str,
    evidence_sha256: str,
    expected_document_id: str | None = None,
    expected_current_source_sha256: str | None = None,
    expected_baseline_index_id: str | None = None,
    expected_baseline_index_sha256: str | None = None,
    context_limits: ContextLimits | None = None,
) -> DrawingDeltaLoadResult:
    """Load a complete one-use journal and validate all changed-entity shards."""
    _match(delta_id, _ID, "delta ID")
    _match(evidence_sha256, _SHA, "delta SHA-256")
    root = workspace_root.resolve(strict=True)
    manifest_path = _file(
        root,
        root / ".ai-engineer" / "autocad" / "drawing-delta" / "inbox"
        / f"{delta_id}.json",
    )
    raw = manifest_path.read_bytes()
    if not raw or len(raw) > _MAX_BYTES or hashlib.sha256(raw).hexdigest() != evidence_sha256:
        raise AutoCADContextError("Drawing delta does not match its exact SHA-256 evidence.")
    try:
        manifest = parse_drawing_delta_manifest(json.loads(raw.decode("utf-8")))
    except (UnicodeError, json.JSONDecodeError) as exc:
        raise AutoCADContextError("Drawing delta must be valid UTF-8 JSON.") from exc
    if manifest.delta_id != delta_id:
        raise AutoCADContextError("Drawing delta ID does not match its filename.")
    expected_pairs = (
        (expected_document_id, manifest.document_id, _GUID, "document ID"),
        (expected_current_source_sha256, manifest.current_source_drawing_sha256, _SHA, "current source SHA-256"),
        (expected_baseline_index_id, manifest.baseline_index_id, _ID, "baseline index ID"),
        (expected_baseline_index_sha256, manifest.baseline_index_sha256, _SHA, "baseline index SHA-256"),
    )
    for expected, observed, pattern, label in expected_pairs:
        if expected is not None:
            _match(expected, pattern, f"expected {label}")
            if expected != observed:
                raise AutoCADContextError(f"Drawing delta {label} does not match expected evidence.")
    context_root = root / ".ai-engineer" / "autocad" / "context" / "inbox"
    limits = context_limits or ContextLimits()
    handles: set[str] = set()
    paths: list[Path] = []
    for chunk in manifest.chunks:
        path = _file(context_root.resolve(strict=True), context_root / f"{chunk.context_id}.json")
        chunk_raw = path.read_bytes()
        if (
            not chunk_raw
            or len(chunk_raw) > 4_194_304
            or hashlib.sha256(chunk_raw).hexdigest() != chunk.context_sha256
        ):
            raise AutoCADContextError("Drawing-delta context does not match its SHA-256.")
        context = parse_autocad_context(
            json.loads(chunk_raw.decode("utf-8")), expected_limits=limits
        )
        if (
            context.context_id != chunk.context_id
            or context.document_id != manifest.document_id
            or context.drawing_units != manifest.drawing_units
            or context.database_modified != manifest.database_modified_after
            or context.source_mode != "drawing"
            or context.truncated
            or len(context.entities) != chunk.entity_count
        ):
            raise AutoCADContextError("Drawing-delta context identity or completeness is invalid.")
        for entity in context.entities:
            if entity.object_id in handles:
                raise AutoCADContextError("Drawing delta repeats a changed entity handle.")
            handles.add(entity.object_id)
        paths.append(path)
    expected_captured = set(manifest.added_object_ids) | set(manifest.modified_object_ids)
    if handles != expected_captured:
        raise AutoCADContextError("Drawing-delta contexts do not exactly cover live candidates.")
    return DrawingDeltaLoadResult(
        manifest,
        evidence_sha256,
        tuple(paths),
        tuple(sorted(handles, key=lambda value: (len(value), value))),
    )


def parse_drawing_delta_manifest(value: object) -> DrawingDeltaManifest:
    top = _mapping(
        value,
        {"schema_version", "delta_version", "delta_id", "captured_at", "document",
         "baseline", "complete_in_session_journal", "one_use", "maximum_candidates",
         "added_object_ids", "modified_object_ids", "removed_object_ids", "candidate_count",
         "context_entity_count", "chunk_count", "chunks", "drawing_mutation_performed"},
        "drawing delta",
    )
    if (
        top["schema_version"] != 1
        or top["delta_version"] != DELTA_VERSION
        or top["complete_in_session_journal"] is not True
        or top["one_use"] is not True
        or top["drawing_mutation_performed"] is not False
    ):
        raise AutoCADContextError("Drawing delta version or safety flags are invalid.")
    document = _mapping(
        top["document"],
        {"document_id", "identity_source", "name", "drawing_units",
         "baseline_source_drawing_sha256", "current_source_drawing_sha256",
         "database_modified_before", "database_modified_after"},
        "drawing delta document",
    )
    baseline = _mapping(
        top["baseline"], {"index_id", "index_sha256", "entity_count"},
        "drawing delta baseline",
    )
    maximum = _integer(top["maximum_candidates"], "maximum candidates", 1, 4096)
    added = _handles(top["added_object_ids"], "added handles", maximum)
    modified = _handles(top["modified_object_ids"], "modified handles", maximum)
    removed = _handles(top["removed_object_ids"], "removed handles", maximum)
    all_handles = added + modified + removed
    if len(all_handles) != len(set(all_handles)):
        raise AutoCADContextError("Drawing delta candidate partitions overlap.")
    candidate_count = _integer(top["candidate_count"], "candidate count", 0, maximum)
    if candidate_count != len(all_handles):
        raise AutoCADContextError("Drawing delta candidate count is inconsistent.")
    context_count = _integer(top["context_entity_count"], "context entity count", 0, maximum)
    if context_count != len(added) + len(modified):
        raise AutoCADContextError("Drawing delta context count is inconsistent.")
    raw_chunks = top["chunks"]
    chunk_count = _integer(top["chunk_count"], "chunk count", 0, 4096)
    if not isinstance(raw_chunks, list) or len(raw_chunks) != chunk_count:
        raise AutoCADContextError("Drawing delta chunk count is inconsistent.")
    chunks: list[DrawingIndexChunk] = []
    for ordinal, raw_chunk in enumerate(raw_chunks):
        item = _mapping(raw_chunk, {"ordinal", "context_id", "context_sha256", "entity_count"}, "delta chunk")
        if item["ordinal"] != ordinal:
            raise AutoCADContextError("Drawing delta chunk ordinals are not contiguous.")
        chunks.append(DrawingIndexChunk(
            ordinal,
            _text_pattern(item["context_id"], _ID, "context ID"),
            _text_pattern(item["context_sha256"], _SHA, "context SHA-256"),
            _integer(item["entity_count"], "chunk entity count", 1, 512),
        ))
    if sum(item.entity_count for item in chunks) != context_count or (
        (context_count == 0) != (chunk_count == 0)
    ):
        raise AutoCADContextError("Drawing delta shard total is inconsistent.")
    before = _dbmod(document["database_modified_before"])
    after = _dbmod(document["database_modified_after"])
    if before != after:
        raise AutoCADContextError("Drawing delta changed DBMOD during capture.")
    identity_source = _text(document["identity_source"], "identity source", 64)
    if identity_source not in {"fingerprint_guid", "path_sha256"}:
        raise AutoCADContextError("Drawing delta identity source is unsupported.")
    return DrawingDeltaManifest(
        _text_pattern(top["delta_id"], _ID, "delta ID"),
        _text(top["captured_at"], "timestamp", 64),
        _text_pattern(document["document_id"], _GUID, "document ID"),
        identity_source,
        _text(document["name"], "drawing name", 4096),
        _integer(document["drawing_units"], "drawing units", 0, 24),
        _text_pattern(document["baseline_source_drawing_sha256"], _SHA, "baseline source SHA-256"),
        _text_pattern(document["current_source_drawing_sha256"], _SHA, "current source SHA-256"),
        before, after,
        _text_pattern(baseline["index_id"], _ID, "baseline index ID"),
        _text_pattern(baseline["index_sha256"], _SHA, "baseline index SHA-256"),
        _integer(baseline["entity_count"], "baseline entity count", 0, 200000),
        maximum, added, modified, removed, candidate_count, context_count, tuple(chunks),
    )


def _mapping(value: object, keys: set[str], label: str) -> dict[str, object]:
    if not isinstance(value, dict) or set(value) != keys:
        raise AutoCADContextError(f"{label} schema is invalid.")
    return value


def _handles(value: object, label: str, maximum: int) -> tuple[str, ...]:
    if not isinstance(value, list) or len(value) > maximum:
        raise AutoCADContextError(f"Drawing delta {label} are invalid.")
    result = tuple(_text_pattern(item, _HANDLE, label) for item in value)
    if len(result) != len(set(result)) or result != tuple(sorted(result, key=lambda item: int(item, 16))):
        raise AutoCADContextError(f"Drawing delta {label} are not unique and ordered.")
    return result


def _file(root: Path, path: Path) -> Path:
    resolved = path.resolve(strict=True)
    if path.is_symlink() or not resolved.is_file() or not resolved.is_relative_to(root):
        raise AutoCADContextError("Drawing-delta evidence must be a contained regular file.")
    return resolved


def _match(value: object, pattern: re.Pattern[str], label: str) -> None:
    if not isinstance(value, str) or pattern.fullmatch(value) is None:
        raise AutoCADContextError(f"Drawing delta {label} is invalid.")


def _text_pattern(value: object, pattern: re.Pattern[str], label: str) -> str:
    _match(value, pattern, label)
    return value


def _text(value: object, label: str, maximum: int) -> str:
    if not isinstance(value, str) or not value or len(value.encode("utf-8")) > maximum:
        raise AutoCADContextError(f"Drawing delta {label} is invalid.")
    return value


def _integer(value: object, label: str, minimum: int, maximum: int) -> int:
    if not isinstance(value, int) or isinstance(value, bool) or not minimum <= value <= maximum:
        raise AutoCADContextError(f"Drawing delta {label} is invalid.")
    return value


def _dbmod(value: object) -> int:
    result = _integer(value, "DBMOD", 0, 31)
    if result & ~24:
        raise AutoCADContextError("Drawing delta DBMOD contains persistent bits.")
    return result


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Capture one read-only in-session AutoCAD drawing delta."
    )
    parser.add_argument("workspace_root", type=Path)
    parser.add_argument("baseline_index_id")
    parser.add_argument("baseline_index_sha256")
    parser.add_argument("--drawing-name")
    parser.add_argument("--timeout-seconds", type=float, default=120.0)
    arguments = parser.parse_args()
    captured = capture_active_drawing_delta(
        arguments.workspace_root,
        baseline_index_id=arguments.baseline_index_id,
        baseline_index_sha256=arguments.baseline_index_sha256,
        drawing_name=arguments.drawing_name,
        timeout_seconds=arguments.timeout_seconds,
    )
    print(json.dumps({
        "delta_id": captured.delta.manifest.delta_id,
        "delta_sha256": captured.delta.manifest_sha256,
        "candidate_count": captured.delta.manifest.candidate_count,
        "added_count": len(captured.delta.manifest.added_object_ids),
        "modified_count": len(captured.delta.manifest.modified_object_ids),
        "removed_count": len(captured.delta.manifest.removed_object_ids),
        "operator_result_sha256": captured.operator_result_sha256,
        "drawing_mutation_performed": False,
    }, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
