# AutoCAD release-readiness evidence — 2026-08-12

## Scope

This is a read-only local preflight for the AutoCAD-only AI-Engineer beta candidate. It starts
neither AutoCAD nor the localhost API, calls no provider, changes no drawing, installs nothing,
and does not claim real-project or paid manufacturer-search acceptance.

Command:

```powershell
python launcher.py autocad-readiness
```

## Result

- Readiness version: `autocad.release_readiness.v1`
- Complete: `true`
- Platform: `Windows`
- Python: `3.13.14`
- AutoCAD root: `C:\Program Files\Autodesk\AutoCAD 2021`
- AutoCAD 2021 required files: `6/6`
- .NET build files: `3/3`
- Localhost port `8765`: available
- Packaged AutoCAD product files: `7/7`
- `provider_egress_performed=false`
- `drawing_mutation_performed=false`

Product-source identities:

| File | SHA-256 |
|---|---|
| `START-AI-ENGINEER-AUTOCAD-PREVIEW.ps1` | `e29e400a3cc9179239ac1e98886d14921f279ccf67bf102c0803316f71ea2117` |
| `START-AI-ENGINEER-AUTOCAD-COMPLIANCE.ps1` | `77eed10cf9a53731999fe286b1d1cc5d6dac2497b2a8dfee17a71179db777c7b` |
| `START-AI-ENGINEER-AUTOCAD-MANUFACTURER.ps1` | `c8d9ac181039310439d0546d1009a5b1f538973b11ebeec2000829c80157ffc7` |
| `START-AI-ENGINEER-AUTOCAD-DISCOVERY.ps1` | `b4fb1e2c7575cbec678545f79849c43e01a3fc5dba726f6bc032708e72c3e278` |
| `integrations/autocad_context_plugin/build.ps1` | `7fe1b4c3418c0dcd7b86a67a6800787702de27de9a2ae0245b981e10752470be` |
| `integrations/autocad_context_plugin/PaletteCommands.cs` | `aaa1b7aa94dc8b07fb806cd854cf8685cfe853e2718a61712472169eb590853c` |
| `integrations/manufacturer_templates/free-candidates.template.json` | `bfed65160362858df8d9b42dcc5b1858c3f038530a4819d22fb42da257e60425` |

## Limitations

`complete=true` means only that fixed local prerequisites and source identities were present and
the default port was free at inspection time. It is not a publisher signature, performance test,
real-project acceptance, product approval, code approval, paid-provider acceptance, or drawing
execution authorization.
