@echo off
title Build DSH Panel
setlocal
cd /d "%~dp0"

set "CSC=C:\WINDOWS\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "SRC=AssemblyInfo.cs DshPanel.cs PanelTheme.cs PanelPlugins.cs PanelUi.cs PanelExtra.cs PanelHost.cs"
set "MAN=app.manifest"
set "OUTDIR=%~dp0dist"
set "OUT=%OUTDIR%\DshPanel.exe"
rem Compile to a temp file first: when the panel is running, writing DshPanel.exe
rem directly hits the image lock ("being used by another process"). deploy-panel.ps1
rem stops the process and swaps the file safely.
set "TMPOUT=%OUTDIR%\DshPanel.build.exe"

rem WebView2 SDK（随项目一起提交，见 lib\README.txt）
set "WV2LIB=%~dp0lib"
set "WV2REF=/r:"%WV2LIB%\Microsoft.Web.WebView2.Core.dll" /r:"%WV2LIB%\Microsoft.Web.WebView2.WinForms.dll""
rem 界面资源：全部嵌进 exe，运行时由 PanelHost 拦截 https://panel.local/* 直接回内存流
set "UIRES=/resource:ui\index.html,ui.index.html /resource:ui\style.css,ui.style.css /resource:ui\app.js,ui.app.js"

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
if not exist "%WV2LIB%\Microsoft.Web.WebView2.Core.dll" (
  echo [X] WebView2 SDK not found in lib\
  echo     See lib\README.txt
  goto :end
)
if not exist "%OUTDIR%" mkdir "%OUTDIR%"

echo [1/4] Compiling ...
"%CSC%" /nologo /target:winexe /codepage:65001 /win32manifest:"%MAN%" ^
  /win32icon:"%~dp0DeepSeekHarness-WhaleGirl.ico" ^
  /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Core.dll ^
  %WV2REF% %UIRES% ^
  /out:"%TMPOUT%" %SRC%
set RC=%errorlevel%
echo.

if not "%RC%"=="0" (
  echo [X] COMPILE FAILED  ^(csc exit code %RC%^)
  if exist "%TMPOUT%" del /q "%TMPOUT%"
  goto :end
)

echo [2/4] Installing into dist\ (stops a running panel first) ...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy-panel.ps1" -Source "%TMPOUT%" -Target "%OUT%" -ProcessName DshPanel
if not "%errorlevel%"=="0" (
  echo [X] INSTALL FAILED - old exe left untouched
  if exist "%TMPOUT%" del /q "%TMPOUT%"
  goto :end
)
if exist "%TMPOUT%" del /q "%TMPOUT%" >nul 2>nul

echo [3/4] Copying the icon next to the exe ...
copy /y "DeepSeekHarness-WhaleGirl.ico" "%OUTDIR%\" >nul

echo [4/4] Copying WebView2 runtime files next to the exe ...
copy /y "%WV2LIB%\Microsoft.Web.WebView2.Core.dll" "%OUTDIR%\" >nul
copy /y "%WV2LIB%\Microsoft.Web.WebView2.WinForms.dll" "%OUTDIR%\" >nul
copy /y "%WV2LIB%\WebView2Loader.dll" "%OUTDIR%\" >nul

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
