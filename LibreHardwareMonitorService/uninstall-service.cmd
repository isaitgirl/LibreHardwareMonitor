@echo off
setlocal
PowerShell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall-service.ps1" %*
exit /b %errorlevel%