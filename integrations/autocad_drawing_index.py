"""Strict local validation for complete, chunked AutoCAD drawing indexes."""

from __future__ import annotations

import hashlib
import json
import re
from dataclasses import dataclass
from pathlib import Path

from agent.exceptions import AutoCADContextError
from integrations.autocad_context import AutoCADContext, ContextLimits, parse_autocad_context


INDEX_VERSION = "autocad.drawing_index.v1"
_ID = re.compile(r"[0-9a-f]{32}")
_GUID = re.compile(
    r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}"
)
_SHA = re.compile(r"[0-9a-f]{64}")
_MAX_MANIFEST_BYTES = 1_048_576
_MAX_CONTEXT_BYTES = 4_194_304


@dataclass(frozen=True, slots=True)
class DrawingIndexChunk:
    """One exact context shard referenced by a drawing index."""

    ordinal: int
    context_id: str
    context_sha256: str
    entity_count: int


@dataclass(frozen=True, slots=True)
class DrawingIndexManifest:
    """Validated identity and completeness metadata for one drawing snapshot."""

    index_id: str
    captured_at: str
    document_id: str
    identity_source: str
    drawing_name: str
    source_drawing_sha256: str
    drawing_units: int
    database_modified_before: int
    database_modified_after: int
    maximum_entities_per_chunk: int
    maximum_total_entities: int
    maximum_chunks: int
    maximum_duration_seconds: int
    chunk_count: int
    entity_count: int
    chunks: tuple[DrawingIndexChunk, ...]


@dataclass(frozen=True, slots=True)
class DrawingIndexLoadResult:
    """Hash-bound manifest plus validated immutable chunk paths."""

    manifest: DrawingIndexManifest
    manifest_sha256: str
    manifest_bytes: int
    context_paths: tuple[Path, ...]
    unique_entity_count: int
    read_only: bool = True
    provider_egress_performed: bool = False


def load_drawing_index(
    workspace_root: Path,
    *,
    index_id: str,
    evidence_sha256: str,
    expected_document_id: str | None = None,
    expected_source_drawing_sha256: str | None = None,
    context_limits: ContextLimits | None = None,
) -> DrawingIndexLoadResult:
    """Load a complete local index and verify every referenced context shard."""
    _pattern(index_id, _ID, "drawing index ID")
    _pattern(evidence_sha256, _SHA, "drawing index SHA-256")
    if expected_document_id is not None:
        _pattern(expected_document_id, _GUID, "expected document ID")
    if expected_source_drawing_sha256 is not None:
        _pattern(
            expected_source_drawing_sha256,
            _SHA,
            "expected source drawing SHA-256",
        )
    root = workspace_root.resolve(strict=True)
    manifest_path = _contained_file(
        root,
        root
        / ".ai-engineer"
        / "autocad"
        / "drawing-index"
        / "inbox"
        / f"{index_id}.json",
    )
    raw = _read_hash_bound(manifest_path, evidence_sha256, _MAX_MANIFEST_BYTES)
    manifest = parse_drawing_index_manifest(json.loads(raw.decode("utf-8")))
    if manifest.index_id != index_id:
        raise AutoCADContextError("Drawing index ID does not match its filename.")
    if expected_document_id is not None and manifest.document_id != expected_document_id:
        raise AutoCADContextError("Drawing index document identity does not match the request.")
    if (
        expected_source_drawing_sha256 is not None
        and manifest.source_drawing_sha256 != expected_source_drawing_sha256
    ):
        raise AutoCADContextError("Drawing index source SHA-256 does not match the request.")

    limits = context_limits or ContextLimits()
    context_root = root / ".ai-engineer" / "autocad" / "context" / "inbox"
    paths: list[Path] = []
    handles: set[str] = set()
    for chunk in manifest.chunks:
        path = _contained_file(context_root.resolve(strict=True), context_root / f"{chunk.context_id}.json")
        chunk_bytes = _read_hash_bound(path, chunk.context_sha256, _MAX_CONTEXT_BYTES)
        try:
            value = json.loads(chunk_bytes.decode("utf-8"))
        except (UnicodeError, json.JSONDecodeError) as exc:
            raise AutoCADContextError("Drawing-index context is not UTF-8 JSON.") from exc
        context = parse_autocad_context(value, expected_limits=limits)
        _validate_context_chunk(context, manifest, chunk, handles)
        paths.append(path)
    if len(handles) != manifest.entity_count:
        raise AutoCADContextError("Drawing index unique-entity total is inconsistent.")
    return DrawingIndexLoadResult(
        manifest=manifest,
        manifest_sha256=evidence_sha256,
        manifest_bytes=len(raw),
        context_paths=tuple(paths),
        unique_entity_count=len(handles),
    )


