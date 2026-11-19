"""Project specification intake and compliance matching engine for CAD/BIM models.

Enables the engineer to upload/attach project specifications (PDF/Excel text/JSON)
to enforce project-specific rules:
- Permissible voltage drop limits (lighting, power, feeders).
- Conductor specifications (Cu/XLPE/PVC/LSZH).
- Ambient temperature & derating factors.
- Approved vendor lists (AVL) for switchgear, cables, luminaires, and wiring devices.
"""

from __future__ import annotations

import re
from dataclasses import dataclass, field
from enum import Enum
from typing import Any, Mapping, Sequence


class ComplianceStatus(str, Enum):
    COMPLIANT = "compliant"
    NON_COMPLIANT = "non_compliant"
    REQUIRES_REVIEW = "requires_review"


@dataclass(frozen=True, slots=True)
class ProjectSpecification:
    """Structured project electrical specifications extracted from tender/client documents."""

    project_name: str
    nominal_voltage_three_phase: float = 400.0  # V
    nominal_voltage_single_phase: float = 230.0  # V
    frequency_hz: float = 50.0  # Hz
    ambient_temperature_celsius: float = 40.0
    max_voltage_drop_lighting_percent: float = 3.0
    max_voltage_drop_power_percent: float = 4.0
    max_voltage_drop_total_percent: float = 5.0
    approved_cable_manufacturers: tuple[str, ...] = (
        "ELSEWEDY", "RIYADH CABLES", "SAUDI CABLE", "DUBAI CABLE", "JEDDAH CABLES"
    )
    approved_panel_manufacturers: tuple[str, ...] = (
        "SCHNEIDER ELECTRIC", "ABB", "SIEMENS", "EATON", "LEGRAND"
    )
    approved_luminaire_manufacturers: tuple[str, ...] = (
        "PHILIPS", "SIGNIFY", "ZUMTOBEL", "OSRAM", "THORN", "COOPER"
    )
    required_cable_insulation: str = "XLPE"
    low_smoke_zero_halogen_required: bool = True
    spec_notes: tuple[str, ...] = ()


@dataclass(frozen=True, slots=True)
class SpecComplianceFinding:
    """One deterministic check against the active project specification."""

    parameter_name: str
    actual_value: Any
    allowed_value: Any
    status: ComplianceStatus
    spec_clause_reference: str
    explanation_ar: str
    explanation_en: str


