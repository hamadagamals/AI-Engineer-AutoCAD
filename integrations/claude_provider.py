"""Optional Anthropic Claude Messages API adapters for reviewed local AI-Engineer workflows.

Credentials exist only in the process environment (ANTHROPIC_API_KEY). The endpoint
is fixed, proxies and redirects are disabled, provider output is validated against
strict JSON schemas, and AI-Engineer retains all local authority.
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
from integrations.provider_contracts import (
    copilot_instructions,
    copilot_payload,
    developer_instructions,
    developer_payload,
)

_MESSAGES_URL = "https://api.anthropic.com/v1/messages"
_MODEL_PATTERN = re.compile(r"[A-Za-z0-9][A-Za-z0-9._:-]{1,127}")
_ENABLE_VALUES = frozenset({"1", "true", "yes", "on"})
_MAX_REQUEST_BYTES = 524_288
_MAX_RESPONSE_BYTES = 1_048_576
HttpPost = Callable[[str, Mapping[str, str], bytes, float], bytes]


@dataclass(frozen=True, slots=True)
class ClaudeProviderConfig:
    """Validated opt-in Claude settings loaded from environment only."""

    api_key: str
    model: str = "claude-3-7-sonnet-20250219"
    timeout_seconds: float = 60.0
    max_output_tokens: int = 4_096

    def __post_init__(self) -> None:
        if not isinstance(self.api_key, str) or not 20 <= len(self.api_key) <= 512:
            raise ApiConfigurationError("ANTHROPIC_API_KEY is missing or invalid.")
        if self.api_key != self.api_key.strip() or any(ch.isspace() for ch in self.api_key):
            raise ApiConfigurationError("ANTHROPIC_API_KEY must not contain whitespace.")
        if not isinstance(self.model, str) or _MODEL_PATTERN.fullmatch(self.model) is None:
            raise ApiConfigurationError("AI_ENGINEER_CLAUDE_MODEL is invalid.")
        if (
            not isinstance(self.timeout_seconds, (int, float))
            or isinstance(self.timeout_seconds, bool)
            or not 5 <= float(self.timeout_seconds) <= 120
        ):
            raise ApiConfigurationError("Claude timeout must be from 5 through 120 seconds.")
        if (
            not isinstance(self.max_output_tokens, int)
            or isinstance(self.max_output_tokens, bool)
            or not 512 <= self.max_output_tokens <= 16_384
        ):
            raise ApiConfigurationError("Claude max output tokens must be from 512 through 16384.")

    @classmethod
    def from_environment(
        cls, environment: Mapping[str, str] | None = None
    ) -> ClaudeProviderConfig:
        """Load only after explicit provider opt-in."""
        values = os.environ if environment is None else environment
        api_key = values.get("ANTHROPIC_API_KEY", "").strip()
        model = values.get("AI_ENGINEER_CLAUDE_MODEL", "claude-3-7-sonnet-20250219").strip() or "claude-3-7-sonnet-20250219"
        raw_timeout = values.get("AI_ENGINEER_CLAUDE_TIMEOUT_SECONDS", "60").strip()
        try:
            timeout_seconds = float(raw_timeout)
        except ValueError as exc:
            raise ApiConfigurationError("AI_ENGINEER_CLAUDE_TIMEOUT_SECONDS is not a valid number.") from exc
        raw_tokens = values.get("AI_ENGINEER_CLAUDE_MAX_OUTPUT_TOKENS", "4096").strip()
        try:
            max_output_tokens = int(raw_tokens)
        except ValueError as exc:
            raise ApiConfigurationError("AI_ENGINEER_CLAUDE_MAX_OUTPUT_TOKENS is not an integer.") from exc
        return cls(
            api_key=api_key,
            model=model,
            timeout_seconds=timeout_seconds,
            max_output_tokens=max_output_tokens,
        )


def _default_post(
    url: str,
    headers: Mapping[str, str],
    body: bytes,
    timeout_seconds: float,
) -> bytes:
    """Issue one strict TLS-verified POST without redirects or proxies."""
    if len(body) > _MAX_REQUEST_BYTES:
        raise ProviderError("Outgoing request exceeds byte limit.")
    context = ssl.create_default_context()
    request = urllib.request.Request(
        url,
        data=body,
        headers=dict(headers),
        method="POST",
    )
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    try:
        with opener.open(request, timeout=timeout_seconds) as response:
            payload = response.read(_MAX_RESPONSE_BYTES + 1)
            if len(payload) > _MAX_RESPONSE_BYTES:
                raise ProviderError("Claude response exceeds maximum allowed size.")
            return payload
    except urllib.error.HTTPError as exc:
        raise ProviderError(f"Claude API HTTP error: {exc.code}") from exc
    except (urllib.error.URLError, TimeoutError, OSError) as exc:
        raise ProviderError(f"Claude API connection error: {exc}") from exc


def _extract_text_content(response_payload: bytes) -> str:
    """Parse Anthropic response JSON and extract assistant text blocks."""
    try:
        data = json.loads(response_payload.decode("utf-8"))
    except Exception as exc:
        raise ProviderError(f"Failed to parse Claude response JSON: {exc}") from exc
    if not isinstance(data, dict):
        raise ProviderError("Claude response JSON must be an object.")
    content = data.get("content")
    if not isinstance(content, list) or not content:
        raise ProviderError("Claude response missing valid content array.")
    text_parts: list[str] = []
    for block in content:
        if isinstance(block, dict) and block.get("type") == "text":
            text_parts.append(block.get("text", ""))
    result = "".join(text_parts).strip()
    if not result:
        raise ProviderError("Claude returned empty text content.")
    return result


def _clean_json_markdown(text: str) -> str:
    """Strip markdown code fence if model wrapped output in ```json."""
    trimmed = text.strip()
    if trimmed.startswith("```json"):
        trimmed = trimmed[7:]
    elif trimmed.startswith("```"):
        trimmed = trimmed[3:]
    if trimmed.endswith("```"):
        trimmed = trimmed[:-3]
    return trimmed.strip()


