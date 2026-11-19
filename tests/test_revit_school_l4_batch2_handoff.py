from pathlib import Path

from integrations import revit_school_l4_batch2_handoff as handoff


ROOT = Path(__file__).resolve().parents[1]


def test_batch2_handoff_is_bound_to_exact_approval() -> None:
    assert handoff.ACTION == "execute_school_l4_batch2_save_as"
    assert handoff.VERIFY_ACTION == "verify_school_l4_batch2_handoff"
    assert handoff.APPROVED_FINGERPRINT == (
        "565529255B384C229F43B446A1A7169037E07DF63AD0F17D744FF37F50943801"
    )


def test_bridge_contains_fixed_batch2_execution_and_reopen_verification() -> None:
    bridge = (ROOT / "integrations/revit_readonly_plugin/ReadOnlyRequestBridge.cs").read_text(encoding="utf-8")
    source = (ROOT / "integrations/revit_readonly_plugin/SchoolL4Batch2Handoff.cs").read_text(encoding="utf-8")
    build = (ROOT / "integrations/revit_readonly_plugin/build.ps1").read_text(encoding="utf-8")
    assert '"execute_school_l4_batch2_save_as"' in bridge
    assert '"verify_school_l4_batch2_handoff"' in bridge
    assert "SchoolL4Batch2Handoff.Execute" in bridge
    assert "SchoolL4Batch2Handoff.Verify" in bridge
    assert handoff.APPROVED_FINGERPRINT in source
    assert "ExistingCount = 23" in source
    assert "AddedCount = 22" in source
    assert "TotalCount = 45" in source
    assert "VerifyExisting(document, preview" in source
    assert "FileShare.ReadWrite | FileShare.Delete" in source
    assert 'Join-Path $pluginRoot "SchoolL4Batch2Handoff.cs"' in build
