@echo off
setlocal
PowerShell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-service.ps1" %*
exit /b %errorlevel%