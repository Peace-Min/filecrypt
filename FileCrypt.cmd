@echo off
title FileCrypt
rem 설치본과 이 폴더의 빌드본 중 더 새것을 띄운다.
rem 예전에는 설치본이 있으면 무조건 그걸 띄워서, 새로 빌드해도 옛 설치본이 떴다.
set INSTALLED=%LOCALAPPDATA%\Programs\FileCrypt\FileCrypt.exe
set BUILT=%~dp0gui\bin\Release\net48\FileCrypt.exe
set PICK=
for /f "usebackq delims=" %%P in (`powershell.exe -NoProfile -Command "$c=@($env:INSTALLED,$env:BUILT)|?{Test-Path -LiteralPath $_}|%%{Get-Item -LiteralPath $_}|sort LastWriteTimeUtc -Desc|select -First 1; if($c){$c.FullName}"`) do set PICK=%%P
if defined PICK (
  start "" "%PICK%"
  exit /b 0
)
echo.
echo   FileCrypt 가 아직 준비되지 않았습니다.
echo.
echo   installer\설치.cmd 를 실행하세요.
echo   (빌드가 안 돼 있으면 알아서 빌드한 뒤 설치합니다)
echo.
pause