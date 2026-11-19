"""Tests for bounded, honest AutoCAD local-core performance evidence."""

from __future__ import annotations

import hashlib
import json
from pathlib import Path

import pytest

from core.autocad_performance import (
    BENCHMARK_VERSION,
    run_autocad_core_benchmark,
    write_autocad_core_benchmark,
)
from launcher import main as launcher_main


def test_core_benchmark_measures_exact_fixtures_and_retains_live_gaps() -> None:
    result = run_autocad_core_benchmark(runs=3)

    assert result["benchmark_version"] == BENCHMARK_VERSION
    assert [(item["operation"], item["fixture"], item["object_count"]) for item in result["trials"]] == [
        ("context_import", "small", 50),
        ("context_import", "maximum", 512),
        ("preview", "small", 10),
        ("preview", "maximum", 1792),
        ("calculation", "small", 1),
        ("calculation", "maximum", 256),
    ]
    assert all(len(item["durations_seconds"]) == 3 for item in result["trials"])
    assert result["trials"][1]["fixture_details"] == {"entities": 512, "layers": 128}
    assert result["trials"][3]["fixture_details"]["terminals"] == 1024
    assert result["trials"][5]["fixture_details"] == {"load_objects": 256, "routes": 256}
    assert result["autocad_context_capture_measured"] is False
    assert result["autocad_preview_display_measured"] is False
    assert result["scratch_execution_recovery_measured"] is False
    assert result["release_performance_gate_complete"] is False
    assert result["provider_egress_performed"] is False
    assert result["drawing_mutation_performed"] is False
    assert isinstance(result["total_memory_bytes"], int)
    assert result["storage_type"] == "not_collected_for_local_core_trial"
    assert result["package_sha256"] is None
    assert result["drawing_sha256"] is None
    digest = result.pop("result_sha256")
    assert digest == hashlib.sha256(
        json.dumps(result, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode()
    ).hexdigest()


def test_benchmark_report_is_atomic_no_overwrite_and_bounded(tmp_path: Path) -> None:
    path = tmp_path / "report.json"
    result = write_autocad_core_benchmark(path, runs=3)

    assert json.loads(path.read_text(encoding="utf-8")) == result
    with pytest.raises(FileExistsError, match="already exists"):
        write_autocad_core_benchmark(path, runs=3)
    for invalid in (0, 2, 21, True):
        with pytest.raises(ValueError, match="3 through 20"):
            run_autocad_core_benchmark(runs=invalid)  # type: ignore[arg-type]


def test_launcher_benchmark_creates_clean_workspace_and_rejects_escape(
    tmp_path: Path, capsys, monkeypatch
) -> None:
    package_root = tmp_path / "clean-package"
    package_root.mkdir()
    monkeypatch.setattr("launcher._root", lambda: package_root)
    assert launcher_main(["autocad-benchmark", "--runs", "3", "--output", "performance/test.json"]) == 0
    output = package_root / "workspace" / "performance" / "test.json"
    assert output.is_file()
    assert json.loads(capsys.readouterr().out)["release_performance_gate_complete"] is False

    assert launcher_main([
        "autocad-benchmark", "--runs", "3", "--output", str(tmp_path / "escape.json")
    ]) == 1
    assert "must stay inside workspace" in capsys.readouterr().err
