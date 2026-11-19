"""Optional OpenAI Responses API adapters for reviewed local AI-Engineer workflows.

The adapters are deliberately injected at runtime. Credentials are read from the
process environment only, the OpenAI endpoint is fixed, redirects/proxies are disabled,
and every response is revalidated by the existing local provider contracts.
"""

from __future__ import annotations

import json
import os
import re
import ssl
import urllib.error
import urllib.request
from dataclasses import dataclass
from typing import Any, Callable, Mapping

from agent.copilot import CopilotLanguage
from agent.copilot_provider import CopilotProviderAdapter, CopilotProviderRequest
from agent.exceptions import ApiConfigurationError, CopilotProviderError, ProviderError
from agent.provider import ProviderAdapter, ProviderPhase, ProviderRequest


_RESPONSES_URL = "https://api.openai.com/v1/responses"
_MODEL_PATTERN = re.compile(r"[A-Za-z0-9][A-Za-z0-9._:-]{1,127}")
_REASONING_EFFORTS = frozenset({"none", "low", "medium", "high", "xhigh", "max"})
_REASONING_MODES = frozenset({"standard", "pro"})
_ENABLE_VALUES = frozenset({"1", "true", "yes", "on"})
_MAX_REQUEST_BYTES = 524_288
_MAX_RESPONSE_BYTES = 1_048_576
HttpPost = Callable[[str, Mapping[str, str], bytes, float], bytes]


@dataclass(frozen=True, slots=True)
class OpenAIProviderConfig:
    """Validated opt-in provider settings loaded from environment only."""

    api_key: str
    model: str = "gpt-5.6"
    reasoning_effort: str = "high"
    reasoning_mode: str = "standard"
    timeout_seconds: float = 60.0
    max_output_tokens: int = 8_192

    def __post_init__(self) -> None:
        if not isinstance(self.api_key, str) or not 20 <= len(self.api_key) <= 512:
            raise ApiConfigurationError("OPENAI_API_KEY is missing or invalid.")
        if self.api_key != self.api_key.strip() or any(ch.isspace() for ch in self.api_key):
            raise ApiConfigurationError("OPENAI_API_KEY must not contain whitespace.")
        if not isinstance(self.model, str) or _MODEL_PATTERN.fullmatch(self.model) is None:
            raise ApiConfigurationError("AI_ENGINEER_OPENAI_MODEL is invalid.")
        if self.reasoning_effort not in _REASONING_EFFORTS:
            raise ApiConfigurationError("AI_ENGINEER_OPENAI_REASONING_EFFORT is invalid.")
        if self.reasoning_mode not in _REASONING_MODES:
            raise ApiConfigurationError("AI_ENGINEER_OPENAI_REASONING_MODE is invalid.")
        if (
            not isinstance(self.timeout_seconds, (int, float))
            or isinstance(self.timeout_seconds, bool)
            or not 5 <= float(self.timeout_seconds) <= 120
        ):
            raise ApiConfigurationError("OpenAI timeout must be from 5 through 120 seconds.")
        if (
            not isinstance(self.max_output_tokens, int)
            or isinstance(self.max_output_tokens, bool)
            or not 512 <= self.max_output_tokens <= 32_768
        ):
            raise ApiConfigurationError("OpenAI max output tokens must be from 512 through 32768.")

    @classmethod
    def from_environment(
        cls, environment: Mapping[str, str] | None = None
    ) -> OpenAIProviderConfig:
        """Load only after explicit provider/data-egress opt-in."""
        values = os.environ if environment is None else environment
        enabled = values.get("AI_ENGINEER_OPENAI_ENABLE", "").strip().casefold()
        if enabled not in _ENABLE_VALUES:
            raise ApiConfigurationError(
                "OpenAI provider is disabled. Set AI_ENGINEER_OPENAI_ENABLE=1 to opt in."
            )
        egress = values.get("AI_ENGINEER_OPENAI_EGRESS_CONFIRMED", "").strip().casefold()
        if egress not in _ENABLE_VALUES:
            raise ApiConfigurationError(
                "OpenAI data egress is not confirmed. Set "
                "AI_ENGINEER_OPENAI_EGRESS_CONFIRMED=1 only after reviewing the boundary."
            )
        key = values.get("OPENAI_API_KEY")
        if key is None:
            raise ApiConfigurationError("OPENAI_API_KEY is required for the OpenAI provider.")
        model = values.get("AI_ENGINEER_OPENAI_MODEL", "gpt-5.6").strip()
        effort = values.get("AI_ENGINEER_OPENAI_REASONING_EFFORT", "high").strip().casefold()
        mode = values.get("AI_ENGINEER_OPENAI_REASONING_MODE", "standard").strip().casefold()
        try:
            timeout = float(values.get("AI_ENGINEER_OPENAI_TIMEOUT_SECONDS", "60"))
            max_tokens = int(values.get("AI_ENGINEER_OPENAI_MAX_OUTPUT_TOKENS", "8192"))
        except ValueError as exc:
            raise ApiConfigurationError(
                "OpenAI timeout and max output tokens must be numeric."
            ) from exc
        return cls(
            api_key=key,
            model=model,
            reasoning_effort=effort,
            reasoning_mode=mode,
            timeout_seconds=timeout,
            max_output_tokens=max_tokens,
        )


