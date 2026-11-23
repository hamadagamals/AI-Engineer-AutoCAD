"""Tests for strict AutoCAD context validation, evidence, and conservative mapping."""

from __future__ import annotations

import hashlib
import json
from pathlib import Path

import pytest

from agent.exceptions import AutoCADContextError, ConfirmationRequiredError
from core.file_manager import FileManager
from core.electrical_drafting import drafting_proposal_from_dict
from core.electrical_drafting_validation import validate_drafting_proposal
from integrations.autocad_context import (
    AutoCADContextService,
    ContextLimits,
    map_context_to_electrical_model,
    parse_autocad_context,
    rebind_matching_project_conventions,
    rebind_project_conventions,
)
from integrations.autocad_project_conventions import (
    BlockConvention,
    RouteConvention,
    TagConvention,
    create_project_convention_profile,
    load_project_convention_profile,
    parse_project_convention_profile,
)
from integrations.build_partial_convention_rebind import build as build_partial_rebind
from integrations.drafting_preview_acceptance import build_acceptance_proposal


PROJECT_ROOT = Path(__file__).resolve().parents[1]
CATALOG = PROJECT_ROOT / "plugins" / "electrical_symbols" / "catalog.json"
BRIDGE_SOURCE = (
    PROJECT_ROOT / "integrations" / "autocad_context_plugin" / "ContextCommands.cs"
)
BRIDGE_BUILD = PROJECT_ROOT / "integrations" / "autocad_context_plugin" / "build.ps1"
PALETTE_SOURCE = (
    PROJECT_ROOT / "integrations" / "autocad_context_plugin" / "PaletteCommands.cs"
)
LIVE_PALETTE_SCRIPT = (
    PROJECT_ROOT / "integrations" / "autocad_context_plugin" / "live_palette.scr"
)
LIVE_DRAFTING_PREVIEW_SCRIPT = (
    PROJECT_ROOT
    / "integrations"
    / "autocad_context_plugin"
    / "live_drafting_preview.scr"
)
LIVE_DRAFTING_PREVIEW_LAUNCHER = (
    PROJECT_ROOT / "START-AI-ENGINEER-AUTOCAD-PREVIEW.ps1"
)
CONTEXT_ID = "a" * 32
DOCUMENT_ID = "12345678-1234-1234-1234-1234567890ab"


def _context() -> dict[str, object]:
    limits = ContextLimits()
    return {
        "schema_version": 1,
        "context_id": CONTEXT_ID,
        "captured_at": "2026-08-06T12:00:00.0000000Z",
        "source_mode": "selection",
        "privacy": {
            "classification": "project_confidential",
            "provider_egress_allowed": False,
        },
        "document": {
            "document_id": DOCUMENT_ID,
            "identity_source": "fingerprint_guid",
            "name": "sample.dwg",
            "drawing_units": 4,
            "layout": "Model",
            "database_modified": 0,
        },
        "limits": limits.to_dict(),
        "truncated": False,
        "truncation_reasons": [],
        "layers": [
            {
                "name": "E-POWR-DEVC",
                "color_index": 2,
                "linetype": "Continuous",
                "is_off": False,
                "is_frozen": False,
                "is_locked": False,
            },
            {
                "name": "E-WIRE",
                "color_index": 3,
                "linetype": "Continuous",
                "is_off": False,
                "is_frozen": False,
                "is_locked": False,
            },
            {
                "name": "NOTES",
                "color_index": 7,
                "linetype": "Continuous",
                "is_off": False,
                "is_frozen": False,
                "is_locked": False,
            },
        ],
        "entities": [
            _entity(
                "A1",
                "INSERT",
                "BlockReference",
                "E-POWR-DEVC",
                points=[[0.0, 0.0, 0.0]],
                rotation_degrees=0.0,
                scale=[1.0, 1.0, 1.0],
                block_name="AIENG_E_SOCKET_SINGLE_V1",
            ),
            _entity(
                "A2",
                "LINE",
                "Line",
                "E-WIRE",
                points=[[0.0, 0.0, 0.0], [1000.0, 0.0, 0.0]],
            ),
            _entity(
                "A3",
                "TEXT",
                "DBText",
                "NOTES",
                text="DB-1",
            ),
        ],
    }


def _entity(
    object_id: str,
    entity_type: str,
    original_type: str,
    layer: str,
    *,
    points: list[list[float]] | None = None,
    radius: float | None = None,
    rotation_degrees: float | None = None,
    scale: list[float] | None = None,
    block_name: str | None = None,
    text: str | None = None,
) -> dict[str, object]:
    return {
        "object_id": object_id,
        "entity_type": entity_type,
        "original_type": original_type,
        "layer": layer,
        "layout": "Model",
        "visible": True,
        "points": points or [],
        "radius": radius,
        "rotation_degrees": rotation_degrees,
        "scale": scale,
        "block_name": block_name,
        "text": text,
        "attributes": [],
    }


def test_context_maps_catalog_device_wire_and_explicit_unknown() -> None:
    context = parse_autocad_context(_context(), expected_limits=ContextLimits())
    digest = hashlib.sha256(b"context").hexdigest()

    model = map_context_to_electrical_model(
        context,
        evidence_sha256=digest,
        symbol_catalog_path=CATALOG,
    )

    assert context.source_mode == "selection"
    assert context.provider_egress_allowed is False
    assert len(model.devices) == 1
    assert model.devices[0].symbol_id == "socket_single"
    assert model.devices[0].source.object_id == "A1"
    assert len(model.routes) == 1
    assert model.routes[0].points[-1] == (1000.0, 0.0, 0.0)
    assert len(model.unknown_objects) == 1
    assert model.unknown_objects[0].original_type == "DBText"
    assert "unclassified" in model.warnings[-1]
    assert model.provenance.provider_egress_allowed is False


@pytest.mark.parametrize(
    ("mutation", "message"),
    [
        (lambda value: value.update({"extra": True}), "exact schema"),
        (
            lambda value: value["privacy"].update(  # type: ignore[union-attr]
                {"provider_egress_allowed": True}
            ),
            "prohibit provider",
        ),
        (
            lambda value: value["entities"][0].update(  # type: ignore[index,union-attr]
                {"object_id": "not-hex"}
            ),
            "object_id",
        ),
        (
            lambda value: value["entities"][1].update(  # type: ignore[index,union-attr]
                {"points": [[0, 0, 0]]}
            ),
            "exactly two",
        ),
        (
            lambda value: value["entities"][2].update(  # type: ignore[index,union-attr]
                {"layer": "MISSING"}
            ),
            "unknown layer",
        ),
    ],
)
def test_context_rejects_malformed_ambiguous_or_egress_data(
    mutation, message: str
) -> None:
    value = _context()
    mutation(value)

    with pytest.raises(AutoCADContextError, match=message):
        parse_autocad_context(value, expected_limits=ContextLimits())


