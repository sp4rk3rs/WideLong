@echo off
setlocal
if not exist "%~dp0output\WideLong-0.5.3\WideLong.exe" (
  echo Build WideLong first: powershell -ExecutionPolicy Bypass -File "%~dp0Build.ps1"
  pause
  exit /b 1
)
start "" "%~dp0output\WideLong-0.5.3\WideLong.exe"
