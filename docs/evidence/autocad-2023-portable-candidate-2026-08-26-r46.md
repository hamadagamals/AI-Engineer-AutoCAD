# AutoCAD 2023 portable candidate r46 evidence

- Archive: `ai-engineer-0.18.0-autocad-2023-portable-candidate-20260826-r46.zip`
- SHA-256: `4ae420d2816b51eff3dc05d441416b11e0a973a8f94a6ac27bbd154c173fda23`
- Archive size: 779,929 bytes
- Manifested files: 210 (including the manifest); clean-installed payload files: 209
- Application version: 0.18.0

## Automated verification

- `python -m pytest tests/test_autocad_release_readiness.py tests/test_package_manager.py tests/test_operator_bridge.py -q`
  passed: 28 tests.
- Python compileall passed for `core`, `integrations`, and `launcher.py`.
- All four affected PowerShell files parsed without syntax errors.
- AutoCAD 2021 current-PC readiness passed: 6/6 Autodesk files, 3/3 build files,
  expected `ACADVER` family `24.0`, and no provider egress or drawing mutation.
- The AutoCAD 2023 fixture passed with 6/6 Autodesk files, .NET Framework 4.8 references,
  expected `ACADVER` family `24.2`, and no provider egress or drawing mutation.
- Clean installation of r46 passed `launcher.py check`, retained all 20 exact AutoCAD
  product files, the AutoCAD 2023 COM identity, and the destination dependency instructions.

## Honest live boundary

AutoCAD 2023 is not installed on the build PC. The real-machine readiness check therefore
correctly returned incomplete with all six AutoCAD 2023 binaries absent while leaving AutoCAD
2021 readiness complete. The second PC must pass the generated readiness report and one
read-only live status/context acceptance before AutoCAD 2023 support is considered accepted.
