# Hybrid AI Provider Architecture

AI-Engineer keeps reasoning providers separate from local execution. The provider may
suggest one bounded plan/action/verification object, but it never receives direct
command authority. All filesystem, test, VS Code, and AutoCAD effects remain inside the
existing command engine, permission policy, review state machine, durable approvals,
backup/recovery rules, and verification gates.

## Provider modes

| Mode | Purpose | Network | Project execution authority |
| --- | --- | --- | --- |
| `ollama` | Free local default for routine coding/reasoning | loopback only | none |
| `codex` | Stronger escalation using the signed-in Codex CLI | OpenAI via Codex | none |
| `hybrid` | Ollama first; Codex only when local provider output fails its contract | local + conditional Codex | none |
| `openai` | Optional API-backed provider | fixed OpenAI API endpoint | none |
| `gemini` | Optional Google Gemini API provider | fixed Gemini Interactions endpoint | none |
| `none` | Local bridge/tools with no AI reasoning provider | none | none |

`auto` resolves to hybrid when both Ollama and Codex are explicitly enabled, then
Ollama, then Codex, then Gemini, then OpenAI. Neither cloud API is enabled without its
own enable flag, egress confirmation, and process-environment API key. Cloud providers are never
silently combined with another mode.

## Gemini mode

Gemini uses the fixed `https://generativelanguage.googleapis.com/v1beta/interactions` endpoint,
the `x-goog-api-key` header, `store=false`, and a JSON response schema translated from the same
provider-neutral AI-Engineer contract used by Ollama. Redirects and proxies are disabled. The key
is accepted only from `GEMINI_API_KEY` after both `AI_ENGINEER_GEMINI_ENABLE=1` and
`AI_ENGINEER_GEMINI_EGRESS_CONFIRMED=1`; it is not written to a project file. The dedicated launcher
starts the isolated AutoCAD preview and its local bridge directly on default port `8766`, avoiding
the existing Operator/VS Code bridge normally using `8765`. Google REST model output is read from
the documented `steps[].content[].text` structure, and bounded provider error details are redacted
before local display.

## Local mode

The local adapter accepts only `http://127.0.0.1:<port>` and defaults to
`qwen2.5-coder:7b` with an 8192-token context budget. It uses Ollama structured-output
JSON schemas with temperature zero. Proxies and redirects are disabled for the loopback
HTTP call.

The model does not get a filesystem tool. It sees only the bounded `ProviderRequest`
or, for the AutoCAD copilot, the exact fields already approved by the egress-review
contract. AI-Engineer then validates the returned JSON again before any action can be
reviewed or executed.

## Codex mode

Codex is deliberately used as a reasoning service rather than as the local executor.
Every provider call starts `codex exec`:

- non-interactively and ephemerally;
- in a new empty temporary directory, never in the engineer's project repository;
- with a read-only sandbox;
- with user configuration and exec-policy rules ignored for deterministic isolation;
- with the Git-repository check skipped only because the temporary directory contains
  no project;
- with a phase-specific output JSON Schema;
- with an environment allowlist that excludes API keys, the AI-Engineer bridge token,
  cloud credentials, and arbitrary project environment variables.

The saved Codex/ChatGPT authentication remains outside the repository. AI-Engineer does
not copy, print, log, or persist Codex authentication material.

## Hybrid escalation

Hybrid mode spends Codex capacity only when the local provider invocation fails or the
local developer response breaks the required JSON structure. A structurally valid local
response stays local. Semantic command validation, permission checks, and actual
execution remain downstream in AI-Engineer and are never bypassed by the fallback.

This policy is intentionally conservative: a valid but mediocre local answer is not
automatically sent to Codex. Future quality scoring may add an explicit engineer-approved
escalation path without silently consuming cloud quota.

## Engineering assurance boundary

Neither local AI nor Codex is an electrical code authority. Engineering conclusions
must eventually combine:

1. structured drawing/model evidence;
2. deterministic, versioned calculations;
3. project-pinned standards/code evidence with exact edition and clause provenance;
4. manufacturer evidence where applicable;
5. AI explanation/synthesis;
6. engineer review for safety-significant decisions.

A model must never invent a standard clause from memory. If approved standards evidence
is absent, the correct result is an explicit missing-evidence finding.

## Windows launchers

- `START-AI-ENGINEER-LOCAL.bat` — checks Ollama, optionally pulls the reviewed local
  model after an explicit `PULL` confirmation, then starts the bridge and VS Code.
- `START-AI-ENGINEER-CODEX.bat` — checks the Codex CLI and requires per-session `YES`
  confirmation for provider data egress.
- `START-AI-ENGINEER-HYBRID.bat` — combines both boundaries with Ollama first.
- `START-AI-ENGINEER-WITH-OPENAI.bat` — optional API-backed mode with a process-only
  API key.
- `START-AI-ENGINEER-WITH-GEMINI.bat` — optional Gemini API mode with a process-only
  API key.

The bridge gets provider settings before launch. General Operator launchers remove cloud secrets
from the parent before starting VS Code. The dedicated Gemini launcher starts the isolated read-only
AutoCAD preview and a separate VS Code runtime profile. Only the local bridge inherits its
process-only Gemini key; the VS Code Operator receives only the loopback port and session token,
and existing VS Code windows remain independent. The local command engine remains the only
execution path.
