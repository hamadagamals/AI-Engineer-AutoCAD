from pathlib import Path

from integrations import revit_school_classroom_sample_handoff as handoff


ROOT = Path(__file__).resolve().parents[1]


def test_sample_handoff_is_bound_to_exact_approval() -> None:
    assert handoff.ACTION == "execute_school_classroom_sample_save_as"
    assert handoff.VERIFY_ACTION == "verify_school_classroom_sample_handoff"
    assert handoff.APPROVED_FINGERPRINT == (
        "0293A7A6A70095DF2C2845754998C1A555C4C9315D48FB50B7E4BF2E9939B116"
    )


def test_bridge_contains_fixed_sample_execution_and_reopen_verification() -> None:
    bridge = (ROOT / "integrations/revit_readonly_plugin/ReadOnlyRequestBridge.cs").read_text(encoding="utf-8")
    source = (ROOT / "integrations/revit_readonly_plugin/SchoolClassroomSampleHandoff.cs").read_text(encoding="utf-8")
    build = (ROOT / "integrations/revit_readonly_plugin/build.ps1").read_text(encoding="utf-8")
    assert '"execute_school_classroom_sample_save_as"' in bridge
    assert '"verify_school_classroom_sample_handoff"' in bridge
    assert "SchoolClassroomSampleHandoff.Execute" in bridge
    assert "SchoolClassroomSampleHandoff.Verify" in bridge
    assert handoff.APPROVED_FINGERPRINT in source
    assert "ExactCount = 8" in source
    assert "PreservedL4Count = 45" in source
    assert "SetApprovedParameters(symbol" in source
    assert "FileShare.ReadWrite | FileShare.Delete" in source
    assert 'Join-Path $pluginRoot "SchoolClassroomSampleHandoff.cs"' in build
