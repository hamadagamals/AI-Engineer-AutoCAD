import hashlib
from pathlib import Path

import pytest

from agent.exceptions import RevitIntegrationError
from integrations import revit_school_outlet_isolated_test as module


def test_preview_pins_family_and_new_output(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    family = tmp_path / "sample.rfa"
    family.write_bytes(b"isolated family test")
    template = tmp_path / "sample.rte"
    template.write_bytes(b"electrical template")
    target = tmp_path / "new.rvt"
    monkeypatch.setattr(module, "FAMILY_PATH", family)
    monkeypatch.setattr(module, "FAMILY_SHA", hashlib.sha256(family.read_bytes()).hexdigest().upper())
    monkeypatch.setattr(module, "TEMPLATE_PATH", template)
    monkeypatch.setattr(module, "TEMPLATE_SHA", hashlib.sha256(template.read_bytes()).hexdigest().upper())
    monkeypatch.setattr(module, "OUTPUT_PATH", target)
    preview, file, sha = module.prepare_preview(
        bridge_root=tmp_path / "bridge", evidence_dir=tmp_path / "evidence")
    assert preview["requested_voltage_v"] == 220
    assert preview["requested_load_va"] == 180
    assert module.file_sha256(file) == sha
    assert not target.exists()


def test_preview_refuses_existing_output(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    family = tmp_path / "sample.rfa"
    family.write_bytes(b"isolated family test")
    template = tmp_path / "sample.rte"
    template.write_bytes(b"electrical template")
    target = tmp_path / "new.rvt"
    target.write_bytes(b"existing model")
    monkeypatch.setattr(module, "FAMILY_PATH", family)
    monkeypatch.setattr(module, "FAMILY_SHA", hashlib.sha256(family.read_bytes()).hexdigest().upper())
    monkeypatch.setattr(module, "TEMPLATE_PATH", template)
    monkeypatch.setattr(module, "TEMPLATE_SHA", hashlib.sha256(template.read_bytes()).hexdigest().upper())
    monkeypatch.setattr(module, "OUTPUT_PATH", target)
    with pytest.raises(RevitIntegrationError):
        module.prepare_preview(bridge_root=tmp_path / "bridge", evidence_dir=tmp_path)
