@echo off
chcp 65001 >nul
title AI-Engineer Setup Wizard
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0SETUP-AI-ENGINEER.ps1"
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [ERROR] Setup encountered an issue.
    pause
)

