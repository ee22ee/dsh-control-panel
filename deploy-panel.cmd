@echo off
rem ============================================================
rem  Deploy DshPanel.exe to %LOCALAPPDATA%\DSH
rem
rem  Why this cmd wrapper exists: the DSH workspace-write sandbox only
rem  permits writes inside the workspace and temp dirs. %LOCALAPPDATA%\DSH
rem  is outside, so copying from a DSH session is denied and you would have
rem  to request danger-full-access (plus a justification) on every deploy.
rem  Double-clicking this file runs OUTSIDE the sandbox: no escalation, ever.
rem
rem  Extra args are forwarded, e.g.  deploy-panel.cmd -WhatIfOnly
rem ============================================================
setlocal
cd /d "%~dp0"

echo.
echo ============================================
echo   Deploying DshPanel
echo ============================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy-panel.ps1" -KeepBackup %*
set RC=%errorlevel%

echo.
if "%RC%"=="0" (
  echo [OK] deploy finished
) else if "%RC%"=="2" (
  echo [X] target exe is still in use - quit the panel from its tray icon, then rerun
) else (
  echo [X] deploy failed ^(exit %RC%^) - see messages above
)
echo.
pause
exit /b %RC%
