"""Local Ollama provider adapters with loopback-only networking and structured output."""

from __future__ import annotations

import json
import os
import re
import urllib.error
import urllib.request
from dataclasses import dataclass
from typing import Any, Callable, Mapping
from urllib.parse import urlsplit

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

_ENABLE_VALUES = frozenset({"1", "true", "yes", "on"})
_MODEL_PATTERN = re.compile(r"[A-Za-z0-9][A-Za-z0-9._:/-]{0,127}")
_DEFAULT_URL = "http://127.0.0.1:11434"
_MAX_REQUEST_BYTES = 524_288
_MAX_RESPONSE_BYTES = 1_048_576
HttpPost = Callable[[str, Mapping[str, str], bytes, float], bytes]


@dataclass(frozen=True, slots=True)
class OllamaProviderConfig:
    """Validated local provider configuration; remote hosts are intentionally rejected."""

    base_url: str = _DEFAULT_URL
    model: str = "qwen2.5-coder:7b"
    timeout_seconds: float = 240.0
    context_tokens: int = 4_096

    def __post_init__(self) -> None:
        parsed = urlsplit(self.base_url)
        if (
            parsed.scheme != "http"
            or parsed.hostname != "127.0.0.1"
            or parsed.port is None
            or parsed.username is not None
            or parsed.password is not None
            or parsed.path not in {"", "/"}
            or parsed.query
            or parsed.fragment
        ):
            raise ApiConfigurationError(
                "Ollama URL must be loopback HTTP in the form http://127.0.0.1:<port>."
            )
        if not 1 <= parsed.port <= 65_535:
            raise ApiConfigurationError("Ollama port is invalid.")
        if not isinstance(self.model, str) or _MODEL_PATTERN.fullmatch(self.model) is None:
            raise ApiConfigurationError("AI_ENGINEER_OLLAMA_MODEL is invalid.")
        if (
            not isinstance(self.timeout_seconds, (int, float))
            or isinstance(self.timeout_seconds, bool)
            or not 5 <= float(self.timeout_seconds) <= 600
        ):
            raise ApiConfigurationError("Ollama timeout must be from 5 through 600 seconds.")
        if (
            not isinstance(self.context_tokens, int)
            or isinstance(self.context_tokens, bool)
            or not 2_048 <= self.context_tokens <= 32_768
        ):
            raise ApiConfigurationError("Ollama context must be from 2048 through 32768 tokens.")

    @classmethod
    def from_environment(
        cls, environment: Mapping[str, str] | None = None
    ) -> "OllamaProviderConfig":
        values = os.environ if environment is None else environment
        enabled = values.get("AI_ENGINEER_OLLAMA_ENABLE", "").strip().casefold()
        if enabled not in _ENABLE_VALUES:
            raise ApiConfigurationError(
                "Ollama provider is disabled. Set AI_ENGINEER_OLLAMA_ENABLE=1 to opt in."
            )
        try:
            timeout = float(values.get("AI_ENGINEER_OLLAMA_TIMEOUT_SECONDS", "240"))
            context = int(values.get("AI_ENGINEER_OLLAMA_CONTEXT_TOKENS", "4096"))
        except ValueError as exc:
            raise ApiConfigurationError("Ollama timeout and context must be numeric.") from exc
        return cls(
            base_url=values.get("AI_ENGINEER_OLLAMA_URL", _DEFAULT_URL).strip().rstrip("/"),
            model=values.get("AI_ENGINEER_OLLAMA_MODEL", "qwen2.5-coder:7b").strip(),
            timeout_seconds=timeout,
            context_tokens=context,
        )


