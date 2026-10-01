@echo off
rem 지금 소스 그대로 설치 파일(setup.exe)을 만든다. 버전을 올리거나 커밋하지 않는다.
rem 결과: installer\Output\FileCrypt-Setup-<버전>.exe - 이 파일 하나만 다른 사람에게 주면 된다.
rem 버전을 올려 배포할 때는 setup만들기.cmd 를 쓴다.
title FileCrypt 설치 패키지 만들기
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-setup.ps1" -Bump none -AllowDirty -NoCommit -Open
pause
