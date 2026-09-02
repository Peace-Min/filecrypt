@echo off
title FileCrypt - ¾ÏÈ£È­
set PS1=%~dp0engine\simple.ps1
if "%~1"=="" (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File "%PS1%" -Mode Encrypt
) else (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File "%PS1%" -Mode Encrypt -Path %*
)
pause
