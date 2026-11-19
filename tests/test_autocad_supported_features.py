"""Contract checks for the honest AutoCAD supported-feature matrix."""

from pathlib import Path


PROJECT_ROOT = Path(__file__).resolve().parents[1]


def test_supported_feature_matrix_states_safety_critical_boundaries() -> None:
    text = (PROJECT_ROOT / "docs" / "AUTOCAD_SUPPORTED_FEATURES.md").read_text(
        encoding="utf-8"
    )
    for required in (
        "Live accepted",
        "Automated verified",
        "Prepared",
        "Not supported",
        "A line is not silently treated as a cable",
        "Automatic semantic meaning from arbitrary geometry",
        "Arbitrary active-drawing edits",
        "Egyptian/IEC/NEC/BS comprehensive clause database",
        "Automatic product approval/download/insertion",
        "ETAP and SOLIDWORKS Electrical",
        "representative real-project acceptance report",
    ):
        assert required in text
    assert "must not infer" in text
    assert "cable connectivity from crossings" in text
    assert "Publisher-signed installer / automatic updater" in text


def test_performance_targets_are_bounded_and_do_not_claim_unmeasured_success() -> None:
    text = (PROJECT_ROOT / "docs" / "AUTOCAD_PERFORMANCE_TARGETS.md").read_text(
        encoding="utf-8"
    )
    for required in (
        "512 entities, 128 layers",
        "1,792 ordered preview items",
        "256 reviewed loads and 256 routes",
        "<= 2 s / <= 8 s",
        "<= 1 s / <= 5 s",
        "<= 0.5 s / <= 3 s",
        "<= 5 s / <= 30 s",
        "five-run median plus maximum",
        "source drawing SHA-256",
        "one-use approval",
        "complete release performance gate is not",
    ):
        assert required in text
