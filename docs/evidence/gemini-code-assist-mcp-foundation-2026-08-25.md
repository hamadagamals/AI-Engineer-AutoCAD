# Gemini Code Assist MCP foundation — 2026-08-25

## Scope

The engineer requested that the ordinary Gemini Code Assist Agent chat reach the
existing AI-Engineer/AutoCAD safety boundary before adding the equivalent GitHub
Copilot integration.

## Implemented boundary

- Local stdio MCP server: `integrations/gemini_mcp_server.py`.
- Gemini settings installer: `integrations/install_gemini_mcp.ps1`.
- Registered server name: `ai-engineer-autocad`.
- Initial tool: `ai_engineer_autocad_status` only.
- The tool delegates to the authenticated loopback API command
  `autocad.get_live_document_status`.
- No arbitrary AutoCAD command text, entity read, save, or drawing mutation surface
  exists in this MCP boundary.
- The MCP child receives the existing session token through process inheritance; no
  token or Gemini API key is written to its configuration.
- `GEMINI.md` instructs Gemini not to bypass the registered tool boundary with shell,
  COM, UI automation, or AutoLISP.

## Automated verification

- MCP initialization, tool listing, tool calling, notification handling, bounded
  stdio JSON, environment validation, safe DWG-name validation, and command-text
  rejection pass.
- The existing live-tool and Operator Bridge regression suites continue to pass.
- The installer passed against a disposable settings file and the real Gemini user
  settings now contain the expected server entry. Existing settings were preserved
  and a timestamped backup is created when a prior file exists.

## Live acceptance still required

Reload the isolated VS Code window, enter Gemini Agent mode, verify `/mcp` lists
`ai-engineer-autocad`, verify `/tools` lists `ai_engineer_autocad_status`, and run one
read-only active-document status request. Acceptance requires `DBMOD=0 -> 0` and
`drawing_mutation_performed=false`.