def test_context_enforces_independent_limits_and_truncation_consistency() -> None:
    value = _context()
    value["entities"] = value["entities"] * 200  # type: ignore[operator]
    with pytest.raises(AutoCADContextError, match="entity limit"):
        parse_autocad_context(value, expected_limits=ContextLimits())

    value = _context()
    value["truncated"] = True
    with pytest.raises(AutoCADContextError, match="inconsistent"):
        parse_autocad_context(value, expected_limits=ContextLimits())

    value = _context()
    value["limits"]["maximum_entities"] = 10  # type: ignore[index]
    with pytest.raises(AutoCADContextError, match="configured local policy"):
        parse_autocad_context(value, expected_limits=ContextLimits())

    legacy = _context()
    legacy["limits"]["maximum_points_per_entity"] = 256  # type: ignore[index]
    parsed_legacy = parse_autocad_context(
        legacy, expected_limits=ContextLimits()
    )
    assert parsed_legacy.limits.maximum_points_per_entity == 256

    excessive = _context()
    excessive["limits"]["maximum_points_per_entity"] = 4097  # type: ignore[index]
    with pytest.raises(AutoCADContextError, match="through 4096"):
        parse_autocad_context(excessive, expected_limits=ContextLimits())

    cyclic = _context()
    cyclic["entities"] = cyclic  # type: ignore[assignment]
    with pytest.raises(AutoCADContextError, match="cyclic"):
        parse_autocad_context(cyclic, expected_limits=ContextLimits())


def test_context_accepts_observed_empty_text_without_inventing_content() -> None:
    value = _context()
    value["entities"][2]["text"] = ""  # type: ignore[index]

    context = parse_autocad_context(value, expected_limits=ContextLimits())

    assert context.entities[2].text == ""


def test_context_accepts_mirrored_block_scale_and_rejects_zero_scale() -> None:
    value = _context()
    value["entities"][0]["scale"] = [-1.0, 1.0, 1.0]  # type: ignore[index]

    context = parse_autocad_context(value, expected_limits=ContextLimits())

    assert context.entities[0].scale == (-1.0, 1.0, 1.0)
    model = map_context_to_electrical_model(
        context,
        evidence_sha256=hashlib.sha256(b"mirrored-context").hexdigest(),
        symbol_catalog_path=CATALOG,
    )
    assert model.devices[0].scale == (-1.0, 1.0, 1.0)

    value["entities"][0]["scale"] = [0.0, 1.0, 1.0]  # type: ignore[index]
    with pytest.raises(AutoCADContextError, match="non-zero"):
        parse_autocad_context(value, expected_limits=ContextLimits())


