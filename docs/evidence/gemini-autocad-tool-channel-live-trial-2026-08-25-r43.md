# Gemini to AutoCAD typed-tool live trial — r43 — 2026-08-25

## Prepared state

- Clean portable candidate: `dist/ai-engineer-0.18.0-autocad-beta-candidate-20260825-r43.zip`.
- Archive SHA-256:
  `003164050e2217a9cafa9d65f53e9c7c5c997b5f32eea35849bcea3b65d1dc6f`.
- Side-by-side install: `D:\AI-Engineer-r43`; r40, r41, and r42 were not replaced.
- Exact test drawing copy SHA-256:
  `1d08ccb0557e740f7d471e006d5accad0675d54b82e45d9aabc64e239ecfb117`.
- Bundled Operator version: 0.1.5.

## Verification completed before live startup

- `node --check clients/vscode/extension.js`: exit 0.
- Focused Gemini/operator/provider/package suite: 41 passed.
- Clean package install: 198 files, version 0.18.0.
- Installed `launcher.py check`: `status=ok`, 18 AutoCAD product files.
- The installed Operator contains the explicit webview-ready handshake and immediate Refresh
  feedback; it will be started in a new isolated VS Code process rather than updating an existing
  cached r42 webview.

## Live acceptance result

- The first r43 launch proved matching bridge/extension-host token fingerprints and port 8766, but
  exposed that VS Code did not execute the nonce-protected inline webview script. The bridge itself
  remained healthy and AutoCAD remained open and unchanged.
- Operator 0.1.6 moves the interactive webview code to a packaged local resource allowed by a
  restrictive CSP and registers the extension-side message listener before assigning the page.
- The corrected panel automatically displayed `API ok - 0.18.0 - provider gemini` without Refresh.
- The engineer submitted and approved exactly this bounded goal:
  `تحقق من حالة ملف AutoCAD المفتوح فقط باستخدام الأداة المسجلة، ولا تقرأ محتوى الرسم ولا تعدله.`
- Gemini proposed only `autocad.get_live_document_status`; the run completed with one action,
  four bounded provider calls, and accepted verification.
- Run ID: `967b3c3360274e75b9734e98e0f9e5fb`.
- AutoCAD result SHA-256:
  `91e412229a0dc1cd828bd1e8d2d9188691655fde378d0756c77b9e321e005eb0`.
- Source drawing SHA-256 remained
  `1d08ccb0557e740f7d471e006d5accad0675d54b82e45d9aabc64e239ecfb117`.
- `DBMOD=0 -> 0`, `drawing_mutation_performed=false`, no drawing content was read, and the provider
  observation contained no local path.

## Clean packaged successor

- Candidate r44 contains Operator 0.1.6 from installation/startup rather than the live r43 overlay.
- Archive SHA-256:
  `b0dfc6a51e35011541f5531b3b6a2d0a6d248793142b0c01be93d996540b94b8`.
- Clean package install contains 201 files; installed readiness returns `status=ok`, version 0.18.0,
  and 18 AutoCAD product files.
