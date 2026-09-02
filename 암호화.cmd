@echo off
title FileCrypt - ¾ÏÈ£È­
powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File "%~dp0engine\simple.ps1" -Mode Encrypt -Path "%~1"
pause
