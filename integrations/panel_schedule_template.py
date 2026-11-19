"""Validated registry and deterministic planning for panel-schedule templates.

Workbook generation is intentionally separated from registration and planning.  The
registered source workbook is immutable and every export must be written to a new
path by the approved spreadsheet adapter.
"""

from __future__ import annotations

from dataclasses import dataclass
from hashlib import sha256
import json
from pathlib import Path
import re
from typing import Any, Iterable
from zipfile import BadZipFile, ZipFile
import xml.etree.ElementTree as ET


_INVALID_SHEET_CHARS = re.compile(r"[\\/*?:\[\]]")
_MAIN_NS = "http://schemas.openxmlformats.org/spreadsheetml/2006/main"
_REL_NS = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"
_PACKAGE_REL_NS = "http://schemas.openxmlformats.org/package/2006/relationships"


class PanelScheduleTemplateError(RuntimeError):
    """Raised when a registered schedule template or request is unsafe or invalid."""


@dataclass(frozen=True)
class SheetProfile:
    """Expected identity and structural counts for one registered worksheet."""

    name: str
    used_range: str
    formula_count: int
    merge_count: int


@dataclass(frozen=True)
class TemplateRegistration:
    """Immutable identity and workbook conventions of a registered template."""

    template_id: str
    workbook_path: Path
    workbook_sha256: str
    panel_name_cell: str
    default_exemplar_sheet: str
    sheet_name_limit: int
    sheets: tuple[SheetProfile, ...]
    generation_policy: dict[str, Any]


@dataclass(frozen=True)
class GenerationPanel:
    """One requested output worksheet and its source exemplar."""

    panel_id: str
    output_sheet_name: str
    exemplar_sheet: str
    panel_name_cell: str


@dataclass(frozen=True)
class GenerationPlan:
    """Hash-bound plan consumed by a separately verified workbook adapter."""

    template_id: str
    template_sha256: str
    panels: tuple[GenerationPanel, ...]

    def as_dict(self) -> dict[str, Any]:
        """Return a stable JSON-serializable representation."""

        return {
            "template_id": self.template_id,
            "template_sha256": self.template_sha256,
            "panels": [
                {
                    "panel_id": panel.panel_id,
                    "output_sheet_name": panel.output_sheet_name,
                    "exemplar_sheet": panel.exemplar_sheet,
                    "panel_name_cell": panel.panel_name_cell,
                }
                for panel in self.panels
            ],
        }


def template_manifest_path(project_root: Path) -> Path:
    """Resolve the built-in template manifest without hard-coding the repository."""

    return (
        project_root.resolve()
        / "data"
        / "templates"
        / "panel-schedules"
        / "panels-sch-8-station.template.json"
    )


def load_registration(project_root: Path) -> TemplateRegistration:
    """Load and validate the declarative registration metadata."""

    manifest_path = template_manifest_path(project_root)
    try:
        payload = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise PanelScheduleTemplateError(
            f"Panel-schedule template manifest cannot be read: {manifest_path}"
        ) from exc

    required = {
        "template_id",
        "workbook_file",
        "workbook_sha256",
        "panel_name_cell",
        "default_exemplar_sheet",
        "sheet_name_limit",
        "sheets",
        "generation_policy",
    }
    missing = sorted(required.difference(payload))
    if missing:
        raise PanelScheduleTemplateError(
            f"Panel-schedule template manifest is missing: {', '.join(missing)}"
        )

    workbook_name = str(payload["workbook_file"])
    if Path(workbook_name).name != workbook_name:
        raise PanelScheduleTemplateError("Template workbook_file must be a file name only.")

    profiles = tuple(
        SheetProfile(
            name=str(item["name"]),
            used_range=str(item["used_range"]),
            formula_count=int(item["formula_count"]),
            merge_count=int(item["merge_count"]),
        )
        for item in payload["sheets"]
    )
    if not profiles:
        raise PanelScheduleTemplateError("Template registration contains no worksheets.")

    registration = TemplateRegistration(
        template_id=str(payload["template_id"]),
        workbook_path=manifest_path.parent / workbook_name,
        workbook_sha256=str(payload["workbook_sha256"]).lower(),
        panel_name_cell=str(payload["panel_name_cell"]),
        default_exemplar_sheet=str(payload["default_exemplar_sheet"]),
        sheet_name_limit=int(payload["sheet_name_limit"]),
        sheets=profiles,
        generation_policy=dict(payload["generation_policy"]),
    )
    if registration.default_exemplar_sheet not in {item.name for item in profiles}:
        raise PanelScheduleTemplateError("Default exemplar sheet is not registered.")
    return registration