def test_exact_reviewed_project_conventions_map_alias_blocks_and_routes(
    tmp_path: Path,
) -> None:
    value = _context()
    value["layers"][1]["name"] = "PROJECT-LIGHT-CIRCUIT"  # type: ignore[index]
    value["entities"][0]["block_name"] = "PROJECT PENDANT L05"  # type: ignore[index]
    value["entities"][0]["scale"] = [-1.0, 1.0, 1.0]  # type: ignore[index]
    value["entities"][1]["layer"] = "PROJECT-LIGHT-CIRCUIT"  # type: ignore[index]
    value["entities"][2]["text"] = "4/ DB3-GA2"  # type: ignore[index]
    context = parse_autocad_context(value, expected_limits=ContextLimits())
    evidence_sha256 = hashlib.sha256(b"project-context").hexdigest()
    profile = create_project_convention_profile(
        profile_id="profile:lighting",
        document_id=context.document_id,
        context_id=context.context_id,
        context_evidence_sha256=evidence_sha256,
        source_drawing_sha256=hashlib.sha256(b"drawing").hexdigest(),
        reviewed_by="engineer:test",
        reviewed_on="2026-08-13",
        block_mappings=(
            BlockConvention(
                "PROJECT PENDANT L05",
                "E-POWR-DEVC",
                "lighting_pendant_l05",
                "Reviewed pendant type L05.",
            ),
        ),
        route_mappings=(
            RouteConvention(
                "PROJECT-LIGHT-CIRCUIT",
                "lighting_circuit_route",
                "explicit_endpoints_only",
            ),
        ),
        tag_mappings=(
            TagConvention(
                "NOTES", "circuit_reference", "circuit_number_slash_panel_id"
            ),
        ),
        assumptions=("No connectivity is inferred from crossings.",),
    )

    model = map_context_to_electrical_model(
        context,
        evidence_sha256=evidence_sha256,
        symbol_catalog_path=CATALOG,
        conventions=profile,
    )

    assert model.devices[0].symbol_id == "lighting_pendant_l05"
    assert model.devices[0].scale == (-1.0, 1.0, 1.0)
    assert len(model.routes) == 1
    assert model.panels[0].name == "DB3-GA2"
    assert model.circuits[0].name == "4"
    assert model.circuits[0].device_ids == ()
    assert "spatial circuit/control association is not inferred" in model.warnings[-1]

    round_trip = parse_project_convention_profile(
        json.loads(json.dumps(profile.to_dict()))
    )
    assert round_trip.profile_sha256 == profile.profile_sha256
    tampered = json.loads(json.dumps(profile.to_dict()))
    tampered["block_mappings"][0]["description"] = "changed"
    with pytest.raises(AutoCADContextError, match="SHA-256"):
        parse_project_convention_profile(tampered)
    with pytest.raises(AutoCADContextError, match="exact context"):
        map_context_to_electrical_model(
            context,
            evidence_sha256=hashlib.sha256(b"different").hexdigest(),
            symbol_catalog_path=CATALOG,
            conventions=profile,
        )

    rebound_value = json.loads(json.dumps(value))
    rebound_value["context_id"] = "b" * 32
    rebound = parse_autocad_context(
        rebound_value, expected_limits=ContextLimits()
    )
    rebound_evidence = hashlib.sha256(b"rebound-context").hexdigest()
    rebound_profile = rebind_project_conventions(
        profile,
        rebound,
        context_evidence_sha256=rebound_evidence,
        source_drawing_sha256=profile.source_drawing_sha256,
    )
    rebound_model = map_context_to_electrical_model(
        rebound,
        evidence_sha256=rebound_evidence,
        symbol_catalog_path=CATALOG,
        conventions=rebound_profile,
    )
    assert rebound_model.devices[0].symbol_id == "lighting_pendant_l05"

    rebound_value["entities"][0]["block_name"] = "UNREVIEWED"  # type: ignore[index]
    unreviewed = parse_autocad_context(
        rebound_value, expected_limits=ContextLimits()
    )
    with pytest.raises(AutoCADContextError, match="unreviewed block"):
        rebind_project_conventions(
            profile,
            unreviewed,
            context_evidence_sha256=rebound_evidence,
            source_drawing_sha256=profile.source_drawing_sha256,
        )

    dbmod_value = json.loads(json.dumps(value))
    dbmod_value["context_id"] = "c" * 32
    dbmod_value["document"]["database_modified"] = 20  # type: ignore[index]
    changed_database = parse_autocad_context(
        dbmod_value, expected_limits=ContextLimits()
    )
    with pytest.raises(AutoCADContextError, match="DBMOD"):
        rebind_project_conventions(
            profile,
            changed_database,
            context_evidence_sha256=rebound_evidence,
            source_drawing_sha256=profile.source_drawing_sha256,
        )

    partial_value = json.loads(json.dumps(value))
    partial_value["context_id"] = "d" * 32
    unknown_block = json.loads(json.dumps(partial_value["entities"][0]))
    unknown_block["object_id"] = "D4"
    unknown_block["block_name"] = "UNREVIEWED FIXTURE"
    partial_value["entities"].append(unknown_block)
    partial_context = parse_autocad_context(
        partial_value, expected_limits=ContextLimits()
    )
    partial_evidence = hashlib.sha256(b"partial-context").hexdigest()
    partial_profile = rebind_matching_project_conventions(
        profile,
        partial_context,
        context_evidence_sha256=partial_evidence,
        source_drawing_sha256=profile.source_drawing_sha256,
    )
    partial_model = map_context_to_electrical_model(
        partial_context,
        evidence_sha256=partial_evidence,
        symbol_catalog_path=CATALOG,
        conventions=partial_profile,
    )
    assert len(partial_model.devices) == 1
    assert len(partial_model.routes) == 1
    assert len(partial_model.unknown_objects) == 1
    assert "blocks 1 matched/1 unmatched" in partial_profile.assumptions[-1]
    with pytest.raises(AutoCADContextError, match="exact document and source drawing"):
        rebind_matching_project_conventions(
            profile,
            partial_context,
            context_evidence_sha256=partial_evidence,
            source_drawing_sha256="f" * 64,
        )

    workspace = tmp_path / "workspace"
    inbox = workspace / ".ai-engineer" / "autocad" / "context" / "inbox"
    profiles = workspace / ".ai-engineer" / "autocad" / "project-conventions"
    inbox.mkdir(parents=True)
    profiles.mkdir(parents=True)
    previous_path = profiles / "previous.json"
    previous_path.write_text(json.dumps(profile.to_dict()), encoding="utf-8")
    encoded_context = json.dumps(partial_value, separators=(",", ":")).encode("utf-8")
    (inbox / f"{partial_context.context_id}.json").write_bytes(encoded_context)
    (workspace / "drawing.dwg").write_bytes(b"drawing")
    built_path = build_partial_rebind(
        workspace,
        context_id=partial_context.context_id,
        previous_profile_path=Path(".ai-engineer/autocad/project-conventions/previous.json"),
        drawing_path=Path("drawing.dwg"),
        output_path=Path(".ai-engineer/autocad/project-conventions/current.json"),
    )
    built = load_project_convention_profile(built_path)
    assert built.context_evidence_sha256 == hashlib.sha256(encoded_context).hexdigest()
    assert "blocks 1 matched/1 unmatched" in built.assumptions[-1]


def test_weatherproof_qualifier_is_exact_and_not_a_control_group() -> None:
    value = _context()
    value["entities"] = [value["entities"][0], value["entities"][2]]  # type: ignore[index]
    value["entities"][1]["text"] = "WP"  # type: ignore[index]
    context = parse_autocad_context(value, expected_limits=ContextLimits())
    evidence_sha256 = hashlib.sha256(b"weatherproof-context").hexdigest()
    profile = create_project_convention_profile(
        profile_id="profile:weatherproof",
        document_id=context.document_id,
        context_id=context.context_id,
        context_evidence_sha256=evidence_sha256,
        source_drawing_sha256=hashlib.sha256(b"drawing").hexdigest(),
        reviewed_by="engineer:test",
        reviewed_on="2026-08-13",
        block_mappings=(
            BlockConvention(
                "AIENG_E_SOCKET_SINGLE_V1", "E-POWR-DEVC", "socket_single",
                "Reviewed fixture block.",
            ),
        ),
        route_mappings=(),
        tag_mappings=(
            TagConvention("NOTES", "environmental_qualifier", "weatherproof_literal"),
        ),
        assumptions=("Weatherproof qualifier has no inferred spatial association.",),
    )

    model = map_context_to_electrical_model(
        context,
        evidence_sha256=evidence_sha256,
        symbol_catalog_path=CATALOG,
        conventions=profile,
    )

    assert model.unknown_objects == ()
    assert "1 reviewed weatherproof qualifier(s)" in model.warnings[-1]
    rebound = rebind_project_conventions(
        profile,
        context,
        context_evidence_sha256=evidence_sha256,
        source_drawing_sha256=profile.source_drawing_sha256,
    )
    assert rebound.context_id == context.context_id

    invalid_value = json.loads(json.dumps(value))
    invalid_value["entities"][1]["text"] = "WPX"
    invalid_context = parse_autocad_context(
        invalid_value, expected_limits=ContextLimits()
    )
    with pytest.raises(AutoCADContextError, match="unreviewed tag"):
        rebind_project_conventions(
            profile,
            invalid_context,
            context_evidence_sha256=evidence_sha256,
            source_drawing_sha256=profile.source_drawing_sha256,
        )


