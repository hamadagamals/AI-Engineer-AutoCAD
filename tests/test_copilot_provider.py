"""Tests for strict read-only copilot provider input and output."""

from __future__ import annotations

import json
import threading

import pytest

from agent.copilot import CopilotLanguage, create_egress_field
from agent.copilot_provider import CopilotProviderAdapter, CopilotProviderRequest
from agent.exceptions import CopilotProviderError


def _request() -> CopilotProviderRequest:
    return CopilotProviderRequest(
        "a" * 32,
        "b" * 32,
        "c" * 32,
        "d" * 64,
        CopilotLanguage.ENGLISH,
        (create_egress_field("question.text", "How many devices?"),),
    )


def test_provider_returns_only_bounded_answer_and_approved_citations() -> None:
    requests: list[CopilotProviderRequest] = []

    def generate(request: CopilotProviderRequest) -> str:
        requests.append(request)
        return json.dumps(
            {
                "language": "en",
                "answer": "The approved context asks about device count.",
                "citations": [
                    {"source_field": "question.text", "title": "Approved question"}
                ],
            }
        )

    output = CopilotProviderAdapter("provider:test", generate).generate(_request())

    assert output.answer.startswith("The approved")
    assert output.citations[0].source_field == "question.text"
    assert requests[0].fields[0].value == "How many devices?"
    assert not hasattr(requests[0], "command")


@pytest.mark.parametrize(
    ("value", "message"),
    [
        ({"language": "en", "answer": "ok", "citations": [], "command": "LINE"}, "exact"),
        ({"language": "ar", "answer": "ok", "citations": []}, "language"),
        (
            {
                "language": "en",
                "answer": "ok",
                "citations": [{"source_field": "drawing.full", "title": "Drawing"}],
            },
            "unapproved",
        ),
    ],
)
def test_provider_rejects_commands_language_changes_and_unapproved_citations(
    value: dict[str, object], message: str
) -> None:
    provider = CopilotProviderAdapter("provider:test", lambda request: json.dumps(value))

    with pytest.raises(CopilotProviderError, match=message):
        provider.generate(_request())


def test_provider_timeout_fails_without_accepting_late_output() -> None:
    release = threading.Event()

    def generate(request: CopilotProviderRequest) -> str:
        release.wait(2)
        return json.dumps({"language": "en", "answer": "late", "citations": []})

    provider = CopilotProviderAdapter(
        "provider:test", generate, timeout_seconds=1, maximum_concurrent_calls=1
    )
    try:
        with pytest.raises(CopilotProviderError, match="timed out"):
            provider.generate(_request())
    finally:
        release.set()
