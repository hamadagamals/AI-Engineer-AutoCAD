@echo off
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0START-AI-ENGINEER-WITH-GEMINI.ps1"
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo.
  echo AI-Engineer Gemini launcher stopped with exit code %EXITCODE%.
  echo Send the visible error for review. Do not send your API key.
  pause
)
exit /b %EXITCODE%
