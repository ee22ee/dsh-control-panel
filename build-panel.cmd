@echo off
title Build DSH Panel
setlocal
cd /d "%~dp0"

set "CSC=C:\WINDOWS\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "SRC=DshPanel.cs PanelTheme.cs PanelPlugins.cs PanelUi.cs PanelExtra.cs"
set "MAN=app.manifest"
set "OUTDIR=%~dp0dist"
set "OUT=%OUTDIR%\DshPanel.exe"
rem Compile to a temp file first: when the panel is running, writing DshPanel.exe
rem directly hits the image lock ("being used by another process"). deploy-panel.ps1
rem stops the process and swaps the file safely.
set "TMPOUT=%OUTDIR%\DshPanel.build.exe"

echo ============================================
echo   Building DSH control panel
echo ============================================
echo.

if not exist "%CSC%" (
  echo [X] csc.exe not found:
  echo     %CSC%
  echo     This needs the .NET Framework 4.x that ships with Windows.
  goto :end
)
if not exist "DshPanel.cs" (
  echo [X] source files not found next to this script.
  goto :end
)
if not exist "%OUTDIR%" mkdir "%OUTDIR%"

echo [1/3] Compiling ...
"%CSC%" /nologo /target:winexe /codepage:65001 /win32manifest:"%MAN%" ^
  /win32icon:"%~dp0DeepSeekHarness-WhaleGirl.ico" ^
  /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Core.dll ^
  /out:"%TMPOUT%" %SRC%
set RC=%errorlevel%
echo.

if not "%RC%"=="0" (
  echo [X] COMPILE FAILED  ^(csc exit code %RC%^)
  if exist "%TMPOUT%" del /q "%TMPOUT%"
  goto :end
)

echo [2/3] Installing into dist\ (stops a running panel first) ...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy-panel.ps1" -Source "%TMPOUT%" -Target "%OUT%" -ProcessName DshPanel
if not "%errorlevel%"=="0" (
  echo [X] INSTALL FAILED - old exe left untouched
  if exist "%TMPOUT%" del /q "%TMPOUT%"
  goto :end
)
if exist "%TMPOUT%" del /q "%TMPOUT%" >nul 2>nul

echo [3/3] Copying the icon next to the exe ...
copy /y "DeepSeekHarness-WhaleGirl.ico" "%OUTDIR%\" >nul

echo.
echo ============================================
echo   [OK] Built: %OUT%
echo ============================================
echo.
echo Just double-click dist\DshPanel.exe to start it.

:end
echo.
echo Press any key to close this window ...
pause >nul
