# Cross-computer handoff

## Exact continuation point

This handoff was prepared on 2026-08-06 after completing and live-verifying Milestone
17. The release version is `0.18.0`; the next work is Milestone 18 in
`docs/WORK_QUEUE.md`. That queue, `AGENTS.md`, and `docs/PRODUCT_VISION.md` are the
authoritative restart context. Do not rely on chat history.

Completed through this checkpoint:

- safe local development platform and authenticated loopback API;
- AutoCAD 2021 read-only inspection and bounded verified drafting foundations;
- reviewed electrical symbols, circuit fragments, and hash-bound drawing handoff;
- immutable shared electrical model and explicit drawing/selection context;
- Arabic/English read-only AutoCAD copilot palette, local sessions, explicit egress
  preview/approval, and a strict injected-provider answer boundary;
- live palette acceptance with three selected objects, `DBMOD=0`, security restoration,
  provider egress disabled, and unchanged bytes on a controlled read-only DWG.

Known limitations and safety notes:

- no real AI provider is configured by default;
- copilot sessions are memory-only and disappear when the API restarts;
- Milestone 18 must not write to an engineer drawing until its typed proposal, preview,
  approval, rollback, and verified execution boundaries are implemented and tested;
- interactive AutoCAD rewrote writable acceptance copies despite `DBMOD=0`; use
  disposable/read-only fixtures until that host behavior is isolated;
- the old computer had one test-started AutoCAD process (`PID 14856`) that could not be
  stopped from the restricted session; close it manually on that computer. It does not
  affect the transferred repository.

## Which artifact to use

- Use `AI-Engineer-0.18.0-full.bundle` to continue development with full Git history.
- Use `ai-engineer-0.18.0.zip` for a clean portable runtime installation.
- Verify both against `SHA256SUMS.txt` after copying them to the flash drive and again
  after copying them to the destination computer.

## Continue development from the Git bundle

```powershell
git bundle verify .\AI-Engineer-0.18.0-full.bundle
git clone .\AI-Engineer-0.18.0-full.bundle D:\program\AI-Engineer
Set-Location D:\program\AI-Engineer
python -m pip install -r requirements.txt
python launcher.py check
python -m pytest -q
git status --short
```

Then read, in order:

1. `AGENTS.md`
2. `docs/TRANSFER_HANDOFF.md`
3. `docs/WORK_QUEUE.md`
4. `docs/PRODUCT_VISION.md`

Start with Milestone 18 queue item 1. Inspect the actual repository and Git status
before changing anything.

## Install only the portable runtime

```powershell
python .\ai-engineer-0.18.0.zip install .\ai-engineer-0.18.0.zip D:\AI-Engineer --confirm
Set-Location D:\AI-Engineer
python -m pip install -r requirements.txt
python launcher.py check
```

The portable ZIP deliberately excludes `.git`, tests, local workspaces, logs, backups,
approvals, API tokens, and machine-specific AutoCAD binaries. The Git bundle is the
correct artifact for continued development.
