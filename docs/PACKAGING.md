# Packaging, migration, and recovery

## Scope and prerequisites

Milestone 7 creates a source-based portable ZIP for trusted Windows computers. It is
not a compiled executable or unattended installer. Each destination requires:

- Windows 11 or another reviewed Windows environment;
- CPython 3.13.x available as `python`;
- the dependencies in `requirements.txt` installed by the operator;
- a private, local API token generated separately whenever the API is used.

Never copy an API token into the ZIP, repository, settings, command history, or a
recovery snapshot. The packaged runtime JSON is freshly generated and contains no
local approvals, task/developer history, lifecycle counters, logs, backups, or
workspace files.

## Build and verify a package

From a reviewed, clean source checkout:

```powershell
python launcher.py check
python -m compileall -q agent core api integrations main.py launcher.py
python -m pytest -q
python launcher.py package --output dist/ai-engineer-0.18.0.zip
```

The output path must not already exist. The builder reads only explicit root files
and the `agent`, `api`, `core`, `docs`, `integrations`, and `plugins` trees. Source
links, oversized inputs, and missing required sources are rejected. ZIP entries use
stable metadata and a `package_manifest.json` file records the application version,
Python requirement, and SHA-256 digest of every payload file.

SHA-256 validation detects accidental corruption and content modification. It is not
a digital signature: a malicious party able to replace both files and manifest can
produce internally consistent hashes. Transfer the archive through a trusted private
channel and review its source provenance separately.

## Install on another computer

Copy only the ZIP, then choose a destination that does not exist. The ZIP contains a
fixed `__main__.py`, so Python can run its validated installer without an existing
AI-Engineer checkout:

```powershell
python ai-engineer-0.18.0.zip install ai-engineer-0.18.0.zip D:\AI-Engineer --confirm
Set-Location D:\AI-Engineer
python -m pip install -r requirements.txt
python launcher.py check
python launcher.py autocad-readiness
```

The production package intentionally excludes the source test suite. Run the complete tests
before building the archive; after installation, `launcher.py check` validates Python/runtime
schemas plus the required AutoCAD launchers, plugin build source, palette source, and packaged
manufacturer template. The package manifest supplies per-file integrity validation during install.
`autocad-readiness` additionally reports Windows/Python, fixed AutoCAD 2021 and .NET build
dependencies, exact AutoCAD product-source SHA-256 identities, and whether the selected localhost
port is free. It is read-only and reports provider-egress/drawing-mutation flags as false.

AutoCAD 2021's .NET Framework file operations still encounter legacy path-length limits in parts of
the operator channel. Install the portable package in a short location such as `D:\AI-Engineer`.
The AutoCAD Preview launcher calculates the longest startup status path and fails before opening
AutoCAD when it would reach 260 characters, instead of allowing a partial channel startup.

The current locally installed AutoCAD beta candidate identity and its explicit remaining gates are
recorded in `docs/evidence/autocad-beta-package-candidate-2026-08-13.md`. That evidence is generated
after the immutable archive; package-identity evidence files are explicitly excluded from portable
archives so a new candidate cannot embed a stale or self-referential package identity.

The installer requires `--confirm`, rejects filesystem-root or existing
destinations, and validates all names and hashes before writing. It creates one new destination
with permissions inherited from its parent, removes that entire new destination if any write fails,
and never merges into or overwrites an existing installation. A successful clean install creates empty
`workspace`, `logs`, and `backup` runtime directories after payload validation; they are not archive
payload entries and contain no source-project or user data.

For ordinary startup:

```powershell
python launcher.py run
```

For the protected localhost API, generate a new token on that computer and keep it
only in the process environment:

```powershell
$bytes = New-Object byte[] 32
[Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
$env:AI_ENGINEER_API_TOKEN = [Convert]::ToBase64String($bytes)
python launcher.py api --check
python launcher.py api
```

The API remains bound to `127.0.0.1`; do not proxy, tunnel, or expose it to a LAN or
the public internet.

## Runtime recovery

Create a private snapshot before a risky maintenance operation:

```powershell
python launcher.py snapshot
```

The default location is `backup/snapshots`. A custom new path can be supplied with
`--output`. The snapshot contains only these schema-v1 JSON files when present:

- `agent_state.json`;
- `approvals.json`;
- `developer_runs.json`;
- `history.json`;
- `mission.json`;
- `settings.json`.

Secret-like JSON keys are rejected recursively. This is an additional guard, not a
classification system: goals, task results, mission descriptions, and other text can
still be confidential. Do not email, publish, or commit snapshots.

Restore only a trusted snapshot and explicitly confirm:

```powershell
python launcher.py restore backup\snapshots\ai-engineer-recovery-0.18.0-<timestamp>.zip --confirm
```

Restore validates the whole archive before mutation. Current runtime files are first
copied to `backup/recovery/<timestamp>-<id>`, then restored atomically. A failed write
rolls back files written by that restore. Keep both archives until the application
passes `python launcher.py check` and its tests.

## Migration

The migration command supports incomplete or older application-version instances of
the documented schema-v1 layout. It creates missing approvals, developer-run, and
history documents and updates `agent_state.json` to the running application version.

Preview is implicit: if nothing needs changing, this command succeeds without
confirmation and returns an empty `changed_files` list:

```powershell
python launcher.py migrate
```

When changes are pending it fails safely until repeated with confirmation:

```powershell
python launcher.py migrate --confirm
python launcher.py check
```

Before mutation, migration creates `backup/migrations/pre-<version>-<timestamp>-<id>.zip`.
Partial writes roll back. Unknown schema versions and invalid JSON fail closed; they
require a separately designed and tested migration rather than manual field guessing.

## Recovery limits

- Package installation does not move an existing workspace or merge data.
- Recovery archives cover runtime JSON only, not source, drawings, logs, or arbitrary
  workspace content.
- There is no cryptographic publisher signature or automatic update channel.
- There is no elevated service, Registry modification, GUI automation, or public
  network deployment.
- AutoCAD 2021 must be installed and initialized independently on each computer; its
  licensed binaries and profile are never packaged.
