@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-setup.ps1" %*
pause
