"""Execute and hand off the exact DB3-GA2 right-group AutoCAD overlay."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from core.file_manager import FileManager
from integrations.autocad import AutoCADManager
from integrations.drawing_handoff import handoff_verified_output


PROJECT_ROOT = Path(__file__).resolve().parents[1]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--workspace-root", type=Path, required=True)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--preview", type=Path, required=True)
    parser.add_argument("--destination", type=Path, required=True)
    parser.add_argument("--source-sha256", required=True)
    parser.add_argument("--preview-sha256", required=True)
    parser.add_argument("--confirmed", action="store_true")
    args = parser.parse_args()
    workspace = args.workspace_root.resolve(strict=True)
    files = FileManager(
        [workspace], PROJECT_ROOT / "backup" / "right-group-review", max_read_bytes=2_097_152
    )
    manager = AutoCADManager(files, timeout_seconds=300)
    artifact = manager.create_db3_ga2_right_group_review_scratch(
        args.source,
        preview_path=args.preview,
        expected_source_sha256=args.source_sha256,
        expected_preview_sha256=args.preview_sha256,
        confirmed=args.confirmed,
    )
    handoff = handoff_verified_output(
        files,
        PROJECT_ROOT / "backup" / "autocad-handoff",
        run_id=artifact.run_id,
        output_sha256=artifact.output_sha256,
        destination=args.destination,
        replace_existing=False,
        confirmed=args.confirmed,
    )
    print(json.dumps({
        "artifact": artifact.to_dict(),
        "handoff": handoff.to_dict(),
        "original_drawing_mutation_performed": False,
    }, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
