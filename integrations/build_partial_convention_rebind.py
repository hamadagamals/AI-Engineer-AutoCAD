"""Build one exact, self-hashed partial convention rebind from reviewed evidence."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import tempfile

from integrations.autocad_context import (
    ContextLimits,
    parse_autocad_context,
    rebind_matching_project_conventions,
)
from integrations.autocad_project_conventions import load_project_convention_profile


def build(
    workspace_root: Path,
    *,
    context_id: str,
    previous_profile_path: Path,
    drawing_path: Path,
    output_path: Path,
) -> Path:
    """Validate exact inputs and atomically write one partial-rebind profile."""
    workspace = workspace_root.resolve(strict=True)
    previous_path = _inside(workspace, previous_profile_path)
    source_path = _inside(workspace, drawing_path)
    target = _inside(workspace, output_path)
    context_path = workspace / ".ai-engineer" / "autocad" / "context" / "inbox" / f"{context_id}.json"
    context_path = _inside(workspace, context_path)
    if not context_path.is_file() or not source_path.is_file():
        raise RuntimeError("Context evidence and source drawing must both exist.")

    context_bytes = context_path.read_bytes()
    try:
        raw_context = json.loads(context_bytes.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise RuntimeError("Context evidence is not valid UTF-8 JSON.") from exc
    context = parse_autocad_context(raw_context, expected_limits=ContextLimits())
    if context.context_id != context_id:
        raise RuntimeError("Context ID does not match its evidence filename.")
    previous = load_project_convention_profile(previous_path)
    drawing_sha256 = hashlib.sha256(source_path.read_bytes()).hexdigest()
    profile = rebind_matching_project_conventions(
        previous,
        context,
        context_evidence_sha256=hashlib.sha256(context_bytes).hexdigest(),
        source_drawing_sha256=drawing_sha256,
    )
    encoded = json.dumps(
        profile.to_dict(), ensure_ascii=False, sort_keys=True, separators=(",", ":")
    ).encode("utf-8")
    _write_or_verify(target, encoded)
    return target


def _inside(workspace: Path, value: Path) -> Path:
    candidate = value if value.is_absolute() else workspace / value
    resolved = candidate.resolve(strict=False)
    if not resolved.is_relative_to(workspace):
        raise RuntimeError("Partial convention rebind paths must stay inside the workspace.")
    return resolved


def _write_or_verify(path: Path, content: bytes) -> None:
    if path.exists():
        if not path.is_file() or path.read_bytes() != content:
            raise RuntimeError(f"Existing partial convention profile differs: {path}")
        return
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary_name: str | None = None
    try:
        with tempfile.NamedTemporaryFile(dir=path.parent, delete=False) as temporary:
            temporary_name = temporary.name
            temporary.write(content)
            temporary.flush()
            os.fsync(temporary.fileno())
        os.replace(temporary_name, path)
    finally:
        if temporary_name is not None:
            Path(temporary_name).unlink(missing_ok=True)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("workspace_root", type=Path)
    parser.add_argument("--context-id", required=True)
    parser.add_argument("--previous-profile", required=True, type=Path)
    parser.add_argument("--drawing", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    arguments = parser.parse_args()
    result = build(
        arguments.workspace_root,
        context_id=arguments.context_id,
        previous_profile_path=arguments.previous_profile,
        drawing_path=arguments.drawing,
        output_path=arguments.output,
    )
    print(result)


if __name__ == "__main__":
    main()
