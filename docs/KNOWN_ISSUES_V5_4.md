# Known Issues / Diagnostics — Operator v5.4

## 1. Last observed issue: panel stays at `Connecting to AI-Engineer…`

Status: **source defects fixed in extension 0.1.4; API-equivalent live acceptance passed;
final visual panel observation pending**.

Destination diagnostics on 2026-08-09 found no listener on port 8765 while the token was
present and extension 0.1.3 was installed. A direct bridge launch then remained alive,
returned `ok`/`0.18.0` from health, rejected an unauthenticated protected request with
401, and returned 200 for the authenticated developer-status request. The server did not
reproduce a deterministic crash.

Two confirmed defects were corrected. Windows launchers previously checked only the
`powershell -NoExit` wrapper, which could outlive a failed Python child, and declared
success without checking health. They now wait up to 15 seconds for a real loopback
health response. The extension previously posted an error without changing its initial
connection label. Extension 0.1.4 probes public health separately and reports API
unavailable, authentication failed, provider unavailable, or API/provider success.

### Diagnostic order

1. Confirm the Operator Bridge PowerShell window is still open and has not printed a
   traceback or startup failure.
2. Open `http://127.0.0.1:8765/health` in a browser.
   - If it fails: diagnose bridge/API startup or port ownership first.
   - If it succeeds: the bridge is listening; next inspect VS Code token/environment and
     extension state.
3. From Command Prompt, verify the extension version:

   `code --list-extensions --show-versions | findstr /I "ai-engineer-local.ai-engineer-operator"`

   Expected: `ai-engineer-local.ai-engineer-operator@0.1.4`.
4. Confirm there is only one intended listener on port 8765:

   `netstat -ano | findstr :8765`

5. If health works but the panel does not, capture:
   - the exact panel text;
   - the last 20 lines of the Operator Bridge window;
   - VS Code `Help > Toggle Developer Tools` Console errors;
   - the extension version output above.

Do not paste or store `AI_ENGINEER_API_TOKEN` values. Tokens are session secrets.

## 2. v5.3 no-progress loop (fixed and live-accepted in v5.4)

A live read-only run repeatedly executed the same successful payload:

`project.scan {"path":"."}`

The run reached 8/10 actions and 18/22 provider calls without progressing to useful file
reads. The user rejected the next duplicate action and cancelled the run.

v5.4 now treats an unchanged successful action as no progress unless an intervening
registered state-changing command could have changed its inputs. It also isolates VERIFY
from the broader plan/goal so a small local model evaluates the command that actually ran.
Windows run `52554b66d7c94dc28ec0091552f81ba5` completed `project.scan`, `file.read` for
`agent/app.py`, and `file.read` for `agent/command_engine.py`; all three succeeded and
verified, and the run completed without a repeated action.

## 3. UI responsiveness / activity visibility

Earlier builds gave little immediate feedback after Approve/Continue. v5.4 adds Working
state, elapsed time, progress, live activity, temporary action-button disablement, and a
visible Cancel path for non-terminal runs. The Cancel API was live-tested during active
Ollama inference: it responded in 0.039 seconds and the late provider response did not
revive the cancelled run. Visual observation of these states inside VS Code remains.

## 4. Core version versus Operator package version

Seeing `0.18.0` in API health is expected. The core semantic release remains 0.18.0 while
Operator/Hybrid development is Unreleased/post-0.18.0. The patched VS Code extension is
0.1.4.

## 5. Destination Ollama model prerequisite (resolved 2026-08-09)

The separately approved `qwen2.5-coder:7b` model is installed (4.7 GB), warmed successfully,
and passed the intended local-provider read-only acceptance. Ollama 0.32.6 reported a
4096-token context on the destination Quadro T1000 4 GB GPU. Local calls can take roughly
one to several minutes; this is performance, not permission expansion.

## 6. Pytest temporary-directory permissions

The destination inherited an inaccessible pytest temp directory. Merely changing
`TEMP`/`TMP` did not avoid enumeration of the blocked `pytest-of-Elsa3eed` directory.
Focused and full verification passed with an explicit unique `--basetemp` plus disabled
pytest cache. `SETUP-AI-ENGINEER.ps1` now creates an exact unique runtime temp path,
passes it via `--basetemp`, and removes only that path afterward.

The bounded `python.pytest` command now applies the same policy internally, preserves
only the non-secret Windows home variables required by Python/Git, disables pytest cache,
uses a unique workspace-contained basetemp, and removes read-only Git artifacts safely.
It also fails the task on a non-zero process result instead of returning a misleading
successful task. Live HIGH-gate acceptance passed with 337 tests and one skip.

## 7. AutoCAD profile boundary

Core Console fails closed with `Problem with setting up current profile` when launched
inside the Codex sandbox account. This is expected and no profile is created, copied, or
borrowed. Running the same approved `/readonly` inventory outside that sandbox as the
initialized interactive Windows user passed: AutoCAD 24.0, 1 layer, 24 entities,
`DBMOD=0`, unchanged source/scratch hashes, and no truncated output.
