@echo off
setlocal
python "%~dp0launcher.py" %*
exit /b %ERRORLEVEL%
