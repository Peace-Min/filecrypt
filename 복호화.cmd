@echo off
title FileCrypt - º¹È£È­
powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File "%~dp0engine\simple.ps1" -Mode Decrypt -Path "%~1"
pause
