# AI-Engineer Operator for VS Code

This dependency-free extension is a localhost UI for the existing reviewed developer
agent. It never receives an OpenAI key and never contacts the internet. It reads only
`AI_ENGINEER_API_TOKEN` and `AI_ENGINEER_API_PORT` from the VS Code process environment
and talks to `127.0.0.1`.

The panel can create a reviewed developer run, inspect the exact plan/action payload,
approve or reject it, decide a durable high-risk command approval, advance the state
machine, and cancel the run. `Auto-approve LOW/MODERATE reads` is opt-in and never
auto-approves HIGH or DESTRUCTIVE actions.

The local operator server must already be running. Connection failures are reported as
API unavailable, authentication failed, or provider unavailable instead of leaving the
panel indefinitely in its initial connecting state. The OpenAI provider, when enabled,
is owned by the Python process and requires its separate environment-level egress opt-in.


Installation is performed through the bundled VSIX using `code --install-extension`, then verified with `code --list-extensions`.
