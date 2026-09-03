@echo off
title FileCrypt - º¹È£È­
set PS1=%~dp0simple.ps1
if "%~1"=="" (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File "%PS1%" -Mode Decrypt
) else (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File "%PS1%" -Mode Decrypt -Path %*
)
pause
