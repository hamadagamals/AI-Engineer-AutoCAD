"""Bounded, evidence-first inspection of local manufacturer CAD assets."""

from __future__ import annotations

import hashlib
import re
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Iterable

from agent.exceptions import ManufacturerEvidenceError
from core.verified_lessons import VerifiedLessonRegistry


MAX_CAD_BYTES = 100 * 1024 * 1024
_SUPPORTED = {".stp": "step", ".step": "step", ".ste": "step", ".dwg": "dwg", ".dxf": "dxf", ".sat": "sat", ".igs": "iges", ".iges": "iges"}
_PART_TOKEN = re.compile(r"[A-Z0-9][A-Z0-9._+-]{2,63}")


@dataclass(frozen=True, slots=True)
class CadAssetObservation:
    """One immutable local CAD observation; it does not approve product use."""

    path: str
    format: str
    size_bytes: int
    sha256: str
    structurally_complete: bool
    embedded_identifiers: tuple[str, ...]
    exact_requested_matches: tuple[str, ...]
    duplicate_of_sha256: str | None
    findings: tuple[str, ...]

    def to_dict(self) -> dict[str, object]:
        return asdict(self)


def inspect_cad_assets(
    paths: Iterable[Path],
    *,
    requested_part_numbers: Iterable[str],
    lessons: VerifiedLessonRegistry | None = None,
) -> tuple[CadAssetObservation, ...]:
    """Inspect supported files and match only exact normalized part identifiers."""
    if lessons is not None:
        lessons.enforce_allowed_action(
            scope="manufacturer.cad_intake", action_code="cad.fuzzy_substitution"
        )
    requested = {_normalize_part(value) for value in requested_part_numbers}
    requested.discard("")
    if not requested:
        raise ManufacturerEvidenceError("At least one requested part number is required.")

    observations: list[CadAssetObservation] = []
    first_path_by_hash: dict[str, str] = {}
    for supplied in paths:
        path = Path(supplied).resolve()
        suffix = path.suffix.lower()
        if suffix not in _SUPPORTED:
            raise ManufacturerEvidenceError(f"Unsupported CAD asset format: {path.suffix}")
        try:
            size = path.stat().st_size
        except OSError as exc:
            raise ManufacturerEvidenceError(f"Cannot inspect CAD asset {path}: {exc}") from exc
        if size <= 0 or size > MAX_CAD_BYTES:
            raise ManufacturerEvidenceError(
                f"CAD asset size must be between 1 and {MAX_CAD_BYTES} bytes: {path}"
            )
        digest = _sha256(path)
        duplicate = digest if digest in first_path_by_hash else None
        first_path_by_hash.setdefault(digest, str(path))
        identifiers: set[str] = set()
        complete = True
        findings: list[str] = []
        if _SUPPORTED[suffix] == "step":
            complete, identifiers = _inspect_step(path)
            if not complete:
                findings.append("STEP header/footer completeness check failed.")
        identifiers.add(_normalize_part(path.stem.split("_3D", 1)[0]))
        identifiers.discard("")
        matches = tuple(sorted(requested.intersection(identifiers)))
        if not matches:
            findings.append("No exact requested part-number match; asset remains unbound.")
        if duplicate is not None:
            findings.append("Exact SHA-256 duplicate; do not register a second asset identity.")
        observations.append(
            CadAssetObservation(
                path=str(path),
                format=_SUPPORTED[suffix],
                size_bytes=size,
                sha256=digest,
                structurally_complete=complete,
                embedded_identifiers=tuple(sorted(identifiers)),
                exact_requested_matches=matches,
                duplicate_of_sha256=duplicate,
                findings=tuple(findings),
            )
        )
    return tuple(observations)


def _inspect_step(path: Path) -> tuple[bool, set[str]]:
    try:
        text = path.read_text(encoding="latin-1")
    except (OSError, UnicodeError) as exc:
        raise ManufacturerEvidenceError(f"Cannot read STEP evidence {path}: {exc}") from exc
    upper = text.upper()
    complete = upper.lstrip().startswith("ISO-10303-21;") and upper.rstrip().endswith("END-ISO-10303-21;")
    identifiers = {
        _normalize_part(token)
        for token in _PART_TOKEN.findall(upper)
        if any(character.isdigit() for character in token)
    }
    return complete, {value for value in identifiers if value}


def _normalize_part(value: object) -> str:
    if not isinstance(value, str):
        raise ManufacturerEvidenceError("Part numbers must be strings.")
    return re.sub(r"[^A-Z0-9]", "", value.upper())


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    try:
        with path.open("rb") as handle:
            while chunk := handle.read(1024 * 1024):
                digest.update(chunk)
    except OSError as exc:
        raise ManufacturerEvidenceError(f"Cannot hash CAD asset {path}: {exc}") from exc
    return digest.hexdigest()
