# AutoCAD 2023 second-PC portable setup

This package keeps the existing AutoCAD 2021 path intact and adds an explicit AutoCAD
2023 path. The AutoCAD bridge is compiled on the destination PC against that PC's exact
AutoCAD 2023 managed assemblies. It does not copy Autodesk binaries or bypass licensing.

## Prerequisites

- Windows 11 x64.
- Licensed AutoCAD 2023 (English is the tested language target).
- Python 3.13.x available as `python.exe`.
- Microsoft .NET Framework 4.8 reference assemblies. The bridge may fall back to 4.7.2
  references if 4.8 references are unavailable, and the readiness report records which
  reference set was selected.
- A short install path such as `D:\AI-Engineer`.

## First run

1. Extract the ZIP to `D:\AI-Engineer`. Do not run it from inside the ZIP.
2. Open PowerShell in that directory.
3. Install the packaged Python dependencies:

   ```powershell
   python -m pip install -r .\requirements.txt
   ```

4. Run the non-mutating readiness check:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\CHECK-AI-ENGINEER-AUTOCAD-2023.ps1
   ```

5. If it passes, copy the DWG to `D:\AI-Engineer\workspace` and start a read-only copy:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\START-AI-ENGINEER-AUTOCAD-2023.ps1 -DrawingPath .\workspace\project.dwg
   ```

For AutoCAD 2023 the launcher uses the release-specific COM identity
`AutoCAD.Application.24.2`. It copies the drawing, marks the preview read-only, binds the service to
`127.0.0.1`, builds the plugin for AutoCAD 2023, and verifies the active document before
allowing the bounded operator channel.

## Non-standard installation or incomplete check

Pass the actual installation folder to both scripts, for example:

```powershell
.\CHECK-AI-ENGINEER-AUTOCAD-2023.ps1 -AutoCADRoot 'E:\Autodesk\AutoCAD 2023'
```

The check writes `workspace\reports\autocad-2023-readiness-*.json`. Send that file back
for a capability-specific adjustment. The report contains product-file hashes and local
prerequisite status, but performs no provider egress and no drawing mutation.

## Acceptance boundary

Automated fixtures prove the 2023 target selection, expected `ACADVER` family `24.2`,
.NET reference selection, packaging, and clean installation. A real AutoCAD 2023 session
on the destination PC is still required before that PC is called supported. Begin with
read-only status/context tests; drawing mutation requires a separate reviewed Scratch
approval and must never use the original project drawing.
