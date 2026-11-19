"""Reviewed, hash-bound CSV export for one deterministic quantity report."""

from __future__ import annotations

import csv
import hashlib
import io
import re
from dataclasses import dataclass

from agent.exceptions import QuantityTakeoffError
from core.electrical_model import JsonRecord
from core.quantity_takeoff import TakeoffReport


_SHA256 = re.compile(r"[0-9a-f]{64}")
_HEADER = (
    "record_type",
    "item_id",
    "classification",
    "quantity",
    "unit",
    "geometric_length_meters",
    "vertical_allowance_meters",
    "termination_allowance_meters",
    "waste_meters",
    "calculated_total_meters",
    "reviewed_override_meters",
    "final_total_meters",
    "override_reason",
    "override_reviewed_by",
    "source_document_id",
    "source_object_ids",
    "context_id",
    "context_evidence_sha256",
    "report_sha256",
    "calculation_version",
    "report_complete",
)
_MAXIMUM_ROWS = 8192
_MAXIMUM_BYTES = 1_048_576


@dataclass(frozen=True, slots=True)
class ReviewedCsvExport(JsonRecord):
    """One bounded CSV artifact produced only for an exact reviewed report hash."""

    schema_version: int
    format: str
    media_type: str
    encoding: str
    report_sha256: str
    content_sha256: str
    byte_count: int
    row_count: int
    spreadsheet_formula_escaping: bool
    drawing_mutation_performed: bool
    file_write_performed: bool
    content: str

    def __post_init__(self) -> None:
        if self.schema_version != 1 or self.format != "csv":
            raise QuantityTakeoffError("Quantity export schema or format is unsupported.")
        if self.media_type != "text/csv; charset=utf-8" or self.encoding != "utf-8-sig":
            raise QuantityTakeoffError("Quantity CSV media type or encoding is invalid.")
        if _SHA256.fullmatch(self.report_sha256) is None:
            raise QuantityTakeoffError("Quantity export report hash must be SHA-256.")
        if _SHA256.fullmatch(self.content_sha256) is None:
            raise QuantityTakeoffError("Quantity export content hash must be SHA-256.")
        if (
            not isinstance(self.row_count, int)
            or isinstance(self.row_count, bool)
            or not 0 <= self.row_count <= _MAXIMUM_ROWS
        ):
            raise QuantityTakeoffError("Quantity export row count is invalid.")
        if (
            not isinstance(self.byte_count, int)
            or isinstance(self.byte_count, bool)
            or not 1 <= self.byte_count <= _MAXIMUM_BYTES
        ):
            raise QuantityTakeoffError("Quantity export byte count is invalid.")
        if self.spreadsheet_formula_escaping is not True:
            raise QuantityTakeoffError("Quantity CSV must enable spreadsheet formula escaping.")
        if self.drawing_mutation_performed is not False or self.file_write_performed is not False:
            raise QuantityTakeoffError("Quantity CSV export cannot claim a drawing or file write.")
        if not isinstance(self.content, str) or not self.content.startswith("\ufeff"):
            raise QuantityTakeoffError("Quantity CSV content must contain a UTF-8 BOM.")
        encoded = self.content.encode("utf-8")
        if len(encoded) != self.byte_count:
            raise QuantityTakeoffError("Quantity CSV byte count does not match its content.")
        if hashlib.sha256(encoded).hexdigest() != self.content_sha256:
            raise QuantityTakeoffError("Quantity CSV SHA-256 does not match its content.")


def create_reviewed_csv_export(
    report: TakeoffReport,
    *,
    reviewed_report_sha256: str,
) -> ReviewedCsvExport:
    """Return deterministic CSV only when the exact report hash was reviewed."""
    if not isinstance(report, TakeoffReport):
        raise QuantityTakeoffError("Quantity CSV export requires a typed report.")
    if _SHA256.fullmatch(reviewed_report_sha256) is None:
        raise QuantityTakeoffError("Reviewed quantity report hash must be SHA-256.")
    if report.report_sha256 != reviewed_report_sha256:
        raise QuantityTakeoffError(
            "Reviewed quantity report hash does not match the recalculated report."
        )

    rows = _rows(report)
    if len(rows) > _MAXIMUM_ROWS:
        raise QuantityTakeoffError("Quantity CSV exceeds its row limit.")
    stream = io.StringIO(newline="")
    stream.write("\ufeff")
    writer = csv.writer(stream, lineterminator="\r\n")
    writer.writerow(_HEADER)
    writer.writerows(rows)
    content = stream.getvalue()
    encoded = content.encode("utf-8")
    if len(encoded) > _MAXIMUM_BYTES:
        raise QuantityTakeoffError("Quantity CSV exceeds its byte limit.")
    return ReviewedCsvExport(
        schema_version=1,
        format="csv",
        media_type="text/csv; charset=utf-8",
        encoding="utf-8-sig",
        report_sha256=report.report_sha256,
        content_sha256=hashlib.sha256(encoded).hexdigest(),
        byte_count=len(encoded),
        row_count=len(rows),
        spreadsheet_formula_escaping=True,
        drawing_mutation_performed=False,
        file_write_performed=False,
        content=content,
    )


def _rows(report: TakeoffReport) -> tuple[tuple[object, ...], ...]:
    common = (
        report.context_id,
        report.context_evidence_sha256,
        report.report_sha256,
        report.calculation_version,
        str(report.is_complete).lower(),
    )
    rows: list[tuple[object, ...]] = []
    for quantity in report.device_quantities:
        rows.append(
            (
                "device",
                quantity.symbol_id,
                _cell(quantity.block_name),
                quantity.quantity,
                quantity.unit.value,
                "", "", "", "", "", "", "", "", "",
                quantity.sources[0].document_id,
                ";".join(source.object_id for source in quantity.sources),
                *common,
            )
        )
    overrides = {item.route_id: item for item in report.policy.route_overrides}
    for quantity in report.route_quantities:
        override = overrides.get(quantity.route_id)
        rows.append(
            (
                "route",
                quantity.route_id,
                quantity.calculation_rule,
                quantity.final_total_meters,
                quantity.output_unit.value,
                quantity.geometric_length_meters,
                quantity.vertical_allowance_meters,
                quantity.termination_allowance_meters,
                quantity.waste_meters,
                quantity.calculated_total_meters,
                quantity.reviewed_override_meters if quantity.reviewed_override_meters is not None else "",
                quantity.final_total_meters,
                _cell(override.reason) if override is not None else "",
                _cell(override.reviewed_by) if override is not None else "",
                quantity.source.document_id,
                quantity.source.object_id,
                *common,
            )
        )
    for unknown_id in report.unknown_object_ids:
        rows.append(
            (
                "unknown", unknown_id, "unclassified", "", "",
                "", "", "", "", "", "", "", "", "", "", "",
                *common,
            )
        )
    return tuple(rows)


def _cell(value: str) -> str:
    """Prevent spreadsheet applications from interpreting reviewed text as a formula."""
    if value and value[0] in "=+-@\t\r":
        return "'" + value
    return value
