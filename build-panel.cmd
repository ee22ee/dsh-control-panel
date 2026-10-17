@echo off
title Build DSH Panel
setlocal
cd /d "%~dp0"

set "CSC=C:\WINDOWS\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "SRC=DshPanel.cs PanelTheme.cs PanelPlugins.cs PanelUi.cs PanelExtra.cs"
set "MAN=app.manifest"
set "OUTDIR=%~dp0dist"
set "OUT=%OUTDIR%\DshPanel.exe"

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

echo [1/2] Compiling ...
"%CSC%" /nologo /target:winexe /codepage:65001 /win32manifest:"%MAN%" ^
  /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Core.dll ^
  /out:"%OUT%" %SRC%
set RC=%errorlevel%
echo.

if not "%RC%"=="0" (
  echo [X] COMPILE FAILED  ^(csc exit code %RC%^)
  goto :end
)

echo [2/2] Copying the icon next to the exe ...
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
