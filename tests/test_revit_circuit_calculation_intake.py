from __future__ import annotations

import hashlib
import json
from pathlib import Path

import pytest

from agent.exceptions import RevitIntegrationError
from integrations.revit_circuit_calculation_intake import (
    _payload_hash,
    create_circuit_calculation_intake,
)


def _review(tmp_path: Path) -> tuple[Path, Path]:
    root = tmp_path / "project"
    evidence = root / "workspace" / "evidence"
    evidence.mkdir(parents=True)
    review: dict[str, object] = {
        "schema_version": 1,
        "source_sha256": hashlib.sha256(b"rvt").hexdigest().upper(),
        "preview_sha256": "A" * 64,
        "query_request_id": "request-1",
        "fixture_element_id": 10,
        "target_system_id": 20,
        "panel_name": "P109",
        "circuit_number": "5",
        "observed": {
            "voltage_v": 120.0,
            "rating_a": 20.0,
            "wire_size": "1-#12, 1-#12, 1-#12",
            "wire_type": {"material": "Copper", "insulation": "THWN"},
            "panel": {"element_id": 30},
        },
        "document_mutation_performed": False,
    }
    review["review_sha256"] = _payload_hash(review, "review_sha256")
    path = evidence / "review.json"
    path.write_text(json.dumps(review), encoding="utf-8")
    return root, path


def test_intake_is_bound_and_blocks_unreviewed_calculations(tmp_path: Path) -> None:
    root, review = _review(tmp_path)
    output = root / "workspace" / "evidence" / "intake.json"
    value = create_circuit_calculation_intake(
        workspace_root=root,
        engineering_review_path=review,
        output_path=output,
    )
    assert value["status"] == "blocked_missing_reviewed_inputs"
    assert value["missing_input_count"] == 25
    assert value["ready_for_ampacity_calculation"] is False
    assert value["ready_for_short_circuit_calculation"] is False
    assert value["ready_for_protection_coordination"] is False
    assert value["calculation_performed"] is False
    assert value["document_mutation_performed"] is False
    assert value["intake_sha256"] == _payload_hash(value, "intake_sha256")


def test_intake_rejects_changed_review_and_existing_output(tmp_path: Path) -> None:
    root, review = _review(tmp_path)
    value = json.loads(review.read_text(encoding="utf-8"))
    value["panel_name"] = "CHANGED"
    review.write_text(json.dumps(value), encoding="utf-8")
    with pytest.raises(RevitIntegrationError, match="SHA-256"):
        create_circuit_calculation_intake(
            workspace_root=root,
            engineering_review_path=review,
            output_path=root / "workspace" / "evidence" / "intake.json",
        )

    root, review = _review(tmp_path / "second")
    output = root / "workspace" / "evidence" / "intake.json"
    output.write_text("existing", encoding="utf-8")
    with pytest.raises(RevitIntegrationError, match="paths"):
        create_circuit_calculation_intake(
            workspace_root=root,
            engineering_review_path=review,
            output_path=output,
        )
