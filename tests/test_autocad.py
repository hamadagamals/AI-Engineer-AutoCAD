"""Tests for bounded AutoCAD 2021 detection and scratch inspection."""

from __future__ import annotations

import hashlib
import json
import re
from pathlib import Path

import pytest

from agent.exceptions import (
    AutoCADIntegrationError,
    ConfirmationRequiredError,
    PathPolicyError,
)
from core.file_manager import FileManager
from integrations.autocad import (
    AUTOCAD_2021_PROG_ID,
    AUTOCAD_2023_PROG_ID,
    AutoCADInstallation,
    AutoCADManager,
    CircuitPlacement,
    _ProcessResult,
    _parse_circuit_fragment_markers,
    _parse_maximum_performance_fixture_markers,
    _parse_panel_sld_fixture_markers,
    _parse_inventory_markers,
    _parse_line_markers,
    _parse_markers,
    _parse_symbol_markers,
    _parse_wire_route_markers,
)
from integrations.electrical_symbols import (
    ElectricalSymbolCatalog,
    calculate_definition_sha256,
)


PROJECT_ROOT = Path(__file__).resolve().parents[1]
SYMBOL_CATALOG = PROJECT_ROOT / "plugins" / "electrical_symbols" / "catalog.json"
SYMBOL_HASH = "8a2f891627de4c1c5d80670a1aed95a6f1b0f602ea4bddd2e3dc94232ea82a95"


def _installation(tmp_path: Path) -> AutoCADInstallation:
    root = tmp_path / "AutoCAD 2021"
    root.mkdir(parents=True)
    executable = root / "acad.exe"
    core = root / "accoreconsole.exe"
    executable.write_bytes(b"test acad")
    core.write_bytes(b"test core")
    return AutoCADInstallation(
        product_name="AutoCAD 2021",
        release="24.0.47.0",
        language="English",
        install_root=root,
        executable=executable,
        core_console=core,
        activex_registered=True,
    )


def _installation_2023(tmp_path: Path) -> AutoCADInstallation:
    root = tmp_path / "AutoCAD 2023"
    root.mkdir(parents=True)
    executable = root / "acad.exe"
    core = root / "accoreconsole.exe"
    executable.write_bytes(b"test acad 2023")
    core.write_bytes(b"test core 2023")
    return AutoCADInstallation(
        product_name="AutoCAD 2023",
        release="24.2.53.0",
        language="English",
        install_root=root,
        executable=executable,
        core_console=core,
        activex_registered=True,
    )


def _manager(
    tmp_path: Path,
) -> tuple[AutoCADManager, Path, AutoCADInstallation]:
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    files = FileManager([workspace], tmp_path / "backup")
    installation = _installation(tmp_path / "installed")
    manager = AutoCADManager(files, locator=lambda: installation)
    return manager, workspace, installation


def test_detect_reports_absence_without_claiming_supported_methods(tmp_path: Path) -> None:
    files = FileManager([tmp_path / "workspace"], tmp_path / "backup")
    manager = AutoCADManager(files, locator=lambda: None)

    status = manager.detect()

    assert status.to_dict() == {
        "installed": False,
        "product_name": None,
        "release": None,
        "language": None,
        "executable_available": False,
        "core_console_available": False,
        "activex_available": False,
        "prog_id": AUTOCAD_2021_PROG_ID,
        "supported_methods": [],
    }


def test_detect_returns_only_non_secret_validated_capabilities(tmp_path: Path) -> None:
    manager, _, installation = _manager(tmp_path)

    status = manager.detect()

    assert status.installed is True
    assert status.release == "24.0.47.0"
    assert status.prog_id == "AutoCAD.Application.24"
    assert status.supported_methods == (
        "autolisp_secureload",
        "activex_com",
        "core_console_readonly",
    )
    assert str(installation.install_root) not in str(status.to_dict())


def test_detect_supports_autocad_2023_with_versioned_progid(tmp_path: Path) -> None:
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    files = FileManager([workspace], tmp_path / "backup")
    installation = _installation_2023(tmp_path / "installed-2023")
    manager = AutoCADManager(files, locator=lambda: installation)

    status = manager.detect()

    assert status.installed is True
    assert status.product_name == "AutoCAD 2023"
    assert status.release == "24.2.53.0"
    assert status.prog_id == AUTOCAD_2023_PROG_ID
    assert status.supported_methods == (
        "autolisp_secureload",
        "activex_com",
        "core_console_readonly",
    )


def test_detect_rejects_executable_escape_and_wrong_release(tmp_path: Path) -> None:
    _, workspace, installation = _manager(tmp_path)
    files = FileManager([workspace], tmp_path / "backup-validation")
    outside = tmp_path / "acad.exe"
    outside.write_bytes(b"outside")
    escaped = AutoCADInstallation(
        product_name=installation.product_name,
        release=installation.release,
        language=installation.language,
        install_root=installation.install_root,
        executable=outside,
        core_console=installation.core_console,
        activex_registered=True,
    )
    escape_manager = AutoCADManager(files, locator=lambda: escaped)
    with pytest.raises(AutoCADIntegrationError, match="escapes"):
        escape_manager.detect()

    wrong = AutoCADInstallation(
        product_name="AutoCAD 2022",
        release="24.1.0.0",
        language="English",
        install_root=installation.install_root,
        executable=installation.executable,
        core_console=installation.core_console,
        activex_registered=True,
    )
    wrong_manager = AutoCADManager(files, locator=lambda: wrong)
    with pytest.raises(AutoCADIntegrationError, match="not supported AutoCAD 2021/2023"):
        wrong_manager.detect()