class ClaudeMessagesClient:
    """Client for generating developer and copilot responses via Anthropic Messages."""

    def __init__(
        self,
        config: ClaudeProviderConfig,
        *,
        http_post: HttpPost | None = None,
    ) -> None:
        self.config = config
        self._post = _default_post if http_post is None else http_post

    def _call(self, system: str, user_prompt: str) -> str:
        body_dict = {
            "model": self.config.model,
            "max_tokens": self.config.max_output_tokens,
            "system": system,
            "messages": [
                {"role": "user", "content": user_prompt}
            ],
            "temperature": 0.1,
        }
        body_bytes = json.dumps(body_dict, ensure_ascii=False).encode("utf-8")
        headers = {
            "Content-Type": "application/json",
            "x-api-key": self.config.api_key,
            "anthropic-version": "2023-06-01",
        }
        raw_output = self._post(
            _MESSAGES_URL,
            headers,
            body_bytes,
            self.config.timeout_seconds,
        )
        text = _extract_text_content(raw_output)
        return _clean_json_markdown(text)

    def generate_developer(self, request: ProviderRequest) -> str:
        system = (
            "You are an expert electrical-engineering software development assistant. "
            "You MUST respond ONLY with a valid raw JSON object strictly adhering to the schema. "
            "Never include explanations, markdown blocks, or text outside the JSON object.\n"
            + developer_instructions(request)
        )
        user_prompt = developer_payload(request)
        return self._call(system, user_prompt)

    def generate_copilot(self, request: CopilotProviderRequest) -> str:
        instructions = copilot_instructions(request.language)
        system = (
            "You are a professional local electrical-engineering CAD/BIM copilot. "
            "You MUST answer strictly based on provided context without hallucinations. "
            "Your output must be a valid raw JSON object matching the requested schema.\n"
            + instructions
        )
        user_prompt = copilot_payload(request)
        return self._call(system, user_prompt)


def create_claude_providers(
    config: ClaudeProviderConfig,
    *,
    http_post: HttpPost | None = None,
) -> tuple[ProviderAdapter, CopilotProviderAdapter]:
    """Create the developer and copilot adapters over one fixed Claude client."""
    client = ClaudeMessagesClient(config, http_post=http_post)
    developer = ProviderAdapter("claude", client.generate_developer)
    copilot = CopilotProviderAdapter(
        "claude",
        client.generate_copilot,
        timeout_seconds=min(60, max(5, int(config.timeout_seconds))),
    )
    return developer, copilot