def test_context_service_binds_exact_id_hash_workspace_and_confirmation(
    tmp_path: Path,
) -> None:
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    files = FileManager([workspace], tmp_path / "backup", max_read_bytes=2_000_000)
    service = AutoCADContextService(files, CATALOG)
    inbox = workspace / ".ai-engineer" / "autocad" / "context" / "inbox"
    inbox.mkdir(parents=True)
    evidence = json.dumps(_context(), separators=(",", ":")).encode("utf-8")
    path = inbox / f"{CONTEXT_ID}.json"
    path.write_bytes(evidence)
    digest = hashlib.sha256(evidence).hexdigest()

    with pytest.raises(ConfirmationRequiredError):
        service.import_context(context_id=CONTEXT_ID, evidence_sha256=digest)
    with pytest.raises(AutoCADContextError, match="digest"):
        service.import_context(
            context_id=CONTEXT_ID,
            evidence_sha256="b" * 64,
            confirmed=True,
        )

    result = service.import_context(
        context_id=CONTEXT_ID,
        evidence_sha256=digest,
        confirmed=True,
    )

    assert result.evidence_bytes == len(evidence)
    assert result.read_only is True
    assert result.provider_egress_performed is False
    assert result.model.devices[0].source.context_id == CONTEXT_ID


def test_context_service_applies_one_exact_self_hashed_convention_profile(
    tmp_path: Path,
) -> None:
    workspace = tmp_path / "workspace"
    inbox = workspace / ".ai-engineer" / "autocad" / "context" / "inbox"
    profiles = workspace / ".ai-engineer" / "autocad" / "project-conventions"
    inbox.mkdir(parents=True)
    profiles.mkdir(parents=True)
    value = _context()
    value["entities"][0]["block_name"] = "PROJECT PENDANT L05"  # type: ignore[index]
    evidence = json.dumps(value, separators=(",", ":")).encode("utf-8")
    digest = hashlib.sha256(evidence).hexdigest()
    (inbox / f"{CONTEXT_ID}.json").write_bytes(evidence)
    profile = create_project_convention_profile(
        profile_id="profile:lighting",
        document_id=DOCUMENT_ID,
        context_id=CONTEXT_ID,
        context_evidence_sha256=digest,
        source_drawing_sha256=hashlib.sha256(b"drawing").hexdigest(),
        reviewed_by="engineer:test",
        reviewed_on="2026-08-13",
        block_mappings=(
            BlockConvention(
                "PROJECT PENDANT L05", "E-POWR-DEVC", "lighting_pendant_l05",
                "Reviewed pendant type L05.",
            ),
        ),
        route_mappings=(),
        tag_mappings=(),
        assumptions=("Exact context only.",),
    )
    (profiles / "lighting.json").write_text(
        json.dumps(profile.to_dict()), encoding="utf-8"
    )
    service = AutoCADContextService(
        FileManager([workspace], tmp_path / "backup", max_read_bytes=2_000_000),
        CATALOG,
    )

    result = service.import_context(
        context_id=CONTEXT_ID, evidence_sha256=digest, confirmed=True
    )

    assert result.model.devices[0].symbol_id == "lighting_pendant_l05"
    assert "exact bound context evidence" in result.model.warnings[-1]

    (profiles / "duplicate.json").write_text(
        json.dumps(profile.to_dict()), encoding="utf-8"
    )
    with pytest.raises(AutoCADContextError, match="Multiple"):
        service.import_context(
            context_id=CONTEXT_ID, evidence_sha256=digest, confirmed=True
        )


def test_context_service_rejects_missing_or_unbounded_evidence(tmp_path: Path) -> None:
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    service = AutoCADContextService(
        FileManager([workspace], tmp_path / "backup"), CATALOG
    )
    with pytest.raises(AutoCADContextError, match="Exactly one"):
        service.import_context(
            context_id=CONTEXT_ID,
            evidence_sha256="a" * 64,
            confirmed=True,
        )


def test_bridge_source_matches_context_modes_and_is_read_only() -> None:
    source = BRIDGE_SOURCE.read_text(encoding="utf-8")

    assert 'Export(document, "selection"' in source
    assert 'Export(document, "drawing"' in source
    assert '[CommandMethod("AIENGDEVICETAGCONTEXT"' in source
    assert "entity is BlockReference || entity is DBText || entity is MText" in source
    assert "selection contains no supported blocks or text tags" in source
    assert '[CommandMethod("AIENGROUTECONTEXT"' in source
    assert "Select one representative LINE or lightweight POLYLINE" in source
    assert "entity.LayerId == routeLayerId" in source
    assert "selection contains no supported routes on the chosen layer" in source
    assert '"explicit_selection"' not in source
    assert '"active_drawing"' not in source
    assert "OpenMode.ForWrite" not in source
    assert "SendStringToExecute" not in source
    assert "provider_egress_allowed\", false" in source
    assert '"\\nAIENG_CONTEXT_DURATION_MS="' in source
    assert 'stopwatch.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture)' in source
    assert 'Path.Combine(assembly, "settings.json")' in source
    assert BRIDGE_BUILD.is_file()
    assert "maximum_points_per_entity = 4096" in BRIDGE_BUILD.read_text(encoding="utf-8")


def test_palette_source_is_loopback_only_read_only_and_bounded() -> None:
    source = PALETTE_SOURCE.read_text(encoding="utf-8")
    build = BRIDGE_BUILD.read_text(encoding="utf-8")

    assert 'CommandMethod("AIENGPALETTE"' in source
    assert "new PaletteSet(" in source
    assert 'new Uri("http://127.0.0.1:"' in source
    assert "handler.UseProxy = false" in source
    assert "BridgeHttpClientFactory.GetSharedClient(75)" in source
    assert 'document.TryGetValue("detail", out raw)' in source
    assert "SafeText(detail, 384)" in source
    assert 'GetEnvironmentVariable("AI_ENGINEER_API_TOKEN")' in source
    assert 'GetEnvironmentVariable("AI_ENGINEER_API_PORT")' in source
    assert 'request["context_id"] = evidence.ContextId' in source
    assert 'RequiredInt(summary, "unknown_count")' in source
    assert 'RequiredInt(summary, "block_reference_count")' in source
    assert 'RequiredInt(summary, "route_primitive_count")' in source
    assert '";raw_blocks="' in source
    assert '";raw_route_primitives="' in source
    assert 'active ? "المحادثة نشطة" : "محادثة جديدة"' in source
    assert "_start.Enabled = !active;" in source
    assert '"/v1/copilot/general-sessions"' in source
    assert '"conversation.history"' in source
    assert 'value.Replace("\\r\\n", "\\n").Replace("\\r", "\\n")' in source
    assert 'MessageBoxButtons.YesNo' in source
    assert 'MessageBoxDefaultButton.Button2' in source
    assert '"اسأل AI"' in source
    assert "actionButtons.Controls.Add(_prepareReview);" not in source
    assert "if (_hasSessionSummary && !String.IsNullOrEmpty(_sessionId))" in source
    assert "private void ShowSummary()" in source
    assert "_hasSessionSummary = false;" in source
    assert '"question.text"' in source
    assert '"context.summary"' in source
    assert '"/egress-reviews"' in source
    assert '"/answers"' in source
    assert "Nothing was sent." in source
    assert "OpenMode.ForWrite" not in source
    assert "SendStringToExecute" not in source
    assert "Process.Start" not in source
    assert "https://" not in source
    assert "PaletteCommands.cs" in build
    assert "System.Net.Http.dll" in build