class OllamaChatClient:
    """Tiny loopback-only Ollama /api/chat client for strict AI-Engineer contracts."""

    def __init__(
        self,
        config: OllamaProviderConfig,
        *,
        http_post: HttpPost | None = None,
    ) -> None:
        self.config = config
        self._http_post = http_post or _post_loopback_only

    def generate_developer(self, request: ProviderRequest) -> str:
        if not isinstance(request, ProviderRequest):
            raise ProviderError("Ollama developer provider requires a typed request.")
        return self._chat(
            developer_instructions(request),
            developer_payload(request),
            developer_json_schema(request.phase),
            ProviderError,
        )

    def generate_copilot(self, request: CopilotProviderRequest) -> str:
        if not isinstance(request, CopilotProviderRequest):
            raise CopilotProviderError("Ollama copilot provider requires a typed request.")
        return self._chat(
            copilot_instructions(request),
            copilot_payload(request),
            copilot_json_schema(request),
            CopilotProviderError,
        )

    def _chat(
        self,
        system_prompt: str,
        user_prompt: str,
        schema: dict[str, Any],
        error_type: type[Exception],
    ) -> str:
        body = {
            "model": self.config.model,
            "messages": [
                {"role": "system", "content": system_prompt},
                {"role": "user", "content": user_prompt},
            ],
            "stream": False,
            "keep_alive": "10m",
            "format": schema,
            "options": {
                "temperature": 0,
                "num_ctx": self.config.context_tokens,
            },
        }
        encoded = json.dumps(body, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        if len(encoded) > _MAX_REQUEST_BYTES:
            raise error_type("Ollama provider request exceeds its safe byte limit.")
        url = self.config.base_url + "/api/chat"
        try:
            raw = self._http_post(
                url,
                {"Content-Type": "application/json", "Accept": "application/json"},
                encoded,
                float(self.config.timeout_seconds),
            )
        except Exception as exc:
            if isinstance(exc, error_type):
                raise
            raise error_type(f"Ollama provider request failed: {type(exc).__name__}.") from exc
        if not isinstance(raw, (bytes, bytearray)) or not 1 <= len(raw) <= _MAX_RESPONSE_BYTES:
            raise error_type("Ollama provider response exceeds its safe byte limit.")
        try:
            document = json.loads(bytes(raw).decode("utf-8"))
        except (UnicodeError, json.JSONDecodeError) as exc:
            raise error_type("Ollama provider returned invalid JSON.") from exc
        message = document.get("message") if isinstance(document, dict) else None
        content = message.get("content") if isinstance(message, dict) else None
        if not isinstance(content, str) or not content.strip():
            raise error_type("Ollama provider returned no message content.")
        return content.strip()


def create_ollama_providers(
    config: OllamaProviderConfig,
    *,
    http_post: HttpPost | None = None,
) -> tuple[ProviderAdapter, CopilotProviderAdapter]:
    client = OllamaChatClient(config, http_post=http_post)
    developer = ProviderAdapter("ollama", client.generate_developer)
    copilot = CopilotProviderAdapter(
        "ollama",
        client.generate_copilot,
        timeout_seconds=min(60, max(5, int(config.timeout_seconds))),
    )
    return developer, copilot


class _NoRedirectHandler(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):  # type: ignore[no-untyped-def]
        return None


def _post_loopback_only(
    url: str,
    headers: Mapping[str, str],
    body: bytes,
    timeout_seconds: float,
) -> bytes:
    parsed = urlsplit(url)
    if (
        parsed.scheme != "http"
        or parsed.hostname != "127.0.0.1"
        or parsed.path != "/api/chat"
        or parsed.port is None
        or parsed.query
        or parsed.fragment
    ):
        raise ProviderError("Ollama endpoint is not loopback allowlisted.")
    request = urllib.request.Request(url, data=body, headers=dict(headers), method="POST")
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), _NoRedirectHandler())
    try:
        with opener.open(request, timeout=timeout_seconds) as response:
            if getattr(response, "status", 200) != 200:
                raise ProviderError("Ollama provider returned a non-success status.")
            raw = response.read(_MAX_RESPONSE_BYTES + 1)
    except urllib.error.HTTPError as exc:
        raise ProviderError(f"Ollama provider returned HTTP {exc.code}.") from exc
    except urllib.error.URLError as exc:
        raise ProviderError("Ollama provider is unavailable on loopback.") from exc
    if len(raw) > _MAX_RESPONSE_BYTES:
        raise ProviderError("Ollama provider response exceeds its safe byte limit.")
    return raw
