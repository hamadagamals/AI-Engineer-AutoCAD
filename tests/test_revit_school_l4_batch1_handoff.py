from pathlib import Path

from integrations import revit_school_l4_batch1_handoff as handoff


ROOT = Path(__file__).resolve().parents[1]


def test_school_l4_handoff_uses_exact_approved_contract() -> None:
    assert handoff.ACTION == "execute_school_l4_batch1_save_as"
    assert handoff.VERIFY_ACTION == "verify_school_l4_batch1_handoff"
    assert handoff.APPROVED_FINGERPRINT == (
        "5E78A601B372501A07612B4B120B9E73878F7FEAD4718C4CC5F39F40EB6F273C"
    )


def test_revit_bridge_contains_fixed_school_l4_execution_and_verification() -> None:
    bridge = (ROOT / "integrations/revit_readonly_plugin/ReadOnlyRequestBridge.cs").read_text(
        encoding="utf-8"
    )
    implementation = (ROOT / "integrations/revit_readonly_plugin/SchoolL4Batch1Handoff.cs").read_text(
        encoding="utf-8"
    )
    build = (ROOT / "integrations/revit_readonly_plugin/build.ps1").read_text(encoding="utf-8")
    assert '"execute_school_l4_batch1_save_as"' in bridge
    assert '"verify_school_l4_batch1_handoff"' in bridge
    assert "SchoolL4Batch1Handoff.Execute" in bridge
    assert "SchoolL4Batch1Handoff.Verify" in bridge
    assert handoff.APPROVED_FINGERPRINT in implementation
    assert "ExactCount = 23" in implementation
    assert "MountingZmm - 9500.0" in implementation
    assert 'mark.Set("AIENG-" + point.PointId)' in implementation
    assert "FileShare.ReadWrite | FileShare.Delete" in implementation
    assert 'Join-Path $pluginRoot "SchoolL4Batch1Handoff.cs"' in build
