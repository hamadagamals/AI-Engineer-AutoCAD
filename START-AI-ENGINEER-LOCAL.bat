@echo off
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0START-AI-ENGINEER-PROVIDER.ps1" -Provider local
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo.
  echo AI-Engineer Local launcher stopped with exit code %EXITCODE%.
  pause
)
exit /b %EXITCODE%
