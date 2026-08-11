@echo off
rem ---------------------------------------------------------------------------
rem Registers ParsecHooks.exe to start at logon.
rem
rem No admin rights needed: changing display topology and HDR through the CCD API works
rem unelevated, so there is no reason to involve Task Scheduler or UAC.
rem
rem Uses HKCU\...\CurrentVersion\Run rather than a Startup-folder shortcut so that the
rem in-app Settings dialog can toggle the same thing with a checkbox. Both show up in
rem Task Manager > Startup apps.
rem ---------------------------------------------------------------------------
setlocal

set "EXE=%~dp0bin\ParsecHooks.exe"
if not exist "%EXE%" (
    echo ERROR: %EXE% not found. Run build.cmd first.
    exit /b 1
)

echo Registering for logon...
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "parsec-hooks" /t REG_SZ /d "\"%EXE%\"" /f >nul
if errorlevel 1 (
    echo ERROR: could not write the Run registry value.
    exit /b 1
)
echo   HKCU\...\CurrentVersion\Run\parsec-hooks = "%EXE%"

rem Older versions of this installer used a Startup shortcut. Remove it so the app is not
rem launched twice.
set "LNK=%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\parsec-hooks.lnk"
if exist "%LNK%" (
    del /f /q "%LNK%"
    echo   removed legacy Startup shortcut
)

echo Starting parsec-hooks...
start "" "%EXE%"

echo.
echo Installed. Look for the monitor icon in the notification area.
echo Right-click it (or double-click) for Settings.
echo   config : %~dp0bin\parsec-hooks.ini
echo   log    : %%LOCALAPPDATA%%\parsec-hooks\parsec-hooks.log
exit /b 0
