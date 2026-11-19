"""Bounded provider adapter for read-only copilot answers only."""

from __future__ import annotations

import json
import re
import threading
from concurrent.futures import ThreadPoolExecutor, TimeoutError as FutureTimeoutError
from dataclasses import dataclass
from typing import Callable

from agent.copilot import CopilotCitation, CopilotLanguage, EgressField
from agent.exceptions import CopilotProviderError


_NAME = re.compile(r"[a-z][a-z0-9_.:-]{0,127}")
_HEX32 = re.compile(r"[0-9a-f]{32}")
_SHA256 = re.compile(r"[0-9a-f]{64}")
_OUTPUT_KEYS = frozenset({"language", "answer", "citations"})
_CITATION_KEYS = frozenset({"source_field", "title"})


@dataclass(frozen=True, slots=True)
class CopilotProviderRequest:
    """Exact fields approved for one provider call; contains no drawing or command."""

    session_id: str
    context_id: str
    review_id: str
    preview_sha256: str
    language: CopilotLanguage
    fields: tuple[EgressField, ...]

    def __post_init__(self) -> None:
        for value, pattern, name in (
            (self.session_id, _HEX32, "session ID"),
            (self.context_id, _HEX32, "context ID"),
            (self.review_id, _HEX32, "review ID"),
            (self.preview_sha256, _SHA256, "preview SHA-256"),
        ):
            if not isinstance(value, str) or pattern.fullmatch(value) is None:
                raise CopilotProviderError(f"Provider request {name} is invalid.")
        if not isinstance(self.language, CopilotLanguage):
            raise CopilotProviderError("Provider request language is invalid.")
        if not isinstance(self.fields, tuple) or not 1 <= len(self.fields) <= 32:
            raise CopilotProviderError("Provider request fields are invalid.")
        if any(not isinstance(field, EgressField) for field in self.fields):
            raise CopilotProviderError("Provider request contains an invalid field.")


@dataclass(frozen=True, slots=True)
class CopilotProviderOutput:
    language: CopilotLanguage
    answer: str
    citations: tuple[CopilotCitation, ...]


class CopilotProviderAdapter:
    """Invoke one injected callback with strict output, timeout, and concurrency bounds."""

    def __init__(
        self,
        name: str,
        generate: Callable[[CopilotProviderRequest], str],
        *,
        timeout_seconds: int = 15,
        maximum_concurrent_calls: int = 2,
    ) -> None:
        if not isinstance(name, str) or _NAME.fullmatch(name) is None:
            raise CopilotProviderError("Copilot provider name is invalid.")
        if not callable(generate):
            raise CopilotProviderError("Copilot provider callback is invalid.")
        if not isinstance(timeout_seconds, int) or not 1 <= timeout_seconds <= 180:
            raise CopilotProviderError("Copilot provider timeout must be from 1 through 180 seconds.")
        if not isinstance(maximum_concurrent_calls, int) or not 1 <= maximum_concurrent_calls <= 4:
            raise CopilotProviderError("Copilot provider concurrency must be from 1 through 4.")
        self.name = name
        self._generate = generate
        self.timeout_seconds = timeout_seconds
        self._slots = threading.BoundedSemaphore(maximum_concurrent_calls)
        self._executor = ThreadPoolExecutor(
            max_workers=maximum_concurrent_calls,
            thread_name_prefix="ai-engineer-copilot-provider",
        )

    def generate(self, request: CopilotProviderRequest) -> CopilotProviderOutput:
        if not isinstance(request, CopilotProviderRequest):
            raise CopilotProviderError("Copilot provider requires a typed request.")
        if not self._slots.acquire(blocking=False):
            raise CopilotProviderError("Copilot provider concurrency limit reached.")
        future = self._executor.submit(self._generate, request)
        try:
            try:
                raw = future.result(timeout=self.timeout_seconds)
            except FutureTimeoutError as exc:
                future.cancel()
                raise CopilotProviderError("Copilot provider timed out.") from exc
            except CopilotProviderError:
                raise
            except Exception as exc:
                raise CopilotProviderError(
                    f"Copilot provider failed: {type(exc).__name__}."
                ) from exc
            return _parse_output(raw, request)
        finally:
            self._slots.release()


def _parse_output(raw: object, request: CopilotProviderRequest) -> CopilotProviderOutput:
    if not isinstance(raw, str) or not 1 <= len(raw.encode("utf-8")) <= 65_536:
        raise CopilotProviderError("Copilot provider output exceeds its text limit.")
    try:
        value = json.loads(raw)
    except json.JSONDecodeError as exc:
        raise CopilotProviderError("Copilot provider output is not valid JSON.") from exc
    if not isinstance(value, dict) or set(value) != _OUTPUT_KEYS:
        raise CopilotProviderError("Copilot provider output must contain exact fields.")
    try:
        language = CopilotLanguage(value["language"])
    except (TypeError, ValueError) as exc:
        raise CopilotProviderError("Copilot provider language is invalid.") from exc
    if language is not request.language:
        raise CopilotProviderError("Copilot provider changed the requested language.")
    answer = value["answer"]
    if (
        not isinstance(answer, str)
        or not answer.strip()
        or len(answer.encode("utf-8")) > 16_384
        or any(ord(character) < 32 and character not in "\t\n" for character in answer)
    ):
        raise CopilotProviderError("Copilot provider answer is invalid.")
    raw_citations = value["citations"]
    if not isinstance(raw_citations, list) or len(raw_citations) > 16:
        raise CopilotProviderError("Copilot provider citations are invalid.")
    allowed_sources = {field.field_name for field in request.fields}
    citations: list[CopilotCitation] = []
    for raw_citation in raw_citations:
        if not isinstance(raw_citation, dict) or set(raw_citation) != _CITATION_KEYS:
            raise CopilotProviderError("Copilot provider citation must contain exact fields.")
        source = raw_citation["source_field"]
        title = raw_citation["title"]
        if source not in allowed_sources:
            raise CopilotProviderError("Copilot provider cited an unapproved source field.")
        try:
            citations.append(CopilotCitation(source, title))
        except Exception as exc:
            raise CopilotProviderError("Copilot provider citation is invalid.") from exc
    if len({item.source_field for item in citations}) != len(citations):
        raise CopilotProviderError("Copilot provider citation sources must be unique.")
    return CopilotProviderOutput(language, answer, tuple(citations))