def test_scratch_inspection_copies_source_and_forces_read_only_execution(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manager, workspace, installation = _manager(tmp_path)
    source = workspace / "sample.dwg"
    source.write_bytes(b"DWG test content")
    captured: dict[str, object] = {}

    def fake_run(command: tuple[str, ...], *, cwd: Path) -> _ProcessResult:
        captured["command"] = command
        captured["cwd"] = cwd
        return _ProcessResult(
            0,
            "\nAIENG_ACADVER=24.0\nAIENG_INSUNITS=4\n"
            "AIENG_MEASUREMENT=1\nAIENG_TILEMODE=1\nAIENG_DBMOD=0\nAIENG_DONE=1\n",
            "",
            False,
            False,
        )

    monkeypatch.setattr(manager, "_run_core_console", fake_run)

    result = manager.inspect_scratch(source, confirmed=True)

    assert result.source == source.resolve()
    assert result.scratch_drawing.read_bytes() == source.read_bytes()
    assert result.scratch_drawing != source
    assert result.read_only is True
    assert result.scratch_copy is True
    assert result.source_unchanged is True
    assert result.scratch_unchanged is True
    assert result.insertion_units == 4
    assert result.database_modified == 0
    command = captured["command"]
    assert isinstance(command, tuple)
    assert command[0] == str(installation.core_console)
    assert "/readonly" in command
    assert "/isolate" not in command
    assert str(source) not in command
    script = Path(captured["cwd"]) / "inspect.scr"  # type: ignore[arg-type]
    script_text = script.read_text(encoding="utf-8")
    assert "getvar" in script_text
    assert script_text.startswith("(progn\n")
    assert script_text.endswith("(princ)\n)\n")
    assert "save" not in script_text.casefold()
    assert "load" not in script_text.casefold()
    assert (Path(captured["cwd"]) / "console.stdout.txt").is_file()  # type: ignore[arg-type]


def test_drawing_inventory_returns_only_bounded_aggregate_counts(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manager, workspace, installation = _manager(tmp_path)
    source = workspace / "inventory.dwg"
    source.write_bytes(b"DWG inventory content")
    captured: dict[str, object] = {}

    def fake_run(command: tuple[str, ...], *, cwd: Path) -> _ProcessResult:
        captured["command"] = command
        captured["cwd"] = cwd
        return _ProcessResult(
            0,
            "\nAIENG_ACADVER=24.0\nAIENG_LAYERCOUNT=7\n"
            "AIENG_ENTITYCOUNT=42\nAIENG_DBMOD=0\nAIENG_DONE=1\n",
            "",
            False,
            False,
        )

    monkeypatch.setattr(manager, "_run_core_console", fake_run)

    result = manager.inventory_scratch(source, confirmed=True)

    assert result.layer_count == 7
    assert result.entity_count == 42
    assert result.database_modified == 0
    assert result.source_unchanged is True
    assert result.scratch_unchanged is True
    command = captured["command"]
    assert isinstance(command, tuple)
    assert command[0] == str(installation.core_console)
    assert "/readonly" in command
    script = Path(captured["cwd"]) / "inventory.scr"  # type: ignore[arg-type]
    script_text = script.read_text(encoding="utf-8")
    assert '(tblnext "LAYER" T)' in script_text
    assert '(ssget "_X")' in script_text
    assert "sslength" in script_text
    assert "save" not in script_text.casefold()
    assert "load" not in script_text.casefold()


def test_drawing_inventory_requires_confirmation_and_detects_scratch_mutation(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manager, workspace, _ = _manager(tmp_path)
    source = workspace / "inventory.dwg"
    source.write_bytes(b"DWG inventory content")

    with pytest.raises(ConfirmationRequiredError):
        manager.inventory_scratch(source)

    def mutating_run(command: tuple[str, ...], *, cwd: Path) -> _ProcessResult:
        (cwd / "source.dwg").write_bytes(b"unexpected mutation")
        return _ProcessResult(
            0,
            "AIENG_ACADVER=24.0\nAIENG_LAYERCOUNT=1\n"
            "AIENG_ENTITYCOUNT=0\nAIENG_DBMOD=0\nAIENG_DONE=1\n",
            "",
            False,
            False,
        )

    monkeypatch.setattr(manager, "_run_core_console", mutating_run)
    with pytest.raises(AutoCADIntegrationError, match="hash state"):
        manager.inventory_scratch(source, confirmed=True)

    source.write_bytes(b"DWG inventory content")

    def source_mutating_run(command: tuple[str, ...], *, cwd: Path) -> _ProcessResult:
        source.write_bytes(b"unexpected source mutation")
        return _ProcessResult(
            0,
            "AIENG_ACADVER=24.0\nAIENG_LAYERCOUNT=1\n"
            "AIENG_ENTITYCOUNT=0\nAIENG_DBMOD=0\nAIENG_DONE=1\n",
            "",
            False,
            False,
        )

    monkeypatch.setattr(manager, "_run_core_console", source_mutating_run)
    with pytest.raises(AutoCADIntegrationError, match="hash state"):
        manager.inventory_scratch(source, confirmed=True)


def test_scratch_mutation_reports_fixed_validation_markers(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manager, workspace, _ = _manager(tmp_path)
    source = workspace / "line-source.dwg"
    source.write_bytes(b"DWG line source")

    def rejected_run(command: tuple[str, ...], *, cwd: Path) -> _ProcessResult:
        return _ProcessResult(
            0,
            "AIENG_LAYERVALID=0\nAIENG_VALIDCOUNT=0\nAIENG_DONE=1\n",
            "",
            False,
            False,
        )

    monkeypatch.setattr(manager, "_run_core_console", rejected_run)
    with pytest.raises(
        AutoCADIntegrationError,
        match="fixed validation rejected: LAYERVALID=0, VALIDCOUNT=0",
    ):
        manager.create_line_scratch(
            source,
            start=(0.0, 0.0, 0.0),
            end=(1000.0, 0.0, 0.0),
            confirmed=True,
        )


def test_scratch_line_writer_changes_only_copy_and_validates_saved_result(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manager, workspace, installation = _manager(tmp_path)
    source = workspace / "line-source.dwg"
    original = b"DWG line source"
    source.write_bytes(original)
    captured: dict[str, object] = {}

    def fake_run(command: tuple[str, ...], *, cwd: Path) -> _ProcessResult:
        captured["command"] = command
        captured["cwd"] = cwd
        scratch = cwd / "source.dwg"
        scratch.write_bytes(scratch.read_bytes() + b" saved line")
        return _ProcessResult(
            0,
            "AIENG_ACADVER=24.0\nAIENG_BEFORECOUNT=2\n"
            "AIENG_AFTERCOUNT=3\nAIENG_CREATEDTYPE=LINE\n"
            "AIENG_DBMOD=0\nAIENG_DONE=1\n",
            "",
            False,
            False,
        )

    monkeypatch.setattr(manager, "_run_core_console", fake_run)
    result = manager.create_line_scratch(
        source,
        start=(0.0, 0.0, 0.0),
        end=(1000.0, 0.0, 0.0),
        confirmed=True,
    )

    assert source.read_bytes() == original
    assert result.source_unchanged is True
    assert result.scratch_changed is True
    assert result.created_type == "LINE"
    assert result.entity_count_after == result.entity_count_before + 1
    assert result.database_modified == 0
    command = captured["command"]
    assert isinstance(command, tuple)
    assert command[0] == str(installation.core_console)
    assert "/readonly" not in command
    assert str(source) not in command
    script = Path(captured["cwd"]) / "create-line.scr"  # type: ignore[arg-type]
    script_text = script.read_text(encoding="utf-8")
    assert '(command-s "_.LINE" (list 0 0 0) (list 1000 0 0) "")' in script_text
    assert '(command-s "_.QSAVE")' in script_text
    evidence = json.loads(
        (script.parent / "drawing-output.json").read_text(encoding="utf-8")
    )
    assert evidence == {
        "operation": "autocad.create_line_scratch",
        "output": str(result.output_drawing),
        "output_sha256": hashlib.sha256(original + b" saved line").hexdigest(),
        "output_size": len(original + b" saved line"),
        "run_id": result.run_id,
        "schema_version": 1,
        "source": str(source),
        "source_sha256": hashlib.sha256(original).hexdigest(),
        "suffix": ".dwg",
    }
    assert "saveas" not in script_text.casefold()


def test_scratch_line_writer_rejects_unsafe_points_and_requires_confirmation(
    tmp_path: Path,
) -> None:
    manager, workspace, _ = _manager(tmp_path)
    source = workspace / "line-source.dwg"
    source.write_bytes(b"DWG line source")

    with pytest.raises(ConfirmationRequiredError):
        manager.create_line_scratch(
            source,
            start=(0.0, 0.0, 0.0),
            end=(1.0, 0.0, 0.0),
        )
    with pytest.raises(AutoCADIntegrationError, match="different"):
        manager.create_line_scratch(
            source,
            start=(1.0, 2.0, 3.0),
            end=(1.0, 2.0, 3.0),
            confirmed=True,
        )
    with pytest.raises(AutoCADIntegrationError, match="different"):
        manager.create_line_scratch(
            source,
            start=(0.0, 0.0, 0.0),
            end=(0.0000000001, 0.0, 0.0),
            confirmed=True,
        )
    with pytest.raises(AutoCADIntegrationError, match="within bounds"):
        manager.create_line_scratch(
            source,
            start=(float("nan"), 0.0, 0.0),
            end=(1.0, 0.0, 0.0),
            confirmed=True,
        )


def test_wire_route_writer_creates_verified_electrical_segments_only_in_copy(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manager, workspace, installation = _manager(tmp_path)
    source = workspace / "wire-source.dwg"
    original = b"DWG wire source"
    source.write_bytes(original)
    captured: dict[str, object] = {}

    def fake_run(command: tuple[str, ...], *, cwd: Path) -> _ProcessResult:
        captured["command"] = command
        captured["cwd"] = cwd
        scratch = cwd / "source.dwg"
        scratch.write_bytes(scratch.read_bytes() + b" saved wire route")
        return _ProcessResult(
            0,
            "AIENG_ACADVER=24.0\nAIENG_LAYERNAME=E-WIRE\n"
            "AIENG_LAYERCOLOR=3\nAIENG_LINETYPE=CONTINUOUS\n"
            "AIENG_LAYERVALID=1\nAIENG_BEFORECOUNT=0\nAIENG_AFTERCOUNT=2\n"
            "AIENG_SEGMENTCOUNT=2\nAIENG_VALIDCOUNT=2\nAIENG_DBMOD=0\n"
            "AIENG_DONE=1\n",
            "",
            False,
            False,
        )

    monkeypatch.setattr(manager, "_run_core_console", fake_run)
    result = manager.create_wire_route_scratch(
        source,
        points=((0.0, 0.0, 0.0), (1000.0, 0.0, 0.0), (1000.0, 500.0, 0.0)),
        confirmed=True,
    )

    assert source.read_bytes() == original
    assert result.source_unchanged is True
    assert result.scratch_changed is True
    assert result.layer == "E-WIRE"
    assert result.layer_color == 3
    assert result.linetype == "CONTINUOUS"
    assert result.segment_count == 2
    assert result.entity_count_after == result.entity_count_before + 2
    assert result.database_modified == 0
    command = captured["command"]
    assert isinstance(command, tuple)
    assert command[0] == str(installation.core_console)
    assert "/readonly" not in command
    assert str(source) not in command
    script = Path(captured["cwd"]) / "create-wire-route.scr"  # type: ignore[arg-type]
    script_text = script.read_text(encoding="utf-8")
    assert '(0 . "LAYER")' in script_text
    assert '(2 . "E-WIRE")' in script_text
    assert "'(62 . 3)" in script_text
    assert "'(6 . \"CONTINUOUS\")" in script_text
    assert script_text.count('(command-s "_.LINE"') == 2
    assert '(command-s "_.QSAVE")' in script_text


def test_wire_route_writer_rejects_unsafe_route_and_requires_confirmation(
    tmp_path: Path,
) -> None:
    manager, workspace, _ = _manager(tmp_path)
    source = workspace / "wire-source.dwg"
    source.write_bytes(b"DWG wire source")
    route = ((0.0, 0.0, 0.0), (1.0, 0.0, 0.0))

    with pytest.raises(ConfirmationRequiredError):
        manager.create_wire_route_scratch(source, points=route)
    with pytest.raises(AutoCADIntegrationError, match="identical adjacent"):
        manager.create_wire_route_scratch(
            source,
            points=((0.0, 0.0, 0.0), (0.0000000001, 0.0, 0.0)),
            confirmed=True,
        )
    with pytest.raises(AutoCADIntegrationError, match="2 through 16"):
        manager.create_wire_route_scratch(
            source,
            points=tuple((float(index), 0.0, 0.0) for index in range(17)),
            confirmed=True,
        )


def test_symbol_catalog_listing_and_scratch_placement_are_verified(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manager, workspace, installation = _manager(tmp_path)
    source = workspace / "symbol-source.dwg"
    original = b"DWG symbol source"
    source.write_bytes(original)
    captured: dict[str, object] = {}

    listed = manager.list_electrical_symbols()
    assert listed["catalog_version"] == 1
    assert listed["symbols"][0]["id"] == "socket_single"  # type: ignore[index]

    def fake_run(command: tuple[str, ...], *, cwd: Path) -> _ProcessResult:
        captured["command"] = command
        captured["cwd"] = cwd
        scratch = cwd / "source.dwg"
        scratch.write_bytes(scratch.read_bytes() + b" saved electrical symbol")
        return _ProcessResult(
            0,
            "AIENG_ACADVER=24.0\nAIENG_INSUNITS=4\nAIENG_UNITSVALID=1\n"
            "AIENG_SYMBOLID=socket_single\n"
            "AIENG_BLOCKNAME=AIENG_E_SOCKET_SINGLE_V1\n"
            f"AIENG_DEFINITIONHASH={SYMBOL_HASH}\n"
            "AIENG_LAYERNAME=E-POWR-DEVC\nAIENG_LAYERCOLOR=2\n"
            "AIENG_LINETYPE=CONTINUOUS\nAIENG_LAYERVALID=1\n"
            "AIENG_BLOCKNEW=0\nAIENG_BLOCKDEFINED=1\nAIENG_BLOCKVALID=1\n"
            "AIENG_BLOCKHEADERVALID=1\nAIENG_BLOCKENDVALID=1\n"
            "AIENG_VALIDATEDGEOMETRYCOUNT=3\n"
            "AIENG_GEOMETRYCOUNT=3\nAIENG_INSERTVALID=1\n"
            "AIENG_ATTRIBUTECOUNT=0\nAIENG_BEFORECOUNT=0\n"
            "AIENG_AFTERCOUNT=1\nAIENG_DBMOD=0\nAIENG_DONE=1\n",
            "",
            False,
            False,
        )

    monkeypatch.setattr(manager, "_run_core_console", fake_run)
    result = manager.place_electrical_symbol_scratch(
        source,
        symbol_id="socket_single",
        definition_sha256=SYMBOL_HASH,
        insertion_point=(250.0, 500.0, 0.0),
        rotation_degrees=90.0,
        scale=1.5,
        confirmed=True,
    )

    assert source.read_bytes() == original
    assert result.block_name == "AIENG_E_SOCKET_SINGLE_V1"
    assert result.insertion_layer == "E-POWR-DEVC"
    assert result.definition_sha256 == SYMBOL_HASH
    assert result.insertion_point == (250.0, 500.0, 0.0)
    assert result.rotation_degrees == 90.0
    assert result.scale == 1.5
    assert result.block_reused is True
    assert result.attribute_count == 0
    assert result.entity_count_after == result.entity_count_before + 1
    assert result.database_modified == 0
    assert result.source_unchanged is True
    assert result.scratch_changed is True
    command = captured["command"]
    assert isinstance(command, tuple)
    assert command[0] == str(installation.core_console)
    assert "/readonly" not in command
    assert str(source) not in command
    script = Path(captured["cwd"]) / "place-electrical-symbol.scr"  # type: ignore[arg-type]
    script_text = script.read_text(encoding="utf-8")
    assert '(2 . "AIENG_E_SOCKET_SINGLE_V1")' in script_text
    assert '(8 . "E-POWR-DEVC")' in script_text
    assert script_text.count("'(0 . \"LINE\")") == 2
    assert script_text.count("'(0 . \"CIRCLE\")") == 1
    assert script_text.count("'(0 . \"INSERT\")") == 1
    assert '(command-s "_.QSAVE")' in script_text


def test_symbol_placement_requires_confirmation_and_exact_catalog_hash(
    tmp_path: Path,
) -> None:
    manager, workspace, _ = _manager(tmp_path)
    source = workspace / "symbol-source.dwg"
    source.write_bytes(b"DWG symbol source")

    with pytest.raises(ConfirmationRequiredError):
        manager.place_electrical_symbol_scratch(
            source,
            symbol_id="socket_single",
            definition_sha256=SYMBOL_HASH,
            insertion_point=(0.0, 0.0, 0.0),
        )
    with pytest.raises(AutoCADIntegrationError, match="approval hash"):
        manager.place_electrical_symbol_scratch(
            source,
            symbol_id="socket_single",
            definition_sha256="0" * 64,
            insertion_point=(0.0, 0.0, 0.0),
            confirmed=True,
        )
    with pytest.raises(AutoCADIntegrationError, match="rotation"):
        manager.place_electrical_symbol_scratch(
            source,
            symbol_id="socket_single",
            definition_sha256=SYMBOL_HASH,
            insertion_point=(0.0, 0.0, 0.0),
            rotation_degrees=float("nan"),
            confirmed=True,
        )
    with pytest.raises(AutoCADIntegrationError, match="scale"):
        manager.place_electrical_symbol_scratch(
            source,
            symbol_id="socket_single",
            definition_sha256=SYMBOL_HASH,
            insertion_point=(0.0, 0.0, 0.0),
            scale=0.0,
            confirmed=True,
        )


def _circuit_output(*, block_new: int = 0) -> str:
    return (
        "AIENG_ACADVER=24.0\nAIENG_INSUNITS=4\nAIENG_UNITSVALID=1\n"
        "AIENG_SYMBOLID=socket_single\n"
        "AIENG_BLOCKNAME=AIENG_E_SOCKET_SINGLE_V1\n"
        f"AIENG_DEFINITIONHASH={SYMBOL_HASH}\n"
        "AIENG_INSERTLAYERNAME=E-POWR-DEVC\nAIENG_INSERTLAYERCOLOR=2\n"
        "AIENG_INSERTLINETYPE=CONTINUOUS\nAIENG_INSERTLAYERVALID=1\n"
        "AIENG_WIRELAYERNAME=E-WIRE\nAIENG_WIRELAYERCOLOR=3\n"
        "AIENG_WIRELINETYPE=CONTINUOUS\nAIENG_WIRELAYERVALID=1\n"
        f"AIENG_BLOCKNEW={block_new}\nAIENG_BLOCKDEFINED=1\nAIENG_BLOCKVALID=1\n"
        "AIENG_BLOCKHEADERVALID=1\nAIENG_BLOCKENDVALID=1\n"
        "AIENG_VALIDATEDGEOMETRYCOUNT=3\n"
        "AIENG_REFERENCECOUNT=3\nAIENG_VALIDREFERENCECOUNT=3\n"
        "AIENG_SEGMENTCOUNT=2\nAIENG_VALIDSEGMENTCOUNT=2\n"
        "AIENG_BEFORECOUNT=5\nAIENG_AFTERCOUNT=10\nAIENG_DBMOD=0\n"
        "AIENG_DONE=1\n"
    )


def test_circuit_fragment_reuses_verified_block_and_saves_exact_entities(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manager, workspace, installation = _manager(tmp_path)
    source = workspace / "existing-symbol.dwg"
    original = b"DWG with an existing approved symbol"
    saved_suffix = b" saved verified circuit"
    source.write_bytes(original)
    captured: dict[str, object] = {}

    def fake_run(command: tuple[str, ...], *, cwd: Path) -> _ProcessResult:
        captured["command"] = command
        captured["cwd"] = cwd
        scratch = cwd / "source.dwg"
        scratch.write_bytes(scratch.read_bytes() + saved_suffix)
        return _ProcessResult(0, _circuit_output(), "", False, False)

    monkeypatch.setattr(manager, "_run_core_console", fake_run)
    placements = (
        CircuitPlacement((0.0, 0.0, 0.0)),
        CircuitPlacement((1000.0, 0.0, 0.0), rotation_degrees=90.0),
        CircuitPlacement((2000.0, 0.0, 0.0), scale=1.5),
    )
    result = manager.create_electrical_circuit_fragment_scratch(
        source,
        symbol_id="socket_single",
        definition_sha256=SYMBOL_HASH,
        placements=placements,
        wire_points=(
            (0.0, 0.0, 0.0),
            (1000.0, 0.0, 0.0),
            (2000.0, 0.0, 0.0),
        ),
        expected_source_sha256=hashlib.sha256(original).hexdigest(),
        confirmed=True,
    )

    assert source.read_bytes() == original
    assert result.source_sha256 == hashlib.sha256(original).hexdigest()
    assert result.output_sha256 == hashlib.sha256(original + saved_suffix).hexdigest()
    assert result.block_reused is True
    assert result.reference_count == 3
    assert result.segment_count == 2
    assert result.entity_count_after == result.entity_count_before + 5
    assert result.source_unchanged is True
    assert result.scratch_changed is True
    assert result.database_modified == 0
    command = captured["command"]
    assert isinstance(command, tuple)
    assert command[0] == str(installation.core_console)
    assert "/readonly" not in command
    run_directory = Path(captured["cwd"])  # type: ignore[arg-type]
    script = run_directory / "create-electrical-circuit-fragment.scr"
    script_text = script.read_text(encoding="utf-8")
    assert script_text.count("'(0 . \"INSERT\")") == 3
    assert script_text.count("'(0 . \"LINE\")") == 4
    assert '(= aieng-block-valid 1)' in script_text
    assert '(command-s "_.QSAVE")' in script_text

    recovery = manager.record_drafting_recovery_evidence(
        result,
        drafting_run_id="1" * 32,
        execution_id="2" * 32,
        execution_request_sha256="3" * 64,
        preview_sha256="4" * 64,
    )
    evidence = json.loads(recovery.path.read_text(encoding="utf-8"))
    assert recovery.evidence_sha256 == hashlib.sha256(
        recovery.path.read_bytes()
    ).hexdigest()
    assert evidence["source_unchanged"] is True
    assert evidence["scratch_changed"] is True
    assert evidence["handoff_requires_separate_approval"] is True
    assert evidence["source_sha256"] == result.source_sha256
    assert evidence["output_sha256"] == result.output_sha256


def test_circuit_fragment_enforces_hash_bounds_and_confirmation(tmp_path: Path) -> None:
    manager, workspace, _ = _manager(tmp_path)
    source = workspace / "circuit-source.dwg"
    source.write_bytes(b"DWG circuit source")
    placements = (
        CircuitPlacement((0.0, 0.0, 0.0)),
        CircuitPlacement((1000.0, 0.0, 0.0)),
    )
    route = ((0.0, 0.0, 0.0), (1000.0, 0.0, 0.0))

    with pytest.raises(AutoCADIntegrationError, match="reviewed SHA-256"):
        manager.create_electrical_circuit_fragment_scratch(
            source,
            symbol_id="socket_single",
            definition_sha256=SYMBOL_HASH,
            placements=placements,
            wire_points=route,
            expected_source_sha256="f" * 64,
            confirmed=True,
        )
    with pytest.raises(ConfirmationRequiredError):
        manager.create_electrical_circuit_fragment_scratch(
            source,
            symbol_id="socket_single",
            definition_sha256=SYMBOL_HASH,
            placements=placements,
            wire_points=route,
        )
    with pytest.raises(AutoCADIntegrationError, match="approval hash"):
        manager.create_electrical_circuit_fragment_scratch(
            source,
            symbol_id="socket_single",
            definition_sha256="0" * 64,
            placements=placements,
            wire_points=route,
            confirmed=True,
        )
    with pytest.raises(AutoCADIntegrationError, match="2 through 8"):
        manager.create_electrical_circuit_fragment_scratch(
            source,
            symbol_id="socket_single",
            definition_sha256=SYMBOL_HASH,
            placements=placements[:1],
            wire_points=route,
            confirmed=True,
        )
    with pytest.raises(AutoCADIntegrationError, match="2 through 16"):
        manager.create_electrical_circuit_fragment_scratch(
            source,
            symbol_id="socket_single",
            definition_sha256=SYMBOL_HASH,
            placements=placements,
            wire_points=tuple((float(index), 0.0, 0.0) for index in range(17)),
            confirmed=True,
        )


def _panel_sld_fixture_output() -> str:
    return (
        "AIENG_ACADVER=24.0\nAIENG_INSUNITS=4\nAIENG_UNITSVALID=1\nAIENG_LAYERVALID=1\n"
        "AIENG_MARKERPROFILE=AIENG_M21_FIXED_V6\nAIENG_MARKERBLOCKCOUNT=4\n"
        "AIENG_MARKERREFERENCECOUNT=10\nAIENG_RECOGNIZEDLOADREFERENCECOUNT=1\n"
        "AIENG_LOADHANDLEVALID=1\nAIENG_ATTRIBUTECOUNT=58\n"
        "AIENG_WAY1ATTRIBUTECOUNT=15\nAIENG_WAY2ATTRIBUTECOUNT=13\n"
        "AIENG_LOADBINDINGPRESENT=1\nAIENG_LOADNAMEPRESENT=1\nAIENG_LOADLINKVALID=1\n"
        "AIENG_CONTENTVALID=1\n"
        "AIENG_BEFORECOUNT=3\nAIENG_AFTERCOUNT=14\nAIENG_DBMOD=0\nAIENG_DONE=1\n"
    )


def test_panel_sld_fixture_is_fixed_hash_bound_and_scratch_only(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manager, workspace, _ = _manager(tmp_path)
    source = workspace / "m21-source.dwt"
    original = b"disposable millimeter fixture source"
    suffix = b" fixed M21 fixture"
    source.write_bytes(original)
    captured: dict[str, Path] = {}

    def fake_run(command: tuple[str, ...], *, cwd: Path) -> _ProcessResult:
        captured["cwd"] = cwd
        scratch = cwd / "source.dwg"
        scratch.write_bytes(scratch.read_bytes() + suffix)
        return _ProcessResult(0, _panel_sld_fixture_output(), "", False, False)

    monkeypatch.setattr(manager, "_run_core_console", fake_run)
    result = manager.create_panel_sld_fixture_scratch(
        source,
        expected_source_sha256=hashlib.sha256(original).hexdigest(),
        confirmed=True,
    )

    assert source.read_bytes() == original
    assert result.source_unchanged is True
    assert result.scratch_changed is True
    assert result.output_drawing.suffix == ".dwg"
    assert result.marker_reference_count == 10
    assert result.attribute_count == 58
    assert result.entity_count_after == result.entity_count_before + 11
    script = (captured["cwd"] / "create-panel-sld-fixture.scr").read_text(encoding="utf-8")
    assert script.count('(0 . "ATTDEF")') == 25
    assert script.count('(0 . "ATTRIB")') == 58
    assert "LOAD_BINDINGS" in script
    assert "LOAD_REFS" not in script
    assert '(ssget "_X"' in script
    assert '(sslength aieng-load-selection) 1' in script
    assert "vlax-ename->vla-object" not in script
    assert '(setq aieng-load-binding' in script
    assert "AIENG_WAY1ATTRIBUTECOUNT=" in script
    first_way_insert = script.index("'(2 . \"AIENG_M21_WAY_V1\")")
    first_way_reference = script.index("(setq aieng-way-one", first_way_insert)
    assert script.rfind('(entmake \'((0 . "SEQEND")))', first_way_insert, first_way_reference) > first_way_insert
    assert "LOAD_HANDLES" not in script
    assert "LOAD_SOURCE_OBJECT_IDS" not in script
    assert "AIENG_CONTENTVALID=" in script
    assert script.index("(setq aieng-layer-valid") < script.index("(setq aieng-block-new")
    assert '(command-s "_.QSAVE")' in script

    with pytest.raises(AutoCADIntegrationError, match="reviewed SHA-256"):
        manager.create_panel_sld_fixture_scratch(
            source, expected_source_sha256="f" * 64, confirmed=True
        )
    with pytest.raises(ConfirmationRequiredError):
        manager.create_panel_sld_fixture_scratch(
            source, expected_source_sha256=hashlib.sha256(original).hexdigest()
        )


def test_panel_sld_fixture_parser_fails_closed_on_profile_mismatch() -> None:
    assert _parse_panel_sld_fixture_markers(
        _panel_sld_fixture_output(), maximum_count=100
    )["LOADLINKVALID"] == "1"
    with pytest.raises(AutoCADIntegrationError, match="fixed marker profile"):
        _parse_panel_sld_fixture_markers(
            _panel_sld_fixture_output().replace("ATTRIBUTECOUNT=58", "ATTRIBUTECOUNT=57"),
            maximum_count=100,
        )


def _maximum_performance_fixture_output() -> str:
    return (
        "AIENG_ACADVER=24.0\nAIENG_INSUNITS=4\nAIENG_UNITSVALID=1\n"
        "AIENG_FIXTUREPROFILE=AIENG_MAX_EXECUTION_V1\n"
        "AIENG_LAYERNAME=AIENG-PERF-FIXTURE\nAIENG_LAYERCOLOR=8\n"
        "AIENG_LAYERPREEXISTING=0\nAIENG_LAYERVALID=1\n"
        "AIENG_OBJECTCOUNT=1792\nAIENG_VALIDCOUNT=1792\n"
        "AIENG_LAYEROBJECTCOUNT=1792\nAIENG_BEFORECOUNT=3\n"
        "AIENG_AFTERCOUNT=1795\nAIENG_DBMOD=0\nAIENG_DONE=1\n"
    )


def test_maximum_performance_fixture_is_fixed_recoverable_and_not_handoffable(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manager, workspace, _ = _manager(tmp_path)
    source = workspace / "performance-source.dwg"
    original = b"disposable millimeter performance source"
    suffix = b" fixed 1792-object fixture"
    source.write_bytes(original)
    captured: dict[str, Path] = {}

    def fake_run(command: tuple[str, ...], *, cwd: Path) -> _ProcessResult:
        captured["cwd"] = cwd
        scratch = cwd / "source.dwg"
        scratch.write_bytes(scratch.read_bytes() + suffix)
        return _ProcessResult(
            0, _maximum_performance_fixture_output(), "", False, False
        )

    monkeypatch.setattr(manager, "_run_core_console", fake_run)
    result = manager.create_maximum_performance_fixture_scratch(
        source,
        expected_source_sha256=hashlib.sha256(original).hexdigest(),
        confirmed=True,
    )

    assert source.read_bytes() == original
    assert result.object_count == 1792
    assert result.entity_count_after == result.entity_count_before + 1792
    assert result.source_unchanged is True
    assert result.scratch_changed is True
    assert result.database_modified == 0
    assert result.total_duration_seconds >= result.execution_duration_seconds
    script = (captured["cwd"] / "create-maximum-performance-fixture.scr").read_text(
        encoding="utf-8"
    )
    assert "(repeat 1792" in script
    assert "AIENG-PERF-FIXTURE" in script
    assert '(command-s "_.QSAVE")' in script
    evidence = json.loads(result.recovery_evidence.read_text(encoding="utf-8"))
    assert evidence["object_count"] == 1792
    assert evidence["handoff_allowed"] is False
    assert evidence["source_unchanged"] is True
    assert evidence["output_sha256"] == result.output_sha256
    assert result.recovery_evidence_sha256 == hashlib.sha256(
        result.recovery_evidence.read_bytes()
    ).hexdigest()
    assert not (captured["cwd"] / "drawing-output.json").exists()

    with pytest.raises(ConfirmationRequiredError):
        manager.create_maximum_performance_fixture_scratch(
            source, expected_source_sha256=hashlib.sha256(original).hexdigest()
        )
    with pytest.raises(AutoCADIntegrationError, match="reviewed SHA-256"):
        manager.create_maximum_performance_fixture_scratch(
            source, expected_source_sha256="f" * 64, confirmed=True
        )


def test_maximum_performance_fixture_parser_fails_closed() -> None:
    parsed = _parse_maximum_performance_fixture_markers(
        _maximum_performance_fixture_output(), maximum_count=10_000
    )
    assert parsed["OBJECTCOUNT"] == "1792"
    with pytest.raises(AutoCADIntegrationError, match="fixed profile"):
        _parse_maximum_performance_fixture_markers(
            _maximum_performance_fixture_output().replace(
                "LAYEROBJECTCOUNT=1792", "LAYEROBJECTCOUNT=1791"
            ),
            maximum_count=10_000,
        )


def test_user_symbol_source_review_proposes_hash_approved_catalog_entry(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manager, workspace, installation = _manager(tmp_path)
    source = workspace / "user-symbol.dwg"
    original = b"DWG reviewed user symbol"
    source.write_bytes(original)
    captured: dict[str, object] = {}

    def fake_run(command: tuple[str, ...], *, cwd: Path) -> _ProcessResult:
        captured["command"] = command
        captured["cwd"] = cwd
        return _ProcessResult(
            0,
            "AIENG_ENTITY0001=CIRCLE|0|0|0|125|1\n"
            "AIENG_ENTITY0002=LINE|-40|-45|0|-40|45|0|1\n"
            "AIENG_ENTITY0003=LINE|40|-45|0|40|45|0|1\n"
            "AIENG_ACADVER=24.0\nAIENG_INSUNITS=4\n"
            "AIENG_BLOCKNAME=AIENG_E_SOCKET_CUSTOM_V1\nAIENG_BLOCKFOUND=1\n"
            "AIENG_BLOCKFLAGS=0\nAIENG_BASEPOINT=0|0|0\n"
            "AIENG_ENTITYCOUNT=3\nAIENG_LINECOUNT=2\nAIENG_CIRCLECOUNT=1\n"
            "AIENG_UNSUPPORTEDCOUNT=0\nAIENG_LAYERZEROCOUNT=3\n"
            "AIENG_ATTDEFCOUNT=0\nAIENG_NESTEDINSERTCOUNT=0\n"
            "AIENG_TRUNCATED=0\nAIENG_DBMOD=0\nAIENG_DONE=1\n",
            "",
            False,
            False,
        )

    monkeypatch.setattr(manager, "_run_core_console", fake_run)
    result = manager.review_electrical_symbol_source(
        source,
        block_name="AIENG_E_SOCKET_CUSTOM_V1",
        symbol_id="socket_custom",
        symbol_version=1,
        insertion_layer="E-POWR-DEVC",
        layer_color=2,
        confirmed=True,
    )

    assert source.read_bytes() == original
    assert result.eligible is True
    assert result.reasons == ()
    assert result.entity_count == 3
    assert result.line_count == 2
    assert result.circle_count == 1
    assert result.source_unchanged is True
    assert result.scratch_unchanged is True
    assert len(result.source_sha256) == 64
    assert result.proposed_definition is not None
    assert result.proposed_definition["id"] == "socket_custom"
    assert result.proposed_definition["block_name"] == "AIENG_E_SOCKET_CUSTOM_V1"
    assert len(str(result.proposed_definition["definition_sha256"])) == 64
    evidence = result.scratch_drawing.parent / "symbol-review.json"
    assert json.loads(evidence.read_text(encoding="utf-8"))["review_run_id"] == result.run_id
    proposed_catalog = tmp_path / "proposed-catalog.json"
    proposed_catalog.write_text(
        json.dumps(
            {
                "schema_version": 1,
                "catalog_version": 1,
                "symbols": [result.proposed_definition],
            }
        ),
        encoding="utf-8",
    )
    assert (
        ElectricalSymbolCatalog.load(proposed_catalog).get("socket_custom").block_name
        == "AIENG_E_SOCKET_CUSTOM_V1"
    )
    command = captured["command"]
    assert isinstance(command, tuple)
    assert command[0] == str(installation.core_console)
    assert "/readonly" in command
    assert str(source) not in command
    script = Path(captured["cwd"]) / "review-electrical-symbol.scr"  # type: ignore[arg-type]
    script_text = script.read_text(encoding="utf-8")
    assert '(tblsearch "BLOCK" "AIENG_E_SOCKET_CUSTOM_V1")' in script_text
    assert "(entnext" in script_text
    assert "(< aieng-count 32)" in script_text
    assert "QSAVE" not in script_text


def test_user_symbol_source_review_reports_ineligible_units_and_geometry(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manager, workspace, _ = _manager(tmp_path)
    source = workspace / "user-symbol.dwg"
    source.write_bytes(b"DWG reviewed user symbol")

    monkeypatch.setattr(
        manager,
        "_run_core_console",
        lambda command, cwd: _ProcessResult(
            0,
            "AIENG_ENTITY0001=OTHER|ATTDEF|0\n"
            "AIENG_ACADVER=24.0\nAIENG_INSUNITS=1\n"
            "AIENG_BLOCKNAME=USER_BLOCK\nAIENG_BLOCKFOUND=1\n"
            "AIENG_BLOCKFLAGS=0\nAIENG_BASEPOINT=0|0|0\n"
            "AIENG_ENTITYCOUNT=1\nAIENG_LINECOUNT=0\nAIENG_CIRCLECOUNT=0\n"
            "AIENG_UNSUPPORTEDCOUNT=1\nAIENG_LAYERZEROCOUNT=0\n"
            "AIENG_ATTDEFCOUNT=1\nAIENG_NESTEDINSERTCOUNT=0\n"
            "AIENG_TRUNCATED=0\nAIENG_DBMOD=0\nAIENG_DONE=1\n",
            "",
            False,
            False,
        ),
    )
    result = manager.review_electrical_symbol_source(
        source,
        block_name="USER_BLOCK",
        symbol_id="user_block",
        symbol_version=1,
        insertion_layer="E-POWR-DEVC",
        layer_color=2,
        confirmed=True,
    )

    assert result.eligible is False
    assert result.proposed_definition is None
    assert "block_name_requires_catalog_rename" in result.reasons
    assert "drawing_units_must_be_millimeters" in result.reasons
    assert "unsupported_geometry_present" in result.reasons
    assert "internal_entities_must_use_layer_0" in result.reasons
    assert "attributes_not_supported" in result.reasons


def test_user_symbol_source_review_requires_confirmation(tmp_path: Path) -> None:
    manager, workspace, _ = _manager(tmp_path)
    source = workspace / "user-symbol.dwg"
    source.write_bytes(b"DWG reviewed user symbol")

    with pytest.raises(ConfirmationRequiredError):
        manager.review_electrical_symbol_source(
            source,
            block_name="AIENG_E_SOCKET_CUSTOM_V1",
            symbol_id="socket_custom",
            symbol_version=1,
            insertion_layer="E-POWR-DEVC",
            layer_color=2,
        )


def test_review_registration_binds_evidence_source_and_proposal_hashes(
    tmp_path: Path,
) -> None:
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    files = FileManager([workspace], tmp_path / "file-backup")
    installation = _installation(tmp_path / "installed-registration")
    catalog_path = tmp_path / "catalog.json"
    catalog_path.write_bytes(SYMBOL_CATALOG.read_bytes())
    manager = AutoCADManager(
        files,
        locator=lambda: installation,
        symbol_catalog_path=catalog_path,
        symbol_catalog_backup_dir=tmp_path / "catalog-backup",
    )
    source = workspace / "reviewed-source.dwg"
    source.write_bytes(b"reviewed drawing bytes")
    source_hash = hashlib.sha256(source.read_bytes()).hexdigest()
    proposal = json.loads(SYMBOL_CATALOG.read_text(encoding="utf-8"))["symbols"][0]
    proposal.update(
        {
            "id": "socket_registered",
            "block_name": "AIENG_E_SOCKET_REGISTERED_V1",
            "description": "Reviewed user-authored electrical symbol socket_registered.",
        }
    )
    proposal.pop("definition_sha256")
    proposal_hash = calculate_definition_sha256(proposal)
    proposal["definition_sha256"] = proposal_hash
    review_run_id = "a" * 32
    evidence_directory = (
        workspace / ".ai-engineer" / "autocad" / "runs" / review_run_id
    )
    evidence_directory.mkdir(parents=True)
    (evidence_directory / "symbol-review.json").write_text(
        json.dumps(
            {
                "schema_version": 1,
                "review_run_id": review_run_id,
                "source": str(source.resolve()),
                "source_sha256": source_hash,
                "eligible": True,
                "proposed_definition": proposal,
            }
        ),
        encoding="utf-8",
    )

    with pytest.raises(AutoCADIntegrationError, match="proposal hash"):
        manager.register_electrical_symbol_review(
            review_run_id=review_run_id,
            proposal_sha256="0" * 64,
            source_sha256=source_hash,
            confirmed=True,
        )
    source.write_bytes(b"changed after review")
    with pytest.raises(AutoCADIntegrationError, match="changed after review"):
        manager.register_electrical_symbol_review(
            review_run_id=review_run_id,
            proposal_sha256=proposal_hash,
            source_sha256=source_hash,
            confirmed=True,
        )
    source.write_bytes(b"reviewed drawing bytes")
    result = manager.register_electrical_symbol_review(
        review_run_id=review_run_id,
        proposal_sha256=proposal_hash,
        source_sha256=source_hash,
        confirmed=True,
    )

    assert result["review_run_id"] == review_run_id
    assert result["proposal_sha256"] == proposal_hash
    assert result["source_sha256"] == source_hash
    assert result["catalog_version"] == 2
    assert result["symbol"]["id"] == "socket_registered"  # type: ignore[index]
    assert ElectricalSymbolCatalog.load(catalog_path).get("socket_registered")
    assert len(list((tmp_path / "catalog-backup").glob("catalog-*.json"))) == 1


def test_scratch_inspection_requires_confirmation_and_safe_source(tmp_path: Path) -> None:
    manager, workspace, _ = _manager(tmp_path)
    drawing = workspace / "sample.dwg"
    drawing.write_bytes(b"drawing")

    with pytest.raises(ConfirmationRequiredError):
        manager.inspect_scratch(drawing)
    with pytest.raises(PathPolicyError):
        manager.inspect_scratch(tmp_path / "outside.dwg", confirmed=True)
    text = workspace / "sample.txt"
    text.write_text("not a drawing", encoding="utf-8")
    with pytest.raises(AutoCADIntegrationError, match=".dwg or .dwt"):
        manager.inspect_scratch(text, confirmed=True)


def test_scratch_size_and_core_console_failure_are_fail_closed(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manager, workspace, installation = _manager(tmp_path)
    files = FileManager([workspace], tmp_path / "backup-limited")
    limited = AutoCADManager(
        files,
        locator=lambda: installation,
        maximum_drawing_bytes=4,
    )
    large = workspace / "large.dwg"
    large.write_bytes(b"12345")
    with pytest.raises(AutoCADIntegrationError, match="byte limit"):
        limited.inspect_scratch(large, confirmed=True)

    source = workspace / "failed.dwg"
    source.write_bytes(b"drawing")
    monkeypatch.setattr(
        manager,
        "_run_core_console",
        lambda command, cwd: _ProcessResult(7, "private output", "private", False, False),
    )
    with pytest.raises(AutoCADIntegrationError, match="exit code 7") as raised:
        manager.inspect_scratch(source, confirmed=True)
    assert "private" not in str(raised.value)
    assert list((workspace / ".ai-engineer" / "autocad" / "runs").iterdir())


@pytest.mark.parametrize(
    "output",
    [
        "",
        "AIENG_DONE=1\n",
        "AIENG_ACADVER=bad!\nAIENG_INSUNITS=4\nAIENG_MEASUREMENT=1\n"
        "AIENG_TILEMODE=1\nAIENG_DBMOD=0\nAIENG_DONE=1\n",
        "AIENG_ACADVER=24.0\nAIENG_INSUNITS=x\nAIENG_MEASUREMENT=1\n"
        "AIENG_TILEMODE=1\nAIENG_DBMOD=0\nAIENG_DONE=1\n",
        "AIENG_ACADVER=24.0\nAIENG_ACADVER=24.0\nAIENG_INSUNITS=4\n"
        "AIENG_MEASUREMENT=1\nAIENG_TILEMODE=1\nAIENG_DBMOD=0\nAIENG_DONE=1\n",
    ],
)
def test_marker_parser_rejects_incomplete_or_malformed_output(output: str) -> None:
    with pytest.raises(AutoCADIntegrationError):
        _parse_markers(output)


def test_marker_parser_ignores_core_console_command_echoes() -> None:
    output = (
        'Command: (princ (strcat "\\nAIENG_ACADVER=" (getvar "ACADVER")))\r\n'
        "AIENG_ACADVER=24.0\r\n"
        '"\\nAIENG_ACADVER=24.0"\r\n'
        "AIENG_INSUNITS=1\r\n"
        "AIENG_MEASUREMENT=0\r\n"
        "AIENG_TILEMODE=1\r\n"
        "AIENG_DBMOD=0\r\n"
        "AIENG_DONE=1\r\n"
        '"\\nAIENG_DONE=1\\n"\r\n'
    )

    assert _parse_markers(output) == {
        "ACADVER": "24.0",
        "INSUNITS": "1",
        "MEASUREMENT": "0",
        "TILEMODE": "1",
        "DBMOD": "0",
        "DONE": "1",
    }


@pytest.mark.parametrize(
    ("output", "maximum_count"),
    [
        ("", 100),
        (
            "AIENG_ACADVER=24.0\nAIENG_LAYERCOUNT=x\n"
            "AIENG_ENTITYCOUNT=0\nAIENG_DBMOD=0\nAIENG_DONE=1\n",
            100,
        ),
        (
            "AIENG_ACADVER=24.0\nAIENG_LAYERCOUNT=101\n"
            "AIENG_ENTITYCOUNT=0\nAIENG_DBMOD=0\nAIENG_DONE=1\n",
            100,
        ),
        (
            "AIENG_ACADVER=24.0\nAIENG_LAYERCOUNT=1\n"
            "AIENG_ENTITYCOUNT=0\nAIENG_DBMOD=1\nAIENG_DONE=1\n",
            100,
        ),
    ],
)
def test_inventory_parser_rejects_invalid_counts_and_database_changes(
    output: str, maximum_count: int
) -> None:
    with pytest.raises(AutoCADIntegrationError):
        _parse_inventory_markers(output, maximum_count=maximum_count)


@pytest.mark.parametrize(
    "output",
    [
        "",
        "AIENG_ACADVER=24.0\nAIENG_BEFORECOUNT=2\nAIENG_AFTERCOUNT=3\n"
        "AIENG_CREATEDTYPE=CIRCLE\nAIENG_DBMOD=0\nAIENG_DONE=1\n",
        "AIENG_ACADVER=24.0\nAIENG_BEFORECOUNT=2\nAIENG_AFTERCOUNT=3\n"
        "AIENG_CREATEDTYPE=LINE\nAIENG_DBMOD=1\nAIENG_DONE=1\n",
        "AIENG_ACADVER=24.0\nAIENG_BEFORECOUNT=2\nAIENG_AFTERCOUNT=101\n"
        "AIENG_CREATEDTYPE=LINE\nAIENG_DBMOD=0\nAIENG_DONE=1\n",
    ],
)
def test_line_parser_rejects_invalid_or_unsaved_results(output: str) -> None:
    with pytest.raises(AutoCADIntegrationError):
        _parse_line_markers(output, maximum_count=100)


@pytest.mark.parametrize(
    "output",
    [
        "",
        "AIENG_ACADVER=24.0\nAIENG_LAYERNAME=E-POWER\nAIENG_LAYERCOLOR=3\n"
        "AIENG_LINETYPE=CONTINUOUS\nAIENG_LAYERVALID=1\nAIENG_BEFORECOUNT=0\n"
        "AIENG_AFTERCOUNT=2\nAIENG_SEGMENTCOUNT=2\nAIENG_VALIDCOUNT=2\n"
        "AIENG_DBMOD=0\nAIENG_DONE=1\n",
        "AIENG_ACADVER=24.0\nAIENG_LAYERNAME=E-WIRE\nAIENG_LAYERCOLOR=3\n"
        "AIENG_LINETYPE=CONTINUOUS\nAIENG_LAYERVALID=1\nAIENG_BEFORECOUNT=0\n"
        "AIENG_AFTERCOUNT=2\nAIENG_SEGMENTCOUNT=2\nAIENG_VALIDCOUNT=2\n"
        "AIENG_DBMOD=1\nAIENG_DONE=1\n",
    ],
)
def test_wire_route_parser_rejects_invalid_schema_or_unsaved_result(
    output: str,
) -> None:
    with pytest.raises(AutoCADIntegrationError):
        _parse_wire_route_markers(output, maximum_count=100)


@pytest.mark.parametrize(
    "replacement",
    [
        "AIENG_BLOCKVALID=0",
        "AIENG_BLOCKENDVALID=0",
        "AIENG_BLOCKNEW=2",
        "AIENG_DEFINITIONHASH=" + "0" * 64,
        "AIENG_VALIDREFERENCECOUNT=2",
        "AIENG_SEGMENTCOUNT=0",
        "AIENG_AFTERCOUNT=101",
        "AIENG_DBMOD=1",
    ],
)
def test_circuit_fragment_parser_rejects_unverified_results(
    replacement: str,
) -> None:
    symbol = ElectricalSymbolCatalog.load(SYMBOL_CATALOG).get("socket_single")
    marker_name = replacement.split("=", 1)[0]
    invalid = re.sub(rf"(?m)^{marker_name}=.*$", replacement, _circuit_output())

    with pytest.raises(AutoCADIntegrationError):
        _parse_circuit_fragment_markers(
            invalid,
            symbol=symbol,
            maximum_count=100,
        )


@pytest.mark.parametrize(
    "replacement",
    [
        "AIENG_BLOCKNEW=2",
        "AIENG_BLOCKVALID=0",
        "AIENG_BLOCKENDVALID=0",
        "AIENG_DEFINITIONHASH=" + "0" * 64,
        "AIENG_INSERTVALID=0",
        "AIENG_ATTRIBUTECOUNT=1",
        "AIENG_INSUNITS=1",
        "AIENG_UNITSVALID=0",
        "AIENG_AFTERCOUNT=101",
        "AIENG_DBMOD=1",
    ],
)
def test_symbol_parser_rejects_conflicts_or_unverified_results(
    replacement: str,
) -> None:
    symbol = ElectricalSymbolCatalog.load(SYMBOL_CATALOG).get("socket_single")
    valid = (
        "AIENG_ACADVER=24.0\nAIENG_INSUNITS=4\nAIENG_UNITSVALID=1\n"
        "AIENG_SYMBOLID=socket_single\n"
        "AIENG_BLOCKNAME=AIENG_E_SOCKET_SINGLE_V1\n"
        f"AIENG_DEFINITIONHASH={SYMBOL_HASH}\n"
        "AIENG_LAYERNAME=E-POWR-DEVC\nAIENG_LAYERCOLOR=2\n"
        "AIENG_LINETYPE=CONTINUOUS\nAIENG_LAYERVALID=1\n"
        "AIENG_BLOCKNEW=1\nAIENG_BLOCKDEFINED=1\nAIENG_BLOCKVALID=1\n"
        "AIENG_BLOCKHEADERVALID=1\nAIENG_BLOCKENDVALID=1\n"
        "AIENG_VALIDATEDGEOMETRYCOUNT=3\n"
        "AIENG_GEOMETRYCOUNT=3\nAIENG_INSERTVALID=1\n"
        "AIENG_ATTRIBUTECOUNT=0\nAIENG_BEFORECOUNT=0\n"
        "AIENG_AFTERCOUNT=1\nAIENG_DBMOD=0\nAIENG_DONE=1\n"
    )
    marker_name = replacement.split("=", 1)[0]
    invalid = re.sub(rf"(?m)^{marker_name}=.*$", replacement, valid)

    with pytest.raises(AutoCADIntegrationError):
        _parse_symbol_markers(invalid, symbol=symbol, maximum_count=100)
