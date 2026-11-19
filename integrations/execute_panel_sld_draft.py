"""Execute and hand off one exact reviewed DB3-GA2 SLD in AutoCAD 2021."""

from __future__ import annotations

import argparse
import json
from datetime import UTC, datetime
from pathlib import Path

from core.file_manager import FileManager
from integrations.autocad import AutoCADManager
from integrations.drawing_handoff import handoff_verified_output


PROJECT_ROOT = Path(__file__).resolve().parents[1]


def execute(
    *,
    workspace_root: Path,
    source: Path,
    preview: Path,
    destination: Path,
    source_sha256: str,
    preview_sha256: str,
    confirmed: bool,
) -> dict[str, object]:
    """Run the scratch producer, verify it, and create one new handoff drawing."""
    workspace = workspace_root.expanduser().resolve(strict=True)
    files = FileManager(
        [workspace],
        PROJECT_ROOT / "backup" / "panel-sld-file-operations",
        max_read_bytes=2_097_152,
    )
    manager = AutoCADManager(
        files,
        drawing_handoff_backup_dir=PROJECT_ROOT / "backup" / "autocad-handoff",
        timeout_seconds=300,
    )
    artifact = manager.create_panel_sld_draft_scratch(
        source,
        preview_path=preview,
        expected_source_sha256=source_sha256,
        expected_preview_sha256=preview_sha256,
        confirmed=confirmed,
    )
    handoff = handoff_verified_output(
        files,
        PROJECT_ROOT / "backup" / "autocad-handoff",
        run_id=artifact.run_id,
        output_sha256=artifact.output_sha256,
        destination=destination,
        replace_existing=False,
        confirmed=confirmed,
    )
    result: dict[str, object] = {
        "schema_version": 1,
        "recorded_at": datetime.now(UTC).isoformat(),
        "operation": "autocad.create_panel_sld_draft_scratch",
        "artifact": artifact.to_dict(),
        "handoff": handoff.to_dict(),
        "source_unchanged": artifact.source_unchanged,
        "scratch_changed": artifact.scratch_changed,
        "drawing_mutation_performed": True,
        "original_drawing_mutation_performed": False,
    }
    report = workspace / "workspace" / "reports" / "db3-ga2-sld-autocad-execution-20260824.json"
    files.write_text(report, json.dumps(result, ensure_ascii=False, indent=2) + "\n")
    result["execution_report"] = str(report)
    return result


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
    print(json.dumps(execute(
        workspace_root=args.workspace_root,
        source=args.source,
        preview=args.preview,
        destination=args.destination,
        source_sha256=args.source_sha256,
        preview_sha256=args.preview_sha256,
        confirmed=args.confirmed,
    ), ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
