@echo off
set EXE=%~dp0gui\bin\Release\net48\FileCrypt.exe
if not exist "%EXE%" (
  echo FileCrypt.exe 가 없습니다. 먼저 빌드하세요:
  echo   cd gui
  echo   dotnet build -c Release
  pause
  exit /b 1
)
start "" "%EXE%"