class OpenAIResponsesClient:
    """Small fixed-endpoint Responses API client for the two reviewed provider contracts."""

    def __init__(
        self,
        config: OpenAIProviderConfig,
        *,
        http_post: HttpPost | None = None,
    ) -> None:
        self.config = config
        self._http_post = http_post or _post_without_redirects_or_proxies

    def generate_developer(self, request: ProviderRequest) -> str:
        """Return strict JSON for one developer-agent PLAN/ACT/VERIFY phase."""
        if not isinstance(request, ProviderRequest):
            raise ProviderError("OpenAI developer provider requires a typed request.")
        schema = _developer_schema_instruction(request.phase)
        instructions = (
            "You are the reasoning provider behind AI-Engineer, a local-first engineering and "
            "software-development agent. The local application owns permissions, approvals, "
            "command validation, execution, and verification. Never claim an action ran unless "
            "the observation says it ran. Use only command names supplied in available_commands. "
            "Do not invent missing payload fields. Return only one JSON object with no markdown. "
            + schema
        )
        payload = json.dumps(request.to_dict(), ensure_ascii=False, separators=(",", ":"))
        return self._responses_text(instructions, payload, ProviderError)

    def generate_copilot(self, request: CopilotProviderRequest) -> str:
        """Return a read-only AutoCAD copilot answer using only approved egress fields."""
        if not isinstance(request, CopilotProviderRequest):
            raise CopilotProviderError("OpenAI copilot provider requires a typed request.")
        language = "Arabic" if request.language is CopilotLanguage.ARABIC else "English"
        allowed_sources = [field.field_name for field in request.fields]
        instructions = (
            "You are AI-Engineer inside AutoCAD. Answer read-only engineering questions from "
            "the exact approved fields below. Do not create commands, drawing mutations, or "
            "unapproved facts. Report uncertainty when context is insufficient. Answer in "
            f"{language}. Return only JSON with exact keys language, answer, citations. "
            "language must be 'ar' or 'en' matching the request. citations is an array of unique "
            "objects with exact keys source_field and title; source_field must be one of: "
            + ", ".join(allowed_sources)
            + ". Use an empty citations array when no approved field directly supports a claim."
        )
        value = {
            "session_id": request.session_id,
            "context_id": request.context_id,
            "review_id": request.review_id,
            "preview_sha256": request.preview_sha256,
            "language": request.language.value,
            "fields": [
                {"field_name": field.field_name, "value": field.value}
                for field in request.fields
            ],
        }
        payload = json.dumps(value, ensure_ascii=False, separators=(",", ":"))
        return self._responses_text(instructions, payload, CopilotProviderError)

    def _responses_text(
        self,
        instructions: str,
        user_input: str,
        error_type: type[Exception],
    ) -> str:
        body: dict[str, Any] = {
            "model": self.config.model,
            "instructions": instructions,
            "input": user_input,
            "store": False,
            "max_output_tokens": self.config.max_output_tokens,
            "reasoning": {"effort": self.config.reasoning_effort},
            "text": {"verbosity": "low"},
        }
        if self.config.reasoning_mode == "pro":
            body["reasoning"]["mode"] = "pro"
        encoded = json.dumps(body, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        if len(encoded) > _MAX_REQUEST_BYTES:
            raise error_type("OpenAI provider request exceeds its safe byte limit.")
        headers = {
            "Authorization": f"Bearer {self.config.api_key}",
            "Content-Type": "application/json",
            "Accept": "application/json",
            "User-Agent": "AI-Engineer/secure-provider",
        }
        try:
            raw = self._http_post(
                _RESPONSES_URL,
                headers,
                encoded,
                float(self.config.timeout_seconds),
            )
        except Exception as exc:
            if isinstance(exc, error_type):
                raise
            raise error_type(f"OpenAI provider request failed: {type(exc).__name__}.") from exc
        if not isinstance(raw, (bytes, bytearray)) or not 1 <= len(raw) <= _MAX_RESPONSE_BYTES:
            raise error_type("OpenAI provider response exceeds its safe byte limit.")
        try:
            document = json.loads(bytes(raw).decode("utf-8"))
        except (UnicodeError, json.JSONDecodeError) as exc:
            raise error_type("OpenAI provider returned invalid JSON.") from exc
        text = _extract_output_text(document)
        if not text:
            raise error_type("OpenAI provider returned no output text.")
        return text


def create_openai_providers(
    config: OpenAIProviderConfig,
    *,
    http_post: HttpPost | None = None,
) -> tuple[ProviderAdapter, CopilotProviderAdapter]:
    """Create the developer and AutoCAD copilot adapters over one fixed client."""
    client = OpenAIResponsesClient(config, http_post=http_post)
    developer = ProviderAdapter("openai", client.generate_developer)
    copilot = CopilotProviderAdapter(
        "openai",
        client.generate_copilot,
        timeout_seconds=min(60, max(5, int(config.timeout_seconds))),
    )
    return developer, copilot


def _developer_schema_instruction(phase: ProviderPhase) -> str:
    if phase is ProviderPhase.PLAN:
        return (
            "For PLAN return exactly {\"summary\": string, \"steps\": [1..20 strings]}. "
            "Plan safe local reads and verification first; mention approvals before sensitive work."
        )
    if phase is ProviderPhase.ACT:
        return (
            "For ACT return either exactly {\"kind\":\"command\",\"command_name\":string,"
            "\"payload\":object,\"rationale\":string} using one available command, or exactly "
            "{\"kind\":\"complete\",\"summary\":string}. Do not complete unless the supplied "
            "state/observation supports verified completion."
        )
    return (
        "For VERIFY return exactly {\"accepted\": boolean, \"reason\": string}. Accept only "
        "when the observed task succeeded and its result actually supports the intended action."
    )


def _extract_output_text(document: object) -> str:
    if not isinstance(document, dict):
        return ""
    direct = document.get("output_text")
    if isinstance(direct, str) and direct.strip():
        return direct.strip()
    output = document.get("output")
    if not isinstance(output, list):
        return ""
    parts: list[str] = []
    for item in output:
        if not isinstance(item, dict) or item.get("type") != "message":
            continue
        content = item.get("content")
        if not isinstance(content, list):
            continue
        for part in content:
            if not isinstance(part, dict) or part.get("type") != "output_text":
                continue
            text = part.get("text")
            if isinstance(text, str) and text:
                parts.append(text)
    return "\n".join(parts).strip()


class _NoRedirectHandler(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):  # type: ignore[no-untyped-def]
        return None


def _post_without_redirects_or_proxies(
    url: str,
    headers: Mapping[str, str],
    body: bytes,
    timeout_seconds: float,
) -> bytes:
    if url != _RESPONSES_URL:
        raise ProviderError("OpenAI endpoint is not allowlisted.")
    request = urllib.request.Request(url, data=body, headers=dict(headers), method="POST")
    context = ssl.create_default_context()
    opener = urllib.request.build_opener(
        urllib.request.ProxyHandler({}),
        _NoRedirectHandler(),
        urllib.request.HTTPSHandler(context=context),
    )
    try:
        with opener.open(request, timeout=timeout_seconds) as response:
            if getattr(response, "status", 200) != 200:
                raise ProviderError("OpenAI provider returned a non-success status.")
            raw = response.read(_MAX_RESPONSE_BYTES + 1)
    except urllib.error.HTTPError as exc:
        raise ProviderError(f"OpenAI provider returned HTTP {exc.code}.") from exc
    except urllib.error.URLError as exc:
        raise ProviderError("OpenAI provider network request failed.") from exc
    if len(raw) > _MAX_RESPONSE_BYTES:
        raise ProviderError("OpenAI provider response exceeds its safe byte limit.")
    return raw
