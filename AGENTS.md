# AGENTS.md — AI-Engineer Repository Instructions

## Mission
Build **AI-Engineer**, a safe local-first electrical-engineering copilot and integration
platform for Windows. Its primary experience is an Arabic/English conversational
palette inside AutoCAD 2021 with explicit drawing selection, reviewed plans, previewed
changes, deterministic calculations, current cited code/manufacturer evidence, and
verified execution. A shared electrical project model must later connect ETAP,
SOLIDWORKS Electrical, Excel, and other reviewed engineering tools.

Read `docs/PRODUCT_VISION.md` completely for the authoritative product scope. Preserve
the existing safe software-development agent, localhost API, VS Code, and packaging
capabilities as supporting platform services.

The user must remain in control through permissions, confirmations, backups, logging, and audit history.

## Environment
- User: Hamada Gamal
- OS: Windows 11 Pro
- Python: 3.13.14
- pip: 26.1.2
- Git: 2.54.0.windows.1
- IDE: Visual Studio Code
- AutoCAD: AutoCAD 2021 English
- Expected repo root: `D:\program\AI-Engineer`

Do not hard-code the repo path. Resolve paths from the project root.

## Target Structure
```text
AI-Engineer/
├── main.py
├── requirements.txt
├── README.md
├── .gitignore
├── AGENTS.md
├── agent/
├── api/
├── core/
├── integrations/
├── plugins/
├── workspace/
├── data/
├── logs/
├── docs/
├── tests/
└── backup/
```

Inspect the actual repository before changing anything. Do not assume files are empty.

## Architecture
- `agent/`: lifecycle, state, commands, tasks, scheduling, permissions.
- `core/`: files, terminal, processes, Git, system info, scanning.
- `api/`: localhost-only FastAPI and validated models.
- `integrations/`: capability-scoped AutoCAD, VS Code, evidence, Excel, ETAP,
  SOLIDWORKS Electrical, and future reviewed adapters.
- `plugins/`: optional extensions that must obey permissions.
- `data/`: state/settings, never secrets.
- `logs/`: operational/error logs.
- `tests/`: unit and integration tests.

Prefer official APIs, CLIs, COM, AutoLISP, or supported SDKs before GUI mouse/keyboard automation.

## Safety Rules
- Bind APIs to `127.0.0.1` by default.
- Never expose an unauthenticated command API to LAN/internet.
- Restrict file operations to configured workspace roots.
- Block path traversal and symlink escapes.
- Require confirmation for destructive/high-risk actions.
- Never use `git reset --hard`, `git clean -fd`, force push, or history rewriting without explicit approval.
- Create timestamped backups before destructive changes.
- Use command policies and timeouts; avoid `shell=True` unless justified.
- Never store secrets in the repository.
- Log actions, approvals, failures, and results.
- Do not implement uncontrolled self-modification.
- Stop and ask if unexpected user-created changes are found.

## Coding Standards
- Python 3.13 compatible.
- Type hints, docstrings, `pathlib.Path`, UTF-8, atomic JSON writes.
- Standard `logging` module.
- Project-specific exceptions with actionable messages.
- Small testable functions; avoid global mutable state.
- No `pass`, unfinished TODOs, placeholders, or fake success messages.
- Add/update tests for every implemented behavior.
- Keep dependencies minimal.

Expected dependencies may include:
```text
fastapi
uvicorn
pydantic
rich
psutil
GitPython
watchdog
pytest
```

## Roadmap
### Milestone 0 — Audit and Foundation
Implement version, config, exceptions, logging, persistent state, app lifecycle, `main.py`, valid JSON data, baseline tests, and docs.

### Milestone 1 — Safe Core
Implement sandboxed FileManager, TerminalManager with timeouts/output capture, ProjectScanner, ProcessManager, SystemInfo, GitManager, and tests.

### Milestone 2 — Command and Task Engine
Typed command registry, task states/history, cancellation, retries, approvals, bounded execution.

### Milestone 3 — Local API
FastAPI on localhost, Pydantic models, authentication token, health/status/file/command/scan/task routes, API tests.

### Milestone 4 — Developer Agent
Provider abstraction and a bounded plan → act → observe → verify loop with strict permissions and human review.

