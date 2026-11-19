"""Strict loader for the bounded read-only inventory exported by Revit."""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path

from agent.exceptions import RevitIntegrationError


MAXIMUM_INVENTORY_BYTES = 1_048_576
MAXIMUM_COUNT_KEYS = 64


@dataclass(frozen=True, slots=True)
class RevitInventory:
    captured_utc: str
    revit_version: str
    status: str
    document_present: bool
    document_title: str
    is_family_document: bool
    is_workshared: bool
    active_view_name: str
    active_view_type: str
    counts: dict[str, int]
    provider_egress_performed: bool
    document_mutation_performed: bool
    limitations: tuple[str, ...]


def default_inventory_root() -> Path:
    """Return the per-user local Revit bridge directory."""
    import os

    value = os.environ.get("LOCALAPPDATA")
    if not value:
        raise RevitIntegrationError("LOCALAPPDATA is required for the Revit bridge.")
    return Path(value) / "AI-Engineer" / "revit"


def load_revit_inventory(path: Path, *, allowed_root: Path | None = None) -> RevitInventory:
    """Load one inventory only from its configured local bridge root."""
    root = (allowed_root or default_inventory_root()).expanduser().resolve(strict=True)
    candidate = path.expanduser().resolve(strict=True)
    if not root.is_dir() or not candidate.is_file() or candidate.is_symlink():
        raise RevitIntegrationError("Revit inventory must be a regular file in the bridge root.")
    if not candidate.is_relative_to(root):
        raise RevitIntegrationError("Revit inventory escaped the configured bridge root.")
    if candidate.stat().st_size > MAXIMUM_INVENTORY_BYTES:
        raise RevitIntegrationError("Revit inventory exceeds the one MiB safety limit.")
    try:
        value = json.loads(candidate.read_text(encoding="utf-8-sig"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise RevitIntegrationError(f"Revit inventory is not valid UTF-8 JSON: {exc}") from exc
    if not isinstance(value, dict) or value.get("schema_version") != 1:
        raise RevitIntegrationError("Revit inventory schema_version must be 1.")
    if value.get("command_name") != "AIENGINEER_REVIT_READONLY_INVENTORY":
        raise RevitIntegrationError("Revit inventory command identity is invalid.")
    counts = value.get("counts")
    if not isinstance(counts, dict) or len(counts) > MAXIMUM_COUNT_KEYS:
        raise RevitIntegrationError("Revit inventory counts are missing or exceed the safe limit.")
    if any(
        not isinstance(key, str) or not key or len(key) > 64
        or not isinstance(count, int) or isinstance(count, bool) or count < 0
        for key, count in counts.items()
    ):
        raise RevitIntegrationError("Revit inventory counts contain an invalid key or value.")
    if value.get("provider_egress_performed") is not False:
        raise RevitIntegrationError("Revit inventory unexpectedly reports provider egress.")
    if value.get("document_mutation_performed") is not False:
        raise RevitIntegrationError("Revit inventory unexpectedly reports document mutation.")
    limitations = value.get("limitations")
    if not isinstance(limitations, list) or not all(isinstance(item, str) for item in limitations):
        raise RevitIntegrationError("Revit inventory limitations are invalid.")

    def text(name: str, maximum: int = 260) -> str:
        item = value.get(name)
        if not isinstance(item, str) or len(item) > maximum:
            raise RevitIntegrationError(f"Revit inventory field {name} is invalid.")
        return item

    def boolean(name: str) -> bool:
        item = value.get(name)
        if not isinstance(item, bool):
            raise RevitIntegrationError(f"Revit inventory field {name} is invalid.")
        return item

    return RevitInventory(
        text("captured_utc", 64),
        text("revit_version", 32),
        text("status", 64),
        boolean("document_present"),
        text("document_title"),
        boolean("is_family_document"),
        boolean("is_workshared"),
        text("active_view_name"),
        text("active_view_type", 64),
        dict(sorted(counts.items())),
        False,
        False,
        tuple(limitations),
    )
