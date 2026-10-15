@echo off
rem Drag your texture zip onto this file to build your own YGOCardCrafter.exe with the textures built in.
if "%~1"=="" (
  echo Drag your texture .zip onto build-my-exe.cmd, or run: build-my-exe.cmd C:\path\to\textures.zip
  pause
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-my-exe.ps1" "%~1"
pause