def test_autocad_project_agent_uses_reviewed_developer_loop() -> None:
    source = PALETTE_SOURCE.read_text(encoding="utf-8")

    assert '_legacyPalette.Add("Project Agent", new ProjectAgentPaletteControl())' in source
    assert "internal sealed class ProjectAgentPaletteControl" in source
    assert '"/v1/developer/runs"' in source
    assert '"/plan-review"' in source
    assert '"/action-review"' in source
    assert '"/v1/approvals/"' in source
    assert '"/decision"' in source
    assert 'risk != "low" && risk != "moderate"' in source
    assert "ExecuteInCommandContextAsync" in source
    assert "new ContextCommands().ExportExplicitSelection();" in source
    assert '"autocad.preview_component_moves' in source or "then autocad.preview_component_moves." in source
    assert "System.Security.Cryptography.SHA256.Create()" in source
    assert 'binding["expected_source_sha256"] = digest;' in source
    assert "if (egress != DialogResult.Yes) return;" in source
    assert "MessageBoxDefaultButton.Button2" in source
    assert "SendStringToExecute" not in source


def test_palette_drafting_execution_is_separate_hash_bound_and_scratch_only() -> None:
    source = PALETTE_SOURCE.read_text(encoding="utf-8")
    drafting = source.split(
        "internal sealed class DraftingPreviewPaletteControl", maxsplit=1
    )[1]

    assert 'new DraftingPreviewPaletteControl()' in source
    assert '"/v1/drafting/runs"' in drafting
    assert '"/preview"' in drafting
    assert '"X-AI-Engineer-Drafting-Key"' in drafting
    assert 'RequiredString(binding, "context_id")' in drafting
    assert 'RequiredString(binding, "context_evidence_sha256")' in drafting
    assert 'RequiredBool(preview, "drawing_mutation_performed")' in drafting
    assert 'RequiredBool(preview, "execution_approved")' in drafting
    assert "HasPersistentDatabaseChanges(evidence.DatabaseModified)" in drafting
    assert "objectDatabase | databaseVariable | field" in drafting
    assert "EnsureContained(path, workspaceRoot)" in drafting
    assert '"/execution-review"' in drafting
    assert '"/execution-review/decision"' in drafting
    assert '"/execute"' in drafting
    assert "CalculateSha256(_sourcePath)" in drafting
    assert 'RequiredBool(execution, "source_will_be_modified")' in drafting
    assert 'RequiredBool(execution, "handoff_requires_separate_approval")' in drafting
    assert "Approve this exact plan for one scratch execution?" in drafting
    assert "The source will not be replaced." in drafting
    assert '"/preview-review"' not in drafting
    assert "OpenMode.ForWrite" not in drafting
    assert "SendStringToExecute" not in drafting


def test_palette_quantity_takeoff_is_reviewed_hash_bound_and_read_only() -> None:
    source = PALETTE_SOURCE.read_text(encoding="utf-8")
    takeoff = source.split(
        "internal sealed class QuantityTakeoffPaletteControl", maxsplit=1
    )[1]

    assert 'new QuantityTakeoffPaletteControl()' in source
    assert '"/v1/takeoff/reports"' in takeoff
    assert '"/v1/takeoff/exports/csv"' in takeoff
    assert 'request["context_id"] = evidence.ContextId' in takeoff
    assert 'request["context_evidence_sha256"] = evidence.Sha256' in takeoff
    assert 'request["reviewed_report_sha256"] = _reportSha256' in takeoff
    assert 'RequiredBool(report, "drawing_mutation_performed")' in takeoff
    assert 'RequiredBool(exported, "file_write_performed")' in takeoff
    assert '"m19.takeoff.v1"' in takeoff
    assert "MessageBoxButtons.YesNo" in takeoff
    assert "OpenMode.ForWrite" not in takeoff
    assert "SendStringToExecute" not in takeoff
    assert "File.WriteAllText" not in takeoff


def test_palette_panel_sld_review_is_exact_explicit_and_read_only() -> None:
    source = PALETTE_SOURCE.read_text(encoding="utf-8")
    panel = source.split(
        "internal sealed class PanelSldPaletteControl", maxsplit=1
    )[1].split("internal sealed class LowVoltageCalculationPaletteControl", maxsplit=1)[0]

    assert 'new PanelSldPaletteControl()' in source
    assert '"Panels / SLD"' in source
    assert '"/v1/panels/extract-schedule"' in panel
    assert 'request["context_id"] = evidence.ContextId' in panel
    assert 'request["context_evidence_sha256"] = evidence.Sha256' in panel
    assert 'request["reviewed_by"] = reviewer' in panel
    assert 'RequiredBool(extraction, "existing_schedule_extraction_performed")' in panel
    assert 'RequiredBool(extraction, "provider_egress_performed")' in panel
    assert 'RequiredBool(extraction, "drawing_mutation_performed")' in panel
    assert 'RequiredString(schedule, "source_contract_sha256")' in panel
    assert 'RequiredString(contract, "contract_sha256")' in panel
    assert 'RequiredObjects(extraction, "findings")' in panel
    assert "UNKNOWN/CONFLICT" in panel
    assert 'RequiredObjects(schedule, "rows")' in panel
    assert "OpenMode.ForWrite" not in panel
    assert "SendStringToExecute" not in panel
    assert "File.WriteAllText" not in panel


