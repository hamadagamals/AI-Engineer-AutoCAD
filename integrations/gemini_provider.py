"""Optional Gemini Interactions API adapters for reviewed AI-Engineer workflows.

Credentials exist only in the process environment. The endpoint is fixed, proxies and
redirects are disabled, provider output is JSON-schema constrained, and AI-Engineer
retains all local command validation, approval, execution, and verification authority.
"""

from __future__ import annotations

import json
import os
import re
import ssl
import time
import urllib.error
import urllib.request
from dataclasses import dataclass
from typing import Any, Callable, Mapping

from agent.copilot_provider import CopilotProviderAdapter, CopilotProviderRequest
from agent.exceptions import ApiConfigurationError, CopilotProviderError, ProviderError
from agent.provider import ProviderAdapter, ProviderRequest
from integrations.provider_contracts import (
    copilot_instructions,
    copilot_json_schema,
    copilot_payload,
    developer_instructions,
    developer_json_schema,
    developer_payload,
)


_INTERACTIONS_URL = "https://generativelanguage.googleapis.com/v1beta/interactions"
_MODEL_PATTERN = re.compile(r"[A-Za-z0-9][A-Za-z0-9._:-]{1,127}")
_ENABLE_VALUES = frozenset({"1", "true", "yes", "on"})
_MAX_REQUEST_BYTES = 524_288
_MAX_RESPONSE_BYTES = 1_048_576
HttpPost = Callable[[str, Mapping[str, str], bytes, float], bytes]


@dataclass(frozen=True, slots=True)
class GeminiProviderConfig:
    """Validated opt-in Gemini settings loaded from environment only."""

    api_key: str
    model: str = "gemini-3.5-flash-lite"
    timeout_seconds: float = 60.0

    def __post_init__(self) -> None:
        if not isinstance(self.api_key, str) or not 20 <= len(self.api_key) <= 512:
            raise ApiConfigurationError("GEMINI_API_KEY is missing or invalid.")
        if self.api_key != self.api_key.strip() or any(ch.isspace() for ch in self.api_key):
            raise ApiConfigurationError("GEMINI_API_KEY must not contain whitespace.")
        if not isinstance(self.model, str) or _MODEL_PATTERN.fullmatch(self.model) is None:
            raise ApiConfigurationError("AI_ENGINEER_GEMINI_MODEL is invalid.")
        if (
            not isinstance(self.timeout_seconds, (int, float))
            or isinstance(self.timeout_seconds, bool)
            or not 5 <= float(self.timeout_seconds) <= 120
        ):
            raise ApiConfigurationError("Gemini timeout must be from 5 through 120 seconds.")

    @classmethod
    def from_environment(
        cls, environment: Mapping[str, str] | None = None
    ) -> "GeminiProviderConfig":
        """Load Gemini only after separate provider and egress opt-ins."""
        values = os.environ if environment is None else environment
        if values.get("AI_ENGINEER_GEMINI_ENABLE", "").strip().casefold() not in _ENABLE_VALUES:
            raise ApiConfigurationError(
                "Gemini provider is disabled. Set AI_ENGINEER_GEMINI_ENABLE=1 to opt in."
            )
        if (
            values.get("AI_ENGINEER_GEMINI_EGRESS_CONFIRMED", "").strip().casefold()
            not in _ENABLE_VALUES
        ):
            raise ApiConfigurationError(
                "Gemini data egress is not confirmed. Set "
                "AI_ENGINEER_GEMINI_EGRESS_CONFIRMED=1 only after reviewing the boundary."
            )
        key = values.get("GEMINI_API_KEY")
        if key is None:
            raise ApiConfigurationError("GEMINI_API_KEY is required for the Gemini provider.")
        try:
            timeout = float(values.get("AI_ENGINEER_GEMINI_TIMEOUT_SECONDS", "60"))
        except ValueError as exc:
            raise ApiConfigurationError("Gemini timeout must be numeric.") from exc
        return cls(
            api_key=key,
            model=values.get("AI_ENGINEER_GEMINI_MODEL", "gemini-3.5-flash-lite").strip(),
            timeout_seconds=timeout,
        )