def parse_drawing_index_manifest(value: object) -> DrawingIndexManifest:
    """Strictly parse a schema-v1 complete drawing-index manifest."""
    top = _exact_mapping(
        value,
        {
            "schema_version",
            "index_version",
            "index_id",
            "captured_at",
            "document",
            "scope",
            "limits",
            "complete",
            "chunk_count",
            "entity_count",
            "chunks",
            "drawing_mutation_performed",
        },
        "drawing index",
    )
    if top["schema_version"] != 1 or top["index_version"] != INDEX_VERSION:
        raise AutoCADContextError("Drawing index version is unsupported.")
    if top["complete"] is not True:
        raise AutoCADContextError("Drawing index must be explicitly complete.")
    if top["drawing_mutation_performed"] is not False:
        raise AutoCADContextError("Drawing index claims drawing mutation.")
    index_id = _text_pattern(top["index_id"], _ID, "drawing index ID")
    captured_at = _timestamp(top["captured_at"])
    document = _exact_mapping(
        top["document"],
        {
            "document_id",
            "identity_source",
            "name",
            "source_drawing_sha256",
            "drawing_units",
            "database_modified_before",
            "database_modified_after",
        },
        "drawing index document",
    )
    document_id = _text_pattern(document["document_id"], _GUID, "document ID")
    identity_source = _text(document["identity_source"], "identity source", 64)
    if identity_source not in {"fingerprint_guid", "path_sha256"}:
        raise AutoCADContextError("Drawing index identity source is unsupported.")
    drawing_name = _text(document["name"], "drawing name", 4096)
    source_sha = _text_pattern(
        document["source_drawing_sha256"], _SHA, "source drawing SHA-256"
    )
    drawing_units = _integer(document["drawing_units"], "drawing units", 0, 24)
    before = _dbmod(document["database_modified_before"])
    after = _dbmod(document["database_modified_after"])
    if before != after:
        raise AutoCADContextError("Drawing index DBMOD changed during capture.")

    scope = _exact_mapping(
        top["scope"],
        {"model_space", "paper_space", "nested_block_definitions", "external_references"},
        "drawing index scope",
    )
    if scope != {
        "model_space": True,
        "paper_space": True,
        "nested_block_definitions": False,
        "external_references": False,
    }:
        raise AutoCADContextError("Drawing index scope is unsupported or ambiguous.")
    limits = _exact_mapping(
        top["limits"],
        {
            "maximum_entities_per_chunk",
            "maximum_total_entities",
            "maximum_chunks",
            "maximum_duration_seconds",
        },
        "drawing index limits",
    )
    per_chunk = _integer(
        limits["maximum_entities_per_chunk"], "entities per chunk", 1, 2_000
    )
    maximum_total = _integer(
        limits["maximum_total_entities"], "maximum total entities", 1, 200_000
    )
    maximum_chunks = _integer(limits["maximum_chunks"], "maximum chunks", 1, 4_096)
    maximum_duration = _integer(
        limits["maximum_duration_seconds"], "maximum duration", 1, 120
    )
    chunk_count = _integer(top["chunk_count"], "chunk count", 0, maximum_chunks)
    entity_count = _integer(top["entity_count"], "entity count", 0, maximum_total)
    raw_chunks = top["chunks"]
    if not isinstance(raw_chunks, list) or len(raw_chunks) != chunk_count:
        raise AutoCADContextError("Drawing index chunk count is inconsistent.")
    chunks: list[DrawingIndexChunk] = []
    seen_ids: set[str] = set()
    for ordinal, raw_chunk in enumerate(raw_chunks):
        item = _exact_mapping(
            raw_chunk,
            {"ordinal", "context_id", "context_sha256", "entity_count"},
            "drawing index chunk",
        )
        if _integer(item["ordinal"], "chunk ordinal", 0, maximum_chunks - 1) != ordinal:
            raise AutoCADContextError("Drawing index chunk ordinals are not contiguous.")
        context_id = _text_pattern(item["context_id"], _ID, "context ID")
        if context_id in seen_ids:
            raise AutoCADContextError("Drawing index repeats a context ID.")
        seen_ids.add(context_id)
        chunks.append(
            DrawingIndexChunk(
                ordinal=ordinal,
                context_id=context_id,
                context_sha256=_text_pattern(
                    item["context_sha256"], _SHA, "context SHA-256"
                ),
                entity_count=_integer(
                    item["entity_count"], "chunk entity count", 1, per_chunk
                ),
            )
        )
    if sum(item.entity_count for item in chunks) != entity_count:
        raise AutoCADContextError("Drawing index entity total is inconsistent.")
    if (entity_count == 0) != (chunk_count == 0):
        raise AutoCADContextError("Empty drawing index metadata is inconsistent.")
    return DrawingIndexManifest(
        index_id=index_id,
        captured_at=captured_at,
        document_id=document_id,
        identity_source=identity_source,
        drawing_name=drawing_name,
        source_drawing_sha256=source_sha,
        drawing_units=drawing_units,
        database_modified_before=before,
        database_modified_after=after,
        maximum_entities_per_chunk=per_chunk,
        maximum_total_entities=maximum_total,
        maximum_chunks=maximum_chunks,
        maximum_duration_seconds=maximum_duration,
        chunk_count=chunk_count,
        entity_count=entity_count,
        chunks=tuple(chunks),
    )