def test_palette_compliance_review_is_cited_hash_bound_and_read_only() -> None:
    source = PALETTE_SOURCE.read_text(encoding="utf-8")
    compliance = source.split(
        "internal sealed class CompliancePaletteControl", maxsplit=1
    )[1].split("internal sealed class PanelSldPaletteControl", maxsplit=1)[0]

    assert 'new CompliancePaletteControl()' in source
    assert '"Code Compliance"' in source
    assert '"/v1/compliance/evaluate"' in compliance
    assert '"AI_ENGINEER_COMPLIANCE_REQUEST"' in compliance
    assert "Load += async delegate { await Evaluate(); };" in compliance
    assert 'RequiredSha256(profile, "profile_sha256")' in compliance
    assert 'RequiredSha256(result, "result_sha256")' in compliance
    assert 'RequiredSha256(clause, "source_content_sha256")' in compliance
    assert 'RequiredString(clause, "clause_locator")' in compliance
    assert 'RequiredBool(result, "provider_retrieval_performed")' in compliance
    assert 'RequiredBool(result, "regulatory_approval_performed")' in compliance
    assert 'RequiredBool(result, "drawing_mutation_performed")' in compliance
    assert "PASS/FAIL/UNKNOWN/CONFLICT" in compliance
    assert "OpenMode.ForWrite" not in compliance
    assert "SendStringToExecute" not in compliance
    assert "File.WriteAllText" not in compliance


def test_palette_manufacturer_review_is_hash_bound_and_read_only() -> None:
    source = PALETTE_SOURCE.read_text(encoding="utf-8")
    manufacturer = source.split(
        "internal sealed class ManufacturerReviewPaletteControl", maxsplit=1
    )[1].split("internal sealed class PanelSldPaletteControl", maxsplit=1)[0]

    assert 'new ManufacturerReviewPaletteControl()' in source
    assert '"Manufacturers"' in source
    assert '"/v1/manufacturers/review"' in manufacturer
    assert '"AI_ENGINEER_MANUFACTURER_REVIEW_REQUEST"' in manufacturer
    assert "Load += async delegate { await Review(); };" in manufacturer
    assert 'RequiredSha256(bundle, "bundle_sha256")' in manufacturer
    assert 'RequiredSha256(result, "result_sha256")' in manufacturer
    assert 'RequiredSha256(artifact, "content_sha256")' in manufacturer
    assert 'RequiredBool(result, "provider_egress_performed")' in manufacturer
    assert 'RequiredBool(result, "retrieval_performed_by_review")' in manufacturer
    assert 'RequiredBool(result, "catalog_registration_performed_by_review")' in manufacturer
    assert 'RequiredBool(result, "drawing_mutation_performed")' in manufacturer
    assert 'RequiredString(finding, "subject_id")' in manufacturer
    assert "PASS/UNKNOWN/CONFLICT" in manufacturer
    assert "OpenMode.ForWrite" not in manufacturer
    assert "SendStringToExecute" not in manufacturer
    assert "File.WriteAllText" not in manufacturer


def test_palette_manufacturer_discovery_requires_approved_request_and_no_actions() -> None:
    source = PALETTE_SOURCE.read_text(encoding="utf-8")
    discovery = source.split(
        "internal sealed class ManufacturerDiscoveryPaletteControl", maxsplit=1
    )[1].split("internal sealed class ManufacturerReviewPaletteControl", maxsplit=1)[0]

    assert 'new ManufacturerDiscoveryPaletteControl()' in source
    assert '"Product Discovery"' in source
    assert '"/v1/manufacturers/discover"' in discovery
    assert '"/v1/manufacturers/discover/select"' in discovery
    assert 'Choose for separate review' in discovery
    assert 'RequiredBool(selection, "product_approval_performed")' in discovery
    assert 'errorDocument.TryGetValue("detail", out rawDetail)' in discovery
    assert 'AI_ENGINEER_MANUFACTURER_DISCOVERY_REQUEST' in discovery
    assert 'private void LoadRequest(string path)' in discovery
    assert 'RequiredSha256(request, "request_sha256")' in discovery
    assert 'RequiredSha256(result, "result_sha256")' in discovery
    assert 'RequiredBool(result, "provider_egress_performed")' in discovery
    assert 'RequiredBool(result, "automatic_selection_performed")' in discovery
    assert 'RequiredBool(result, "automatic_download_performed")' in discovery
    assert 'RequiredBool(result, "catalog_registration_performed")' in discovery
    assert 'RequiredBool(result, "drawing_mutation_performed")' in discovery
    assert 'RequiredString(finding, "outcome").ToUpperInvariant()' in discovery
    assert 'layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));' in discovery
    assert '_output.MinimumSize = new Size(0, 160);' in discovery
    assert '_output.WordWrap = true;' in discovery
    assert 'choice.Controls.Add(_candidates, 0, 0);' in discovery
    assert '"Copy complete result"' in discovery
    assert 'Clipboard.SetText(_output.Text);' in discovery
    assert 'BridgeHttpClientFactory.GetSharedClient(210)' in discovery
    assert "OpenMode.ForWrite" not in discovery
    assert "SendStringToExecute" not in discovery
    assert "File.WriteAllText" not in discovery


def test_palette_low_voltage_calculation_uses_reviewed_inputs_and_no_compliance_claim() -> None:
    source = PALETTE_SOURCE.read_text(encoding="utf-8")
    calculation = source.split(
        "internal sealed class LowVoltageCalculationPaletteControl", maxsplit=1
    )[1]

    assert 'new LowVoltageCalculationPaletteControl()' in source
    assert '"/v1/takeoff/reports"' in calculation
    assert '"/v1/calculations/low-voltage"' in calculation
    assert '"m20.lv_current_voltage_drop.v1"' in calculation


def test_palette_load_aggregation_uses_explicit_factors_phases_and_read_only_api() -> None:
    source = PALETTE_SOURCE.read_text(encoding="utf-8")
    calculation = source.split(
        "internal sealed class LowVoltageCalculationPaletteControl", maxsplit=1
    )[1]

    assert '"Power per load (W)"' in calculation
    assert '"Per-load demand"' in calculation
    assert '"Group diversity"' in calculation
    assert '"Load phases CSV"' in calculation
    assert '"m20.load_aggregation.v1"' in calculation
    assert '"/v1/calculations/load-aggregation"' in calculation
    assert 'request.Remove("policy")' in calculation
    assert 'RequiredBool(result, "drawing_mutation_performed")' in calculation


def test_palette_cable_ampacity_uses_reviewed_chain_and_rejects_selection_claims() -> None:
    source = PALETTE_SOURCE.read_text(encoding="utf-8")
    calculation = source.split(
        "internal sealed class LowVoltageCalculationPaletteControl", maxsplit=1
    )[1]

    assert '"Ampacity route number"' in calculation
    assert '"Cable designation"' in calculation
    assert '"Base ampacity (A)"' in calculation
    assert '"Ambient factor"' in calculation
    assert '"Grouping factor"' in calculation
    assert '"Ampacity/factor basis"' in calculation
    assert '"m20.cable_ampacity_derating.v1"' in calculation
    assert '"/v1/calculations/cable-ampacity"' in calculation
    assert 'RequiredBool(result, "cable_selection_performed")' in calculation
    assert 'RequiredBool(result, "protective_device_selection_performed")' in calculation
    assert 'RequiredBool(result, "drawing_mutation_performed")' in calculation
    assert 'RequiredBool(result, "compliance_evaluation_performed")' in calculation


