@echo off
title FileCrypt
set INSTALLED=%LOCALAPPDATA%\Programs\FileCrypt\FileCrypt.exe
if exist "%INSTALLED%" (
  start "" "%INSTALLED%"
  exit /b 0
)
set BUILT=%~dp0gui\bin\Release\net48\FileCrypt.exe
if exist "%BUILT%" (
  start "" "%BUILT%"
  exit /b 0
)
echo.
echo   FileCrypt 가 아직 준비되지 않았습니다.
echo.
echo   installer\설치.cmd 를 실행하세요.
echo   (빌드가 안 돼 있으면 알아서 빌드한 뒤 설치합니다)
echo.
pause
