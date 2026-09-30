@echo off
rem 암호화.cmd / 복호화.cmd 가 함께 쓰는 실행기. FC_MODE = Encrypt 또는 Decrypt
if /i "%FC_MODE%"=="Encrypt" (title FileCrypt - 암호화) else (title FileCrypt - 복호화)
set PS1=%~dp0simple.ps1
if "%~1"=="" (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File "%PS1%" -Mode %FC_MODE%
) else (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File "%PS1%" -Mode %FC_MODE% -Path %*
)
pause
