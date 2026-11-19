"""Cost-aware hybrid provider: local reasoning first, Codex escalation on provider failure."""

from __future__ import annotations

import json

from agent.copilot_provider import (
    CopilotProviderAdapter,
    CopilotProviderOutput,
    CopilotProviderRequest,
)
from agent.exceptions import CopilotProviderError, ProviderError
from agent.provider import ProviderAdapter, ProviderPhase, ProviderRequest


class HybridDeveloperGenerator:
    """Use a local adapter first and fall back only when its provider contract fails."""

    def __init__(self, primary: ProviderAdapter, fallback: ProviderAdapter) -> None:
        self.primary = primary
        self.fallback = fallback

    def __call__(self, request: ProviderRequest) -> str:
        try:
            response = self.primary.generate(request)
            if not _developer_contract_valid(response, request):
                raise ProviderError("Primary provider violated the developer output contract.")
            return response
        except ProviderError:
            return self.fallback.generate(request)


class HybridCopilotGenerator:
    """Validate local output before spending one Codex fallback call."""

    def __init__(
        self,
        primary: CopilotProviderAdapter,
        fallback: CopilotProviderAdapter,
    ) -> None:
        self.primary = primary
        self.fallback = fallback

    def __call__(self, request: CopilotProviderRequest) -> str:
        try:
            output = self.primary.generate(request)
        except CopilotProviderError:
            output = self.fallback.generate(request)
        return _copilot_json(output)


def create_hybrid_providers(
    local_developer: ProviderAdapter,
    local_copilot: CopilotProviderAdapter,
    codex_developer: ProviderAdapter,
    codex_copilot: CopilotProviderAdapter,
) -> tuple[ProviderAdapter, CopilotProviderAdapter]:
    """Build a hybrid pair without bypassing either child provider's validators."""
    developer = ProviderAdapter(
        "hybrid",
        HybridDeveloperGenerator(local_developer, codex_developer),
    )
    copilot = CopilotProviderAdapter(
        "hybrid",
        HybridCopilotGenerator(local_copilot, codex_copilot),
        timeout_seconds=125,
        maximum_concurrent_calls=1,
    )
    return developer, copilot


def _copilot_json(output: CopilotProviderOutput) -> str:
    return json.dumps(
        {
            "language": output.language.value,
            "answer": output.answer,
            "citations": [
                {"source_field": item.source_field, "title": item.title}
                for item in output.citations
            ],
        },
        ensure_ascii=False,
        separators=(",", ":"),
    )


def _developer_contract_valid(raw: str, request: ProviderRequest) -> bool:
    """Cheap structural validation used only to decide whether Codex fallback is warranted."""
    try:
        value = json.loads(raw)
    except (TypeError, json.JSONDecodeError):
        return False
    if not isinstance(value, dict):
        return False
    if request.phase is ProviderPhase.PLAN:
        return (
            set(value) == {"summary", "steps"}
            and isinstance(value.get("summary"), str)
            and bool(value["summary"].strip())
            and isinstance(value.get("steps"), list)
            and 1 <= len(value["steps"]) <= 20
            and all(isinstance(item, str) and item.strip() for item in value["steps"])
        )
    if request.phase is ProviderPhase.VERIFY:
        return (
            set(value) == {"accepted", "reason"}
            and isinstance(value.get("accepted"), bool)
            and isinstance(value.get("reason"), str)
            and bool(value["reason"].strip())
        )
    kind = value.get("kind")
    if kind == "complete":
        return (
            set(value) == {"kind", "summary"}
            and isinstance(value.get("summary"), str)
            and bool(value["summary"].strip())
        )
    if kind != "command" or set(value) != {"kind", "command_name", "payload", "rationale"}:
        return False
    allowed = {command.name for command in request.available_commands}
    return (
        isinstance(value.get("command_name"), str)
        and value["command_name"] in allowed
        and isinstance(value.get("payload"), dict)
        and isinstance(value.get("rationale"), str)
        and bool(value["rationale"].strip())
    )