def test_palette_short_circuit_uses_reviewed_impedance_and_rejects_extra_claims() -> None:
    source = PALETTE_SOURCE.read_text(encoding="utf-8")
    calculation = source.split(
        "internal sealed class LowVoltageCalculationPaletteControl", maxsplit=1
    )[1]

    assert '"Fault route number"' in calculation
    assert '"Fault arrangement"' in calculation
    assert '"Fault nominal voltage (V)"' in calculation
    assert '"Voltage multiplier"' in calculation
    assert '"Source R (ohm)"' in calculation
    assert '"Source X (ohm)"' in calculation
    assert '"Path R (ohm)"' in calculation
    assert '"Path X (ohm)"' in calculation
    assert '"m20.short_circuit_source_equivalent.v1"' in calculation
    assert '"/v1/calculations/short-circuit"' in calculation
    assert 'RequiredBool(result, "protective_device_adequacy_evaluated")' in calculation
    assert 'RequiredBool(result, "peak_current_calculated")' in calculation
    assert 'RequiredBool(result, "arc_flash_calculated")' in calculation
    assert 'RequiredBool(result, "drawing_mutation_performed")' in calculation
    assert 'RequiredBool(result, "compliance_evaluation_performed")' in calculation


def test_palette_candidate_review_recalculates_inputs_and_shows_five_findings() -> None:
    source = PALETTE_SOURCE.read_text(encoding="utf-8")
    calculation = source.split(
        "internal sealed class LowVoltageCalculationPaletteControl", maxsplit=1
    )[1]

    assert '"Protective device"' in calculation
    assert '"Device rating (A)"' in calculation
    assert '"Breaking capacity (A)"' in calculation
    assert '"Required Iz / Ib"' in calculation
    assert '"Maximum voltage drop (%)"' in calculation
    assert '"Review candidate"' in calculation
    assert '"m20.candidate_adequacy.v1"' in calculation
    assert '"/v1/calculations/candidate-adequacy"' in calculation
    assert 'request["low_voltage"] = lowVoltage' in calculation
    assert 'request["ampacity"] = ampacity' in calculation
    assert 'request["short_circuit"] = shortCircuit' in calculation
    assert 'RequiredObjects(result, "findings")' in calculation
    assert 'RequiredBool(result, "automatic_selection_performed")' in calculation
    assert 'RequiredBool(result, "drawing_mutation_performed")' in calculation
    assert 'RequiredBool(result, "compliance_evaluation_performed")' in calculation


def test_palette_protective_conductor_shows_source_outcome_and_no_selection() -> None:
    source = PALETTE_SOURCE.read_text(encoding="utf-8")
    calculation = source.split(
        "internal sealed class LowVoltageCalculationPaletteControl", maxsplit=1
    )[1]

    assert '"PE rule applicability"' in calculation
    assert '"Disconnection basis"' in calculation
    assert '"Disconnection time (s)"' in calculation
    assert '"Conductor factor k (A*sqrt(s)/mm2)"' in calculation
    assert '"PE rule source SHA-256"' in calculation
    assert '"Size PE conductor"' in calculation
    assert '"m20.protective_conductor_adiabatic.v1"' in calculation
    assert '"/v1/calculations/protective-conductor"' in calculation
    assert 'request["short_circuit"] = shortCircuit' in calculation
    assert '"Outcome: " + outcome.ToUpperInvariant()' in calculation
    assert 'RequiredBool(result, "conductor_selection_performed")' in calculation
    assert 'RequiredBool(result, "drawing_mutation_performed")' in calculation
    assert 'RequiredBool(result, "compliance_evaluation_performed")' in calculation


def test_lv_palette_pins_commands_and_scrolls_long_inputs_independently() -> None:
    source = PALETTE_SOURCE.read_text(encoding="utf-8")
    calculation = source.split(
        "internal sealed class LowVoltageCalculationPaletteControl", maxsplit=1
    )[1]

    assert "inputs.AutoScroll = true;" in calculation
    assert "inputs.Dock = DockStyle.Fill;" in calculation
    assert "new RowStyle(SizeType.Percent, 55)" in calculation
    assert "new RowStyle(SizeType.Percent, 45)" in calculation
    assert calculation.index("layout.Controls.Add(buttons, 0, 1)") < calculation.index(
        "layout.Controls.Add(inputs, 0, 2)"
    )
    assert 'RequiredBool(result, "compliance_evaluation_performed")' in calculation
    assert 'calculation["load_object_ids"] = loadIds.ToArray()' in calculation
    assert 'calculation["route_ids"] = routeIds.ToArray()' in calculation
    assert 'calculation["reviewed_by"]' in calculation
    assert 'calculation["assumptions"]' in calculation
    assert 'RequiredBool(result, "compliance_evaluation_performed")' in calculation
    assert 'RequiredBool(result, "drawing_mutation_performed")' in calculation
    assert "OpenMode.ForWrite" not in calculation
    assert "SendStringToExecute" not in calculation
    assert "File.WriteAllText" not in calculation


def test_live_preview_acceptance_proposal_reuses_exact_context_and_validates(
    tmp_path: Path,
) -> None:
    workspace = tmp_path / "workspace"
    inbox = workspace / ".ai-engineer" / "autocad" / "context" / "inbox"
    inbox.mkdir(parents=True)
    evidence = json.dumps(_context(), separators=(",", ":")).encode("utf-8")
    (inbox / f"{CONTEXT_ID}.json").write_bytes(evidence)
    digest = hashlib.sha256(evidence).hexdigest()

    output = build_acceptance_proposal(
        workspace_root=workspace,
        context_id=CONTEXT_ID,
        evidence_sha256=digest,
        output_path=Path("drafting-preview") / "proposal.json",
    )
    proposal = drafting_proposal_from_dict(json.loads(output.read_text(encoding="utf-8")))
    imported = AutoCADContextService(
        FileManager([workspace], tmp_path / "backup"), CATALOG
    ).import_context(
        context_id=CONTEXT_ID,
        evidence_sha256=digest,
        confirmed=True,
    )
    validation = validate_drafting_proposal(
        proposal,
        model=imported.model,
        symbol_catalog_path=CATALOG,
    )

    assert proposal.binding.context_id == CONTEXT_ID
    assert proposal.binding.context_evidence_sha256 == digest
    assert len(proposal.devices) == 2
    assert len(proposal.routes) == 1
    assert next(
        item for item in proposal.layers if item.layer_id == proposal.routes[0].layer_id
    ).name == "E-WIRE"
    assert validation.findings == ()
    assert validation.preview_allowed is True


