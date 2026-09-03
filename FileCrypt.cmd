@echo off
title FileCrypt
set EXE=%~dp0gui\bin\Release\net48\FileCrypt.exe
if exist "%EXE%" (
  start "" "%EXE%"
  exit /b 0
)
echo.
echo   FileCrypt.exe 가 아직 빌드되지 않았습니다.
echo.
echo   아래를 한 번만 실행하세요:
echo       cd "%~dp0gui"
echo       dotnet build -c Release
echo.
echo   또는 Visual Studio 로 gui\FileCrypt.csproj 를 열어 빌드하세요.
echo.
pause