def _validate_context_chunk(
    context: AutoCADContext,
    manifest: DrawingIndexManifest,
    chunk: DrawingIndexChunk,
    handles: set[str],
) -> None:
    if context.context_id != chunk.context_id:
        raise AutoCADContextError("Drawing-index context ID is inconsistent.")
    if context.document_id != manifest.document_id:
        raise AutoCADContextError("Drawing-index context document identity is inconsistent.")
    if context.drawing_units != manifest.drawing_units:
        raise AutoCADContextError("Drawing-index context units are inconsistent.")
    if context.database_modified != manifest.database_modified_after:
        raise AutoCADContextError("Drawing-index context DBMOD is inconsistent.")
    if context.source_mode != "drawing" or context.truncated:
        raise AutoCADContextError("Drawing-index context is incomplete.")
    if len(context.entities) != chunk.entity_count:
        raise AutoCADContextError("Drawing-index context entity count is inconsistent.")
    for entity in context.entities:
        if entity.object_id in handles:
            raise AutoCADContextError("Drawing index contains an overlapping entity handle.")
        handles.add(entity.object_id)


def _read_hash_bound(path: Path, expected_sha256: str, maximum_bytes: int) -> bytes:
    raw = path.read_bytes()
    if not raw or len(raw) > maximum_bytes:
        raise AutoCADContextError("Drawing-index evidence size is invalid.")
    if hashlib.sha256(raw).hexdigest() != expected_sha256:
        raise AutoCADContextError("Drawing-index evidence does not match its SHA-256.")
    return raw


def _contained_file(root: Path, path: Path) -> Path:
    resolved = path.resolve(strict=True)
    if path.is_symlink() or not resolved.is_file() or not resolved.is_relative_to(root):
        raise AutoCADContextError("Drawing-index evidence must be a contained regular file.")
    return resolved


def _exact_mapping(value: object, keys: set[str], label: str) -> dict[str, object]:
    if not isinstance(value, dict) or set(value) != keys:
        raise AutoCADContextError(f"{label} schema is invalid.")
    return value


def _text(value: object, label: str, maximum: int) -> str:
    if not isinstance(value, str) or not value or len(value.encode("utf-8")) > maximum:
        raise AutoCADContextError(f"Drawing index {label} is invalid.")
    return value


def _pattern(value: object, pattern: re.Pattern[str], label: str) -> None:
    if not isinstance(value, str) or pattern.fullmatch(value) is None:
        raise AutoCADContextError(f"Drawing index {label} is invalid.")


def _text_pattern(value: object, pattern: re.Pattern[str], label: str) -> str:
    _pattern(value, pattern, label)
    return value


def _integer(value: object, label: str, minimum: int, maximum: int) -> int:
    if (
        not isinstance(value, int)
        or isinstance(value, bool)
        or not minimum <= value <= maximum
    ):
        raise AutoCADContextError(f"Drawing index {label} is invalid.")
    return value


def _dbmod(value: object) -> int:
    parsed = _integer(value, "DBMOD", 0, 31)
    if parsed & ~24:
        raise AutoCADContextError("Drawing index DBMOD contains persistent modification bits.")
    return parsed


def _timestamp(value: object) -> str:
    text = _text(value, "timestamp", 64)
    if not text.endswith("Z") and "+00:00" not in text:
        raise AutoCADContextError("Drawing index timestamp must be UTC.")
    return text
