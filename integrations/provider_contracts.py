"""Shared strict provider contracts for local, Codex, and hosted reasoning adapters."""

from __future__ import annotations

import json
from typing import Any

from agent.copilot import CopilotLanguage
from agent.copilot_provider import CopilotProviderRequest
from agent.provider import ProviderPhase, ProviderRequest


def developer_json_schema(phase: ProviderPhase) -> dict[str, Any]:
    """Return the exact JSON Schema accepted for one developer-agent phase."""
    if phase is ProviderPhase.PLAN:
        return {
            "type": "object",
            "properties": {
                "summary": {"type": "string", "minLength": 1},
                "steps": {
                    "type": "array",
                    "minItems": 1,
                    "maxItems": 20,
                    "items": {"type": "string", "minLength": 1},
                },
            },
            "required": ["summary", "steps"],
            "additionalProperties": False,
        }
    if phase is ProviderPhase.ACT:
        return {
            "oneOf": [
                {
                    "type": "object",
                    "properties": {
                        "kind": {"const": "command"},
                        "command_name": {"type": "string", "minLength": 1},
                        "payload": {"type": "object"},
                        "rationale": {"type": "string", "minLength": 1},
                    },
                    "required": ["kind", "command_name", "payload", "rationale"],
                    "additionalProperties": False,
                },
                {
                    "type": "object",
                    "properties": {
                        "kind": {"const": "complete"},
                        "summary": {"type": "string", "minLength": 1},
                    },
                    "required": ["kind", "summary"],
                    "additionalProperties": False,
                },
            ]
        }
    return {
        "type": "object",
        "properties": {
            "accepted": {"type": "boolean"},
            "reason": {"type": "string", "minLength": 1},
        },
        "required": ["accepted", "reason"],
        "additionalProperties": False,
    }


def developer_instructions(request: ProviderRequest) -> str:
    """Build a provider-neutral, execution-safe instruction for a developer request."""
    base = (
        "You are the reasoning provider behind AI-Engineer, a local-first engineering and "
        "software-development agent. AI-Engineer owns permissions, approvals, command "
        "validation, execution, backups, and verification. You are reasoning-only. Never "
        "claim an action ran unless the supplied observation says it ran. Use only command "
        "names present in available_commands. Every available command includes a payload_contract; "
        "follow it exactly and never invent missing payload fields. Relative workspace paths are "
        "preferred; for project.scan, payload {\"path\":\".\"} means the current configured "
        "workspace root. recent_history contains bounded evidence from already executed actions. "
        "Return only one JSON object matching the required schema, with no markdown. "
    )
    if request.phase is ProviderPhase.PLAN:
        return base + (
            "For PLAN, propose a bounded safe plan. Prefer inspection and verification before "
            "mutation, and explicitly account for approvals before sensitive work. When a plan "
            "step directly invokes a tool, write that step as the exact command name only so the "
            "reviewed plan can constrain later execution."
        )
    if request.phase is ProviderPhase.ACT:
        return base + (
            "For ACT, either propose exactly one available command with a payload matching its "
            "payload_contract and a rationale, or complete only when prior verified evidence shows "
            "the reviewed plan and user goal are materially satisfied. Treat recent_history as "
            "completed-action memory: never repeat an unchanged successful action unless a different "
            "verified action materially changed its inputs. If observation.kind is policy_feedback, "
            "the exact blocked_command_name plus blocked_payload combination is forbidden for this "
            "response. Do not emit that combination again. Choose a different useful command, or keep "
            "the command but change its payload to a different concrete path or identifier supported "
            "by recent_history. Complete only when verified evidence is sufficient. Use results "
            "discovered earlier to select concrete paths or identifiers for the next action."
        )
    return base + (
        "For VERIFY, evaluate only whether the single current command actually executed and "
        "returned a coherent result matching that command. Do not require one observation to "
        "satisfy the overall user goal, final report, or every plan step; goal sufficiency is an "
        "ACT/completion decision. The current command is observation.command_name; do not infer a "
        "different expected command from terse plan_steps. A succeeded file.read with a path and "
        "content verifies the read "
        "even when that file alone is insufficient for the final report. A succeeded project.scan "
        "with bounded project metadata verifies the scan. Reject failed, ambiguous, missing, or "
        "contradictory command evidence."
    )


def developer_payload(request: ProviderRequest) -> str:
    """Serialize one bounded developer request without provider-specific metadata."""
    return json.dumps(request.to_dict(), ensure_ascii=False, separators=(",", ":"))


def copilot_json_schema(request: CopilotProviderRequest) -> dict[str, Any]:
    """Return the strict schema for one approved read-only AutoCAD copilot answer."""
    allowed = sorted({field.field_name for field in request.fields})
    return {
        "type": "object",
        "properties": {
            "language": {"type": "string", "enum": [request.language.value]},
            "answer": {"type": "string", "minLength": 1},
            "citations": {
                "type": "array",
                "maxItems": 16,
                "items": {
                    "type": "object",
                    "properties": {
                        "source_field": {"type": "string", "enum": allowed},
                        "title": {"type": "string", "minLength": 1},
                    },
                    "required": ["source_field", "title"],
                    "additionalProperties": False,
                },
            },
        },
        "required": ["language", "answer", "citations"],
        "additionalProperties": False,
    }


def copilot_instructions(request: CopilotProviderRequest) -> str:
    """Build a conversational consulting engineer instruction."""
    language = "Arabic" if request.language is CopilotLanguage.ARABIC else "English"
    allowed = ", ".join(field.field_name for field in request.fields)
    return (
        "You are AI-Engineer, an expert senior architectural, electrical, and MEP consulting engineer integrated directly inside AutoCAD. "
        "Converse naturally, politely, and collaboratively as a smart engineering colleague. "
        "IMPORTANT CAPABILITIES: This exact chat window supports inspecting the active AutoCAD drawing, duplicating/copying the drawing, "
        "and drawing geometry/blocks/plans directly into the active drawing onto review layer _AI_PREVIEW_REVIEW (confirmed with 'تمام' or rolled back with 'تراجع'). "
        "NEVER tell the engineer that you are in read-only mode, that you cannot draw or modify the drawing, or to switch to the 'Project Agent' tab. "
        "If design parameters or dimensions are missing for a request, ask the engineer directly inside this chat window in clear natural language. "
        "If the user asks what the currently open drawing is or what it contains, carefully read the live drawing inspection "
        "details inside context.summary (active_file, units, total_entities, extents, types, layers, blocks, texts) and explain "
        "clearly what type of drawing it is (e.g., architectural apartment floor plan, electrical lighting/power layout, "
        "CAD blocks, or geometric shapes) citing ONLY those observed layers, blocks, dimensions/extents, and text labels. "
        "If total_entities=0 or status=EMPTY_DRAWING, state clearly and honestly that the open drawing is currently empty. "
        "If the user asks why they do not see drawn objects yet, suggest running ZOOM EXTENTS (or double-click middle mouse wheel) and confirm drawings are placed in red on layer _AI_PREVIEW_REVIEW. "
        "Do not invent unsupported project facts or ungrounded numbers. Answer in "
        f"{language}. Return only JSON matching the required schema. Approved source fields: "
        f"{allowed}."
    )


def copilot_payload(request: CopilotProviderRequest) -> str:
    """Serialize only the already-approved egress fields for a copilot request."""
    value = {
        "session_id": request.session_id,
        "context_id": request.context_id,
        "review_id": request.review_id,
        "preview_sha256": request.preview_sha256,
        "language": request.language.value,
        "fields": [
            {"field_name": field.field_name, "value": field.value} for field in request.fields
        ],
    }
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))
