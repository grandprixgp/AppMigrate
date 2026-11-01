@echo off
setlocal
set "SCRIPT=%~dp0Export-Apps.ps1"
set "PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%PS%" set "PS=powershell.exe"
if not exist "%SCRIPT%" (
  echo Script not found: %SCRIPT%
  pause
  exit /b 2
)
"%PS%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" %*
set "CODE=%ERRORLEVEL%"
if "%~1"=="" (
  echo.
  pause
)
exit /b %CODE%
