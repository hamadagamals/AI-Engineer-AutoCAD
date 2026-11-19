"""Bounded provider adapter and typed developer-agent requests."""

from __future__ import annotations

import re
from dataclasses import asdict, dataclass
from enum import StrEnum
from typing import Any, Callable

from agent.exceptions import ProviderError


class ProviderPhase(StrEnum):
    """Provider phases controlled by the local state machine."""

    PLAN = "plan"
    ACT = "act"
    VERIFY = "verify"


@dataclass(frozen=True, slots=True)
class ProviderCommand:
    """Sanitized registered-command metadata visible to a provider."""

    name: str
    description: str
    permission: str
    risk: str
    payload_contract: str = "{}"


@dataclass(frozen=True, slots=True)
class ProviderRequest:
    """One bounded request sent to an injected provider implementation."""

    run_id: str
    phase: ProviderPhase
    goal: str
    plan_summary: str | None
    plan_steps: tuple[str, ...]
    action_number: int
    maximum_actions: int
    available_commands: tuple[ProviderCommand, ...]
    observation: dict[str, Any] | None = None
    recent_history: tuple[dict[str, Any], ...] = ()

    def to_dict(self) -> dict[str, Any]:
        """Return a JSON-compatible representation for SDK adapters."""
        value = asdict(self)
        value["phase"] = self.phase.value
        return value


ProviderGenerator = Callable[[ProviderRequest], str]


class ProviderAdapter:
    """Wrap a trusted SDK callback behind a small, redacted interface."""

    __slots__ = ("_generator", "name", "maximum_response_bytes")

    def __init__(
        self,
        name: str,
        generator: ProviderGenerator,
        *,
        maximum_response_bytes: int = 65_536,
    ) -> None:
        if not isinstance(name, str) or re.fullmatch(r"[a-z][a-z0-9_-]{1,63}", name) is None:
            raise ProviderError(
                "Provider name must be a lowercase identifier from 2 through 64 characters."
            )
        if not callable(generator):
            raise ProviderError("Provider generator must be callable.")
        if (
            not isinstance(maximum_response_bytes, int)
            or isinstance(maximum_response_bytes, bool)
            or not 1_024 <= maximum_response_bytes <= 1_048_576
        ):
            raise ProviderError("Provider response limit must be from 1024 through 1048576 bytes.")
        self.name = name
        self._generator = generator
        self.maximum_response_bytes = maximum_response_bytes

    def generate(self, request: ProviderRequest) -> str:
        """Generate one response and enforce its type and encoded byte limit."""
        try:
            response = self._generator(request)
        except Exception as exc:
            raise ProviderError(
                f"Provider invocation failed with {type(exc).__name__}."
            ) from exc
        if not isinstance(response, str):
            raise ProviderError("Provider response must be a JSON string.")
        try:
            size = len(response.encode("utf-8"))
        except UnicodeEncodeError as exc:
            raise ProviderError("Provider response must contain valid UTF-8 text.") from exc
        if size == 0 or size > self.maximum_response_bytes:
            raise ProviderError("Provider response is empty or exceeds its byte limit.")
        return response

    def __repr__(self) -> str:
        return (
            f"ProviderAdapter(name={self.name!r}, "
            f"maximum_response_bytes={self.maximum_response_bytes})"
        )