def validate_registration(project_root: Path) -> TemplateRegistration:
    """Verify template bytes and structural worksheet invariants."""

    registration = load_registration(project_root)
    try:
        workbook_bytes = registration.workbook_path.read_bytes()
    except OSError as exc:
        raise PanelScheduleTemplateError(
            f"Registered workbook cannot be read: {registration.workbook_path}"
        ) from exc
    observed_hash = sha256(workbook_bytes).hexdigest()
    if observed_hash != registration.workbook_sha256:
        raise PanelScheduleTemplateError(
            "Registered workbook SHA-256 does not match its manifest."
        )

    try:
        with ZipFile(registration.workbook_path) as archive:
            observed = _inspect_workbook(archive, registration.panel_name_cell)
    except (BadZipFile, KeyError, ET.ParseError) as exc:
        raise PanelScheduleTemplateError("Registered workbook is not valid OOXML.") from exc

    expected = {
        item.name: (item.used_range, item.formula_count, item.merge_count)
        for item in registration.sheets
    }
    if observed != expected:
        raise PanelScheduleTemplateError(
            "Registered workbook structure differs from the reviewed manifest."
        )
    return registration


def build_generation_plan(
    project_root: Path,
    panel_ids: Iterable[str],
    *,
    exemplar_sheet: str | None = None,
) -> GenerationPlan:
    """Build a deterministic, non-mutating plan for one sheet per panel."""

    registration = validate_registration(project_root)
    exemplar = exemplar_sheet or registration.default_exemplar_sheet
    if exemplar not in {item.name for item in registration.sheets}:
        raise PanelScheduleTemplateError(f"Unknown exemplar worksheet: {exemplar}")

    panels: list[GenerationPanel] = []
    seen: set[str] = set()
    for raw_panel_id in panel_ids:
        panel_id = str(raw_panel_id).strip()
        if not panel_id:
            raise PanelScheduleTemplateError("Panel identifiers cannot be blank.")
        output_name = _safe_sheet_name(panel_id, registration.sheet_name_limit)
        folded = output_name.casefold()
        if folded in seen:
            raise PanelScheduleTemplateError(
                f"Panel identifiers produce a duplicate worksheet name: {output_name}"
            )
        seen.add(folded)
        panels.append(
            GenerationPanel(
                panel_id=panel_id,
                output_sheet_name=output_name,
                exemplar_sheet=exemplar,
                panel_name_cell=registration.panel_name_cell,
            )
        )
    if not panels:
        raise PanelScheduleTemplateError("At least one panel is required.")

    return GenerationPlan(
        template_id=registration.template_id,
        template_sha256=registration.workbook_sha256,
        panels=tuple(panels),
    )


def _safe_sheet_name(panel_id: str, limit: int) -> str:
    if _INVALID_SHEET_CHARS.search(panel_id):
        raise PanelScheduleTemplateError(
            f"Panel identifier contains an Excel worksheet character: {panel_id}"
        )
    if panel_id.startswith("'") or panel_id.endswith("'"):
        raise PanelScheduleTemplateError("Worksheet names cannot start or end with apostrophe.")
    if len(panel_id) > limit:
        raise PanelScheduleTemplateError(
            f"Panel identifier exceeds the {limit}-character worksheet limit: {panel_id}"
        )
    return panel_id


def _inspect_workbook(archive: ZipFile, panel_name_cell: str) -> dict[str, tuple[str, int, int]]:
    workbook = ET.fromstring(archive.read("xl/workbook.xml"))
    relationships = ET.fromstring(archive.read("xl/_rels/workbook.xml.rels"))
    targets = {
        item.attrib["Id"]: item.attrib["Target"]
        for item in relationships.findall(f"{{{_PACKAGE_REL_NS}}}Relationship")
    }
    result: dict[str, tuple[str, int, int]] = {}
    for sheet in workbook.findall(f".//{{{_MAIN_NS}}}sheet"):
        name = sheet.attrib["name"]
        relationship_id = sheet.attrib[f"{{{_REL_NS}}}id"]
        target = targets[relationship_id].lstrip("/")
        entry_name = target if target.startswith("xl/") else f"xl/{target}"
        worksheet = ET.fromstring(archive.read(entry_name))
        dimension = worksheet.find(f"{{{_MAIN_NS}}}dimension")
        used_range = dimension.attrib.get("ref", "") if dimension is not None else ""
        formulas = len(worksheet.findall(f".//{{{_MAIN_NS}}}f"))
        merges = len(worksheet.findall(f".//{{{_MAIN_NS}}}mergeCell"))
        if worksheet.find(f".//{{{_MAIN_NS}}}c[@r='{panel_name_cell}']") is None:
            raise PanelScheduleTemplateError(
                f"Worksheet {name} has no panel-name cell {panel_name_cell}."
            )
        result[name] = (used_range, formulas, merges)
    return result