### Milestone 5 — VS Code Integration
Use the `code` CLI and the stable local API; build a dedicated extension only after the API is reliable.

### Milestone 6 — AutoCAD 2021 Integration
Detect AutoCAD, validate supported integration methods, start with non-destructive commands and temporary drawings, require backups/confirmations.

### Milestone 7 — Packaging
Portable launcher/installer, migration, recovery, and multi-PC documentation.

### Milestones 8–15 — AutoCAD verified foundation
Read-only inspection, inventory, bounded drawing primitives, reviewed symbol intake
and registration, verified circuit fragments, and hash-bound drawing handoff.

### Milestone 16 — Electrical Model and Drawing Context
Implement the typed shared electrical model and bounded read-only AutoCAD drawing and
explicit-selection context needed by every product-facing feature.

### Milestone 17 — AutoCAD Copilot Palette
Build a read-only in-product palette using the authenticated local API, explicit data
egress review, and provider-independent chat/session contracts.

### Milestone 18 — Previewed Electrical Drafting
Add heterogeneous devices, terminals, circuits, routes, previews, undoable supported-
API transactions, scratch execution, and verified handoff.

### Milestones 19–21 — Quantities, Calculations, Panels, and SLD
Add traceable takeoff, deterministic cable/voltage-drop/protection calculations,
panel schedules, and reviewed single-line diagram workflows.

### Milestones 22–23 — Live Evidence
Add version-pinned, cited code compliance plus opt-in official manufacturer data and
reviewed CAD assets without bypassing licensing or access controls.

### Milestones 24–25 — Engineering Connectors
Add ETAP and then SOLIDWORKS Electrical through the shared model, official supported
interfaces, conflict review, and live acceptance tests.

### Milestone 26 — Professional 1.0
Real-project acceptance, performance, security, recovery, documentation freeze, and
professional packaging.

## Definition of Done
A milestone is complete only when code compiles, relevant tests pass, documented run commands work, errors are logged, docs match reality, modified files and commands are reported, and limitations are stated honestly.

## Persistent Work Queue
- Read `docs/WORK_QUEUE.md` after this file at the start of every substantial task.
- Read `docs/PRODUCT_VISION.md` before changing product scope, architecture, roadmap,
  engineering models, AutoCAD UI, calculations, evidence retrieval, or connectors.
- `docs/WORK_QUEUE.md` contains unfinished work only; completed work belongs in
  `docs/ROADMAP.md` and `docs/CHANGELOG.md`.
- Keep the active milestone decomposed into independently verifiable steps.
- Remove a queue step only after its implementation, relevant tests, and required live
  verification pass. Record the result in the roadmap/changelog in the same commit.
- Add newly discovered work to the queue with its dependency and safety boundary; do
  not rely on chat memory as the only record.
- If work stops mid-milestone, leave the remaining steps accurate and ordered before
  ending the session.

## Required Workflow
For every substantial task:
1. Read this file.
2. Read and reconcile `docs/WORK_QUEUE.md`.
3. Inspect relevant files and Git status.
4. State a concise plan.
5. Implement the smallest complete milestone.
6. Run compile checks and tests.
7. Fix failures or identify a genuine blocker.
8. Review the diff and update the queue/history documents.
9. Report files changed, commands, exit codes, tests, limitations, and next task.

Do not merely provide instructions when safe implementation and verification can be completed in the repository.

## Immediate Priority
First complete the post-0.18.0 Operator Bridge Windows live acceptance documented in
`docs/TRANSFER_HANDOFF.md`: resolve/clear the v5.4 `Connecting to AI-Engineer…` state
using evidence-first health/extension/port diagnostics, then pass the read-only
`project.scan` + `file.read` acceptance test and verify no-progress loop prevention and
live UI activity. After that, continue Milestone 18 from `docs/WORK_QUEUE.md`: finish
lifecycle coverage, implement read-only preview, and only then one separately approved,
bounded, undoable supported-API AutoCAD execution path with recovery and verification.
Do not begin ETAP/SOLIDWORKS work, weaken approval gates, or rely on model memory for
safety-significant engineering calculations or code clauses.
