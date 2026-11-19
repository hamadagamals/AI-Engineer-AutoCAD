# AI-Engineer — Project Brief

## Goal

AI-Engineer is a safe, local-first electrical-engineering copilot and integration
platform for Windows. Its primary product experience is an Arabic/English
conversational palette inside AutoCAD 2021. The engineer can select drawing content,
ask the AI to inspect, calculate, review, or draft, preview the exact plan and changes,
approve execution, and receive traceable verified results.

The platform grows around a shared electrical project model that connects AutoCAD,
deterministic engineering calculations, quantities, panel schedules, single-line
diagrams, current code evidence, manufacturer data, and later reviewed ETAP and
SOLIDWORKS Electrical connectors. `docs/PRODUCT_VISION.md` is the authoritative
product definition.

## Product Capabilities

The finished platform should be able to:

- inspect and understand repositories;
- create, read, and update files using backups and atomic writes;
- execute approved commands with timeouts and captured output;
- diagnose failures, run tests, apply bounded fixes, and use Git safely;
- maintain task state, history, mission data, provenance, and audit logs;
- expose a protected localhost CLI/API and integrate with VS Code;
- expose bounded drawing and explicit-selection context to an AutoCAD palette;
- maintain a typed electrical model with cross-application identity;
- generate and read reviewed blocks, devices, circuits, layers, and routes;
- calculate quantities, cable sizing, voltage drop, panels, and short circuit through
  deterministic tested engines;
- retrieve current official or licensed code and manufacturer evidence with citations;
- integrate later with ETAP, SOLIDWORKS Electrical, and other reviewed tools.

## Connectivity Reality

A ChatGPT conversation does not automatically control the local computer or share its
full history with the Codex session in VS Code. Persistent product context lives in
repository documents. Cloud AI access, internet retrieval, and drawing data egress
must be explicit, bounded, reviewable, and separately configured.

## Safety Requirements

No unrestricted remote control, public command server, silent destructive operation,
unbacked drawing overwrite, arbitrary AutoCAD command, hidden cloud upload, unlicensed
code scraping, AI-invented engineering value, secret in source, or success claim
without verification.

## Engineering Direction

AutoCAD is the first production connector, not the final boundary. Read explicit
selections and drawings through supported APIs into structured bounded data; do not
pretend that a screenshot alone is an engineering model. Keep originals protected,
preview changes, use undoable transactions or verified scratch handoff, and prefer
.NET/API/COM/AutoLISP over fragile screen automation.

AI interprets requests, plans, retrieves evidence, and explains results. Versioned
tested code owns engineering formulas and rule evaluation. Every externally sourced
code clause, data sheet, property, and CAD asset needs provenance, edition/version,
date, and review. Project code editions remain pinned until the engineer approves an
upgrade.

## Collaboration Model

- ChatGPT/provider: intent understanding, planning, evidence discovery, and explanation.
- Deterministic local engines: calculations, validation, permissions, and verification.
- Repository docs: persistent product and engineering source of truth.
- Codex in VS Code: inspected, tested, local implementation.
- User/engineer: project authority, risky-action approval, and final engineering review.

## Current Milestone

Milestones 0–17 are complete, including the typed shared electrical model, bounded
read-only AutoCAD drawing/selection context, and live-accepted read-only AutoCAD copilot
palette. Milestone 18 is active: heterogeneous electrical drafting proposal/validation
and durable lifecycle foundations are implemented, while read-only preview plus one
separately approved, bounded, undoable, verified AutoCAD execution path remain unfinished.
The post-0.18.0 Operator Bridge also requires Windows live acceptance of v5.4 connection,
no-progress loop prevention, and visible local-agent activity before broader automation.