class ProjectSpecMatcher:
    """Intake, parser, and compliance validator for project specifications."""

    def __init__(self, spec: ProjectSpecification | None = None) -> None:
        self.spec = spec or ProjectSpecification(project_name="Standard Project Baseline")

    @classmethod
    def parse_from_text(cls, raw_text: str, project_name: str = "Uploaded Project Spec") -> "ProjectSpecMatcher":
        """Extract structured parameters from uploaded spec text using bounded patterns."""
        notes: list[str] = []
        voltage_drop_lighting = 3.0
        voltage_drop_power = 4.0
        ambient_temp = 40.0
        lszh = True

        # Extract lighting voltage drop (handling either order: lighting ... voltage drop OR voltage drop ... lighting)
        vdrop_lgt_match = re.search(
            r"(?:lighting|إنارة|انارة).{0,50}?(?:voltage\s+drop|هبوط\s+الجهد).{0,40}?(\d+(?:\.\d+)?)\s*%"
            r"|(?:voltage\s+drop|هبوط\s+الجهد).{0,50}?(?:lighting|إنارة|انارة).{0,40}?(\d+(?:\.\d+)?)\s*%",
            raw_text,
            re.IGNORECASE,
        )
        if vdrop_lgt_match:
            try:
                val_str = vdrop_lgt_match.group(1) or vdrop_lgt_match.group(2)
                voltage_drop_lighting = float(val_str)
                notes.append(f"Parsed lighting voltage drop limit: {voltage_drop_lighting}%")
            except ValueError:
                pass

        # Extract power voltage drop
        vdrop_pwr_match = re.search(
            r"(?:power|sub-feeder|feeder|قوى|مخارج).{0,50}?(?:voltage\s+drop|هبوط\s+الجهد).{0,40}?(\d+(?:\.\d+)?)\s*%"
            r"|(?:voltage\s+drop|هبوط\s+الجهد).{0,50}?(?:power|sub-feeder|feeder|قوى|مخارج).{0,40}?(\d+(?:\.\d+)?)\s*%",
            raw_text,
            re.IGNORECASE,
        )
        if vdrop_pwr_match:
            try:
                val_str = vdrop_pwr_match.group(1) or vdrop_pwr_match.group(2)
                voltage_drop_power = float(val_str)
                notes.append(f"Parsed power voltage drop limit: {voltage_drop_power}%")
            except ValueError:
                pass

        # Extract ambient temperature e.g. "ambient temperature: 45 C" or "درجة الحرارة 50"
        temp_match = re.search(r"(?:ambient|درجة\s+الحرارة|حرارة).{0,60}?(\d{2})\s*(?:°?C|مئوية)?", raw_text, re.IGNORECASE)
        if temp_match:
            try:
                val = float(temp_match.group(1))
                if 25.0 <= val <= 60.0:
                    ambient_temp = val
                    notes.append(f"Parsed ambient temperature: {ambient_temp}°C")
            except ValueError:
                pass

        # Check for LSZH / Low Smoke
        if re.search(r"(?:LSZH|LS0H|Low\s+Smoke|خالي\s+من\s+الهالوجين)", raw_text, re.IGNORECASE):
            lszh = True
            notes.append("Low Smoke Zero Halogen (LSZH) specified.")

        spec = ProjectSpecification(
            project_name=project_name,
            ambient_temperature_celsius=ambient_temp,
            max_voltage_drop_lighting_percent=voltage_drop_lighting,
            max_voltage_drop_power_percent=voltage_drop_power,
            low_smoke_zero_halogen_required=lszh,
            spec_notes=tuple(notes),
        )
        return cls(spec)

    def verify_voltage_drop(
        self,
        circuit_type: str,
        calculated_voltage_drop_percent: float,
        circuit_tag: str = "Circuit",
    ) -> SpecComplianceFinding:
        """Verify whether a circuit's calculated voltage drop meets project specifications."""
        is_lighting = any(k in circuit_type.lower() for k in ("light", "إنارة", "انارة", "lgt"))
        max_allowed = (
            self.spec.max_voltage_drop_lighting_percent
            if is_lighting
            else self.spec.max_voltage_drop_power_percent
        )

        status = (
            ComplianceStatus.COMPLIANT
            if calculated_voltage_drop_percent <= max_allowed
            else ComplianceStatus.NON_COMPLIANT
        )

        explanation_ar = (
            f"الدائرة '{circuit_tag}' تحقق حد هبوط الجهد المسموح ({calculated_voltage_drop_percent:.2f}% <= {max_allowed}%)."
            if status == ComplianceStatus.COMPLIANT
            else f"الدائرة '{circuit_tag}' تتجاوز حد هبوط الجهد المسموح بمواصفة المشروع ({calculated_voltage_drop_percent:.2f}% > {max_allowed}%). يلزم زيادة مقاس الكابل."
        )

        explanation_en = (
            f"Circuit '{circuit_tag}' voltage drop is compliant ({calculated_voltage_drop_percent:.2f}% <= {max_allowed}%)."
            if status == ComplianceStatus.COMPLIANT
            else f"Circuit '{circuit_tag}' exceeds project limit ({calculated_voltage_drop_percent:.2f}% > {max_allowed}%). Upsizing required."
        )

        return SpecComplianceFinding(
            parameter_name=f"{circuit_type} Voltage Drop",
            actual_value=calculated_voltage_drop_percent,
            allowed_value=max_allowed,
            status=status,
            spec_clause_reference="Project Specs - Section 16120 / Electrical Calculations",
            explanation_ar=explanation_ar,
            explanation_en=explanation_en,
        )

    def verify_manufacturer(
        self,
        equipment_category: str,
        manufacturer_name: str,
    ) -> SpecComplianceFinding:
        """Verify whether proposed manufacturer is on the Approved Vendor List (AVL)."""
        cat_lower = equipment_category.lower()
        if "panel" in cat_lower or "switchgear" in cat_lower or "لوح" in cat_lower:
            avl = self.spec.approved_panel_manufacturers
        elif "cable" in cat_lower or "wire" in cat_lower or "كابل" in cat_lower:
            avl = self.spec.approved_cable_manufacturers
        elif "light" in cat_lower or "luminaire" in cat_lower or "إنارة" in cat_lower:
            avl = self.spec.approved_luminaire_manufacturers
        else:
            avl = ()

        mfg_upper = manufacturer_name.strip().upper()
        is_approved = any(mfg_upper in item or item in mfg_upper for item in avl)

        status = ComplianceStatus.COMPLIANT if is_approved else ComplianceStatus.REQUIRES_REVIEW

        explanation_ar = (
            f"المصنّع '{manufacturer_name}' معتمد ضمن قائمة الموردين المعتمدين (AVL) للمشروع."
            if is_approved
            else f"المصنّع '{manufacturer_name}' غير مدرج صراحة في قائمة المعتمدين؛ يلزم تقديم اعتماد مواد (Material Submittal)."
        )

        explanation_en = (
            f"Manufacturer '{manufacturer_name}' is listed on the Project Approved Vendor List (AVL)."
            if is_approved
            else f"Manufacturer '{manufacturer_name}' is not in AVL; requires consultant material submittal."
        )

        return SpecComplianceFinding(
            parameter_name=f"{equipment_category} Manufacturer",
            actual_value=manufacturer_name,
            allowed_value=", ".join(avl) if avl else "Consultant Approval Required",
            status=status,
            spec_clause_reference="Project Specs - Section 16010 / Approved Manufacturers List",
            explanation_ar=explanation_ar,
            explanation_en=explanation_en,
        )