class GeminiInteractionsClient:
    """Fixed-endpoint Gemini client for the existing strict provider contracts."""

    def __init__(
        self,
        config: GeminiProviderConfig,
        *,
        http_post: HttpPost | None = None,
        sleep: Callable[[float], None] | None = None,
    ) -> None:
        self.config = config
        self._http_post = http_post or _post_without_redirects_or_proxies
        self._sleep = sleep or time.sleep

    def generate_developer(self, request: ProviderRequest) -> str:
        if not isinstance(request, ProviderRequest):
            raise ProviderError("Gemini developer provider requires a typed request.")
        return self._interact(
            developer_instructions(request),
            developer_payload(request),
            developer_json_schema(request.phase),
            ProviderError,
        )

    def generate_copilot(self, request: CopilotProviderRequest) -> str:
        if not isinstance(request, CopilotProviderRequest):
            raise CopilotProviderError("Gemini copilot provider requires a typed request.")
        return self._interact(
            copilot_instructions(request),
            copilot_payload(request),
            copilot_json_schema(request),
            CopilotProviderError,
        )

    def _interact(
        self,
        instructions: str,
        payload: str,
        schema: dict[str, Any],
        error_type: type[Exception],
    ) -> str:
        body = {
            "model": self.config.model,
            "input": instructions + "\n\nApproved request:\n" + payload,
            "store": False,
            "response_format": {
                "type": "text",
                "mime_type": "application/json",
                "schema": _gemini_schema(schema),
            },
        }
        encoded = json.dumps(body, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        if len(encoded) > _MAX_REQUEST_BYTES:
            raise error_type("Gemini provider request exceeds its safe byte limit.")
        headers = {
            "x-goog-api-key": self.config.api_key,
            "Content-Type": "application/json",
            "Accept": "application/json",
            "User-Agent": "AI-Engineer/secure-provider",
        }
        raw: bytes | bytearray | None = None
        for attempt in range(3):
            try:
                raw = self._http_post(
                    _INTERACTIONS_URL,
                    headers,
                    encoded,
                    float(self.config.timeout_seconds),
                )
                break
            except Exception as exc:
                transient = isinstance(exc, ProviderError) and (
                    "HTTP 429" in str(exc) or "HTTP 503" in str(exc)
                )
                if transient and attempt < 2:
                    self._sleep(float(2**attempt))
                    continue
                if isinstance(exc, error_type):
                    raise
                if isinstance(exc, ProviderError):
                    raise error_type(str(exc)) from exc
                raise error_type(
                    f"Gemini provider request failed: {type(exc).__name__}."
                ) from exc
        if not isinstance(raw, (bytes, bytearray)) or not 1 <= len(raw) <= _MAX_RESPONSE_BYTES:
            raise error_type("Gemini provider response exceeds its safe byte limit.")
        try:
            document = json.loads(bytes(raw).decode("utf-8"))
        except (UnicodeError, json.JSONDecodeError) as exc:
            raise error_type("Gemini provider returned invalid JSON.") from exc
        output = _extract_output_text(document)
        if not output:
            raise error_type("Gemini provider returned no output text.")
        return output


def create_gemini_providers(
    config: GeminiProviderConfig,
    *,
    http_post: HttpPost | None = None,
) -> tuple[ProviderAdapter, CopilotProviderAdapter]:
    """Create developer and AutoCAD copilot adapters over one Gemini client."""
    client = GeminiInteractionsClient(config, http_post=http_post)
    return (
        ProviderAdapter("gemini", client.generate_developer),
        CopilotProviderAdapter(
            "gemini",
            client.generate_copilot,
            timeout_seconds=min(60, max(5, int(config.timeout_seconds))),
        ),
    )


def _gemini_schema(value: object) -> object:
    """Translate strict local JSON Schema keywords to Gemini's documented subset."""
    if isinstance(value, list):
        return [_gemini_schema(item) for item in value]
    if not isinstance(value, dict):
        return value
    result: dict[str, object] = {}
    for key, item in value.items():
        if key in {"additionalProperties", "minLength"}:
            continue
        translated = "anyOf" if key == "oneOf" else key
        if key == "const":
            result["enum"] = [item]
        else:
            result[translated] = _gemini_schema(item)
    return result


def _extract_output_text(document: object) -> str:
    if not isinstance(document, dict):
        return ""
    direct = document.get("output_text")
    if isinstance(direct, str) and direct.strip():
        return direct.strip()
    for field in ("outputs", "steps"):
        items = document.get(field)
        if not isinstance(items, list):
            continue
        parts: list[str] = []
        for item in items:
            if not isinstance(item, dict):
                continue
            if field == "steps" and item.get("type") != "model_output":
                continue
            text = item.get("text") or item.get("output_text")
            if isinstance(text, str) and text:
                parts.append(text)
            content = item.get("content")
            if isinstance(content, str) and content:
                parts.append(content)
            elif isinstance(content, list):
                for block in content:
                    if not isinstance(block, dict):
                        continue
                    block_text = block.get("text")
                    if (
                        block.get("type") == "text"
                        and isinstance(block_text, str)
                        and block_text
                    ):
                        parts.append(block_text)
        if parts:
            return "\n".join(parts).strip()
    return ""


class _NoRedirectHandler(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):  # type: ignore[no-untyped-def]
        return None


def _post_without_redirects_or_proxies(
    url: str,
    headers: Mapping[str, str],
    body: bytes,
    timeout_seconds: float,
) -> bytes:
    if url != _INTERACTIONS_URL:
        raise ProviderError("Gemini endpoint is not allowlisted.")
    request = urllib.request.Request(url, data=body, headers=dict(headers), method="POST")
    opener = urllib.request.build_opener(
        urllib.request.ProxyHandler({}),
        _NoRedirectHandler(),
        urllib.request.HTTPSHandler(context=ssl.create_default_context()),
    )
    try:
        with opener.open(request, timeout=timeout_seconds) as response:
            if getattr(response, "status", 200) != 200:
                raise ProviderError("Gemini provider returned a non-success status.")
            raw = response.read(_MAX_RESPONSE_BYTES + 1)
    except urllib.error.HTTPError as exc:
        detail = _safe_http_error_detail(exc)
        suffix = f": {detail}" if detail else "."
        raise ProviderError(f"Gemini provider returned HTTP {exc.code}{suffix}") from exc
    except urllib.error.URLError as exc:
        raise ProviderError("Gemini provider network request failed.") from exc
    if len(raw) > _MAX_RESPONSE_BYTES:
        raise ProviderError("Gemini provider response exceeds its safe byte limit.")
    return raw


def _safe_http_error_detail(error: urllib.error.HTTPError) -> str:
    """Extract a bounded, credential-redacted Google API error for local diagnosis."""
    try:
        raw = error.read(8193)
    except Exception:
        return ""
    if not raw or len(raw) > 8192:
        return ""
    try:
        document = json.loads(raw.decode("utf-8"))
    except (UnicodeError, json.JSONDecodeError):
        return ""
    payload = document.get("error") if isinstance(document, dict) else None
    if not isinstance(payload, dict):
        return ""
    status = payload.get("status")
    message = payload.get("message")
    parts: list[str] = []
    if isinstance(status, str) and re.fullmatch(r"[A-Z_]{2,64}", status):
        parts.append(status)
    if isinstance(message, str):
        clean = " ".join(message.split())
        clean = re.sub(r"AIza[0-9A-Za-z_-]{20,}", "[REDACTED_API_KEY]", clean)
        if clean:
            parts.append(clean[:320])
    return " - ".join(parts)