def test_live_palette_acceptance_script_restores_security_and_never_saves() -> None:
    source = LIVE_PALETTE_SCRIPT.read_text(encoding="utf-8")

    assert 'getenv "AI_ENGINEER_AUTOCAD_BRIDGE"' in source
    assert 'getenv "AI_ENGINEER_AUTOCAD_LIVE_LOG"' in source
    assert '(setvar "TRUSTEDPATHS" aieng-old-trusted)' in source
    assert "_.AIENGCONTEXT" in source
    assert "_.AIENGPALETTE" in source
    assert "AIENG_DBMOD=" in source
    assert "AIENG_TRUSTEDPATHS_RESTORED=" in source
    assert "_.QUIT\n_N" in source
    assert "QSAVE" not in source.upper()
    assert "SAVEAS" not in source.upper()


def test_live_drafting_preview_launcher_uses_disposable_read_only_drawing() -> None:
    script = LIVE_DRAFTING_PREVIEW_SCRIPT.read_text(encoding="utf-8")
    launcher = LIVE_DRAFTING_PREVIEW_LAUNCHER.read_text(encoding="utf-8")

    assert '(getenv "AI_ENGINEER_AUTOCAD_BRIDGE")' in script
    assert '(setvar "TRUSTEDPATHS" aieng-old-trusted)' in script
    assert "_.AIENGPALETTE" in script
    assert "AIENGOPERATORPULSE" not in script
    assert "AIENGCONTEXT" in script
    assert "SCRIPT_STARTED=1" in script
    assert "NETLOAD_RETURNED=1" in script
    assert "PALETTE_RETURNED=1" in script
    assert "QSAVE" not in script.upper()
    assert "SAVEAS" not in script.upper()
    assert "EXECUTE" not in script.upper()
    assert "127.0.0.1" in launcher
    assert "AI_ENGINEER_PROVIDER = 'none'" in launcher
    assert "AI_ENGINEER_OPERATOR_ROOTS = $workspace" in launcher
    assert "$operatorPathProbe.Length -ge 260" in launcher
    assert "install AI-Engineer in a shorter directory" in launcher
    assert 'GetActiveObject($autoCADProgId)' in launcher
    assert "'AutoCAD.Application.24'" in launcher
    assert "'AutoCAD.Application.24.2'" in launcher
    assert "Get-ReviewedAutoCADDocument $automation $drawing" in launcher
    assert "AutoCAD lost the reviewed preview document before DBMOD verification." in launcher
    assert "[string]::IsNullOrWhiteSpace($fullName)" in launcher
    assert "$active.Close($false)" in launcher
    assert "$automation.Documents.Open($drawing, $true)" in launcher
    assert "Preview DBMOD:" in launcher
    assert '$active.SendCommand("_.AIENGOPERATORPULSE`n")' not in launcher
    assert "AutoCAD did not publish the exact read-only operator status after startup:" in launcher
    assert "operator-pulse-error.json" in launcher
    assert "AI_ENGINEER_AUTOCAD_STARTUP_LOG" in launcher
    assert "startup-markers.log" in launcher
    assert "Remove-Item -LiteralPath $pulseErrorPath -Force" in launcher
    assert "Operator status pulse: verified" in launcher
    assert "operator --allow-project-root" not in launcher
    assert "Copy-Item -LiteralPath $source -Destination $drawing" in launcher
    assert ".IsReadOnly = $true" in launcher
    assert "[switch]$WritableScratchAcceptance" in launcher
    assert "Writable Scratch acceptance requires an exact source.dwg" in launcher
    assert "'.ai-engineer\\autocad\\runs'" in launcher
    assert ".IsReadOnly = $false" in launcher
    assert "Writable Scratch acceptance rejects a reparse-point drawing" in launcher
    assert "Writable Scratch acceptance rejects a reparse-point directory" in launcher
    assert "Get-FileHash" in launcher
    assert "live_drafting_preview.scr" in launcher
    assert "Get-Command python.exe" in launcher
    assert "Start-Process -FilePath $python -PassThru" in launcher
    assert "-NoExit" not in launcher
    assert "stop_bridge_after_autocad.ps1" in launcher
    assert "-AutoCADPid" in launcher
    assert "-BridgePid" in launcher


def test_autocad_bridge_monitor_stops_only_the_exact_python_process() -> None:
    monitor = (
        PROJECT_ROOT / "integrations" / "stop_bridge_after_autocad.ps1"
    ).read_text(encoding="utf-8")

    assert "Wait-Process -Id $AutoCADPid" in monitor
    assert "Get-Process -Id $BridgePid" in monitor
    assert "$bridge.ProcessName -like 'python*'" in monitor
    assert "Stop-Process -Id $BridgePid" in monitor
    assert "Stop-Process -Name" not in monitor


def test_manufacturer_discovery_launcher_keeps_key_out_of_autocad_process() -> None:
    source = (PROJECT_ROOT / "START-AI-ENGINEER-AUTOCAD-DISCOVERY.ps1").read_text(
        encoding="utf-8"
    )
    remove_key = source.index("Remove-Item Env:OPENAI_API_KEY")
    start_autocad = source.index("Start-Process -FilePath $acad")
    assert remove_key < start_autocad
    assert "$env:AI_ENGINEER_PROVIDER = 'none'" in source
    assert "$env:AI_ENGINEER_MANUFACTURER_DISCOVERY_ENABLE = '1'" in source
    assert "gpt-5.6-luna" in source
    assert "gpt-5.6" in source
    assert "Verify free candidate file" in source
    assert "AI_ENGINEER_MANUFACTURER_DISCOVERY_REQUEST" in source
    assert "$confirm.ToUpperInvariant() -ne 'YES'" in source
    assert "$confirm -cne 'نعم'" in source
    assert "OPENAI_API_KEY" not in source[start_autocad:source.index("Wait-Process", start_autocad)]
    assert "Stop-Process -Id $api.Id" in source
