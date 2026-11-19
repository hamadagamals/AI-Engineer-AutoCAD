"""Create a hash-bound local proposal for live read-only preview acceptance."""

from __future__ import annotations

import argparse
import hashlib
import json
import uuid
from datetime import UTC, datetime, timedelta
from pathlib import Path

from core.electrical_drafting import (
    ProposedCircuit,
    ProposedDevice,
    ProposedLayer,
    ProposedRoute,
    ProposedTerminal,
    create_drafting_proposal,
    create_source_model_binding,
)
from agent.exceptions import DraftingContractError
from core.electrical_drafting_validation import validate_drafting_proposal
from core.file_manager import FileManager
from integrations.autocad_context import AutoCADContextService
from integrations.electrical_symbols import ElectricalSymbolCatalog


PROJECT_ROOT = Path(__file__).resolve().parents[1]
CATALOG_PATH = PROJECT_ROOT / "plugins" / "electrical_symbols" / "catalog.json"


def build_acceptance_proposal(
    *,
    workspace_root: Path,
    context_id: str,
    evidence_sha256: str,
    output_path: Path,
    start: tuple[float, float, float] = (0.0, 0.0, 0.0),
    end: tuple[float, float, float] = (1000.0, 0.0, 0.0),
    confirm_overwrite: bool = False,
) -> Path:
    """Create one two-device connected proposal from exact local context evidence."""
    workspace = workspace_root.resolve(strict=True)
    files = FileManager(
        [workspace],
        PROJECT_ROOT / "backup" / "drafting-preview-acceptance",
        max_read_bytes=4_194_304,
    )
    context = AutoCADContextService(files, CATALOG_PATH).import_context(
        context_id=context_id,
        evidence_sha256=evidence_sha256,
        confirmed=True,
    )
    catalog = ElectricalSymbolCatalog.load(CATALOG_PATH)
    symbol = catalog.get("socket_single")
    catalog_sha256 = hashlib.sha256(CATALOG_PATH.read_bytes()).hexdigest()
    binding = create_source_model_binding(
        context.model,
        context_evidence_sha256=evidence_sha256,
        symbol_catalog_sha256=catalog_sha256,
    )

    existing_layer = next(
        (
            layer
            for layer in context.model.layers
            if layer.name.casefold() == symbol.insertion_layer.casefold()
        ),
        None,
    )
    suffix = uuid.uuid4().hex[:12]
    layer_id = (
        existing_layer.layer_id
        if existing_layer is not None
        else f"layer:preview-power-{suffix}"
    )
    layer = ProposedLayer(
        layer_id,
        symbol.insertion_layer,
        symbol.layer_color,
        symbol.linetype,
        existing_layer is None,
    )
    existing_wire_layer = next(
        (
            item
            for item in context.model.layers
            if item.name.casefold() == "e-wire"
        ),
        None,
    )
    wire_layer_id = (
        existing_wire_layer.layer_id
        if existing_wire_layer is not None
        else f"layer:preview-wire-{suffix}"
    )
    wire_layer = ProposedLayer(
        wire_layer_id,
        "E-WIRE",
        3,
        "CONTINUOUS",
        existing_wire_layer is None,
    )
    source_id = f"device:preview-source-{suffix}"
    load_id = f"device:preview-load-{suffix}"
    source_terminal_id = f"terminal:preview-source-l-{suffix}"
    load_terminal_id = f"terminal:preview-load-l-{suffix}"
    circuit_id = f"circuit:preview-power-{suffix}"
    route_id = f"route:preview-power-{suffix}"
    midpoint = (
        (start[0] + end[0]) / 2.0,
        (start[1] + end[1]) / 2.0,
        (start[2] + end[2]) / 2.0,
    )
    devices = (
        ProposedDevice(
            source_id,
            symbol.symbol_id,
            symbol.definition_sha256,
            symbol.block_name,
            layer_id,
            start,
            0.0,
            (1.0, 1.0, 1.0),
            (),
        ),
        ProposedDevice(
            load_id,
            symbol.symbol_id,
            symbol.definition_sha256,
            symbol.block_name,
            layer_id,
            end,
            90.0,
            (1.0, 1.0, 1.0),
            (),
        ),
    )
    terminals = (
        ProposedTerminal(source_terminal_id, source_id, "L", start),
        ProposedTerminal(load_terminal_id, load_id, "L", end),
    )
    circuit = ProposedCircuit(
        circuit_id,
        "Preview power circuit",
        (source_terminal_id, load_terminal_id),
        (route_id,),
    )
    route = ProposedRoute(
        route_id,
        circuit_id,
        wire_layer_id,
        source_terminal_id,
        load_terminal_id,
        (start, midpoint, end),
    )
    now = datetime.now(UTC)
    proposal = create_drafting_proposal(
        proposal_id=uuid.uuid4().hex,
        binding=binding,
        layers=(layer, wire_layer),
        devices=devices,
        terminals=terminals,
        circuits=(circuit,),
        routes=(route,),
        input_issues=(),
        created_at=_timestamp(now),
        expires_at=_timestamp(now + timedelta(minutes=90)),
    )
    validation = validate_drafting_proposal(
        proposal,
        model=context.model,
        symbol_catalog_path=CATALOG_PATH,
    )
    if not validation.preview_allowed or validation.findings:
        codes = ", ".join(item.code for item in validation.findings)
        raise DraftingContractError(
            "Acceptance proposal did not pass deterministic preview validation: "
            + (codes or "preview was not allowed")
        )
    target = files.resolve_path(output_path)
    payload = json.dumps(proposal.to_dict(), ensure_ascii=False, indent=2) + "\n"
    files.write_text(
        target,
        payload,
        confirm_overwrite=confirm_overwrite,
        create_parents=True,
    )
    return target


def main() -> int:
    """Parse explicit acceptance inputs and print only the created local path."""
    parser = argparse.ArgumentParser()
    parser.add_argument("--workspace-root", required=True, type=Path)
    parser.add_argument("--context-id", required=True)
    parser.add_argument("--evidence-sha256", required=True)
    parser.add_argument(
        "--output",
        type=Path,
        default=Path("drafting-preview") / "reviewed-proposal.json",
    )
    parser.add_argument("--overwrite", action="store_true")
    arguments = parser.parse_args()
    target = build_acceptance_proposal(
        workspace_root=arguments.workspace_root,
        context_id=arguments.context_id,
        evidence_sha256=arguments.evidence_sha256,
        output_path=arguments.output,
        confirm_overwrite=arguments.overwrite,
    )
    print(target)
    return 0


def _timestamp(value: datetime) -> str:
    return value.astimezone(UTC).isoformat().replace("+00:00", "Z")


if __name__ == "__main__":
    raise SystemExit(main())
