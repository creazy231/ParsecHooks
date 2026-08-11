@echo off
rem Removes the logon registration and stops the app (reverting any active tweaks first).
setlocal

echo Reverting any applied display tweaks...
if exist "%~dp0bin\ParsecHooks.exe" "%~dp0bin\ParsecHooks.exe" --revert

echo Stopping parsec-hooks...
taskkill /IM ParsecHooks.exe /F >nul 2>&1

echo Removing logon registration...
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "parsec-hooks" /f >nul 2>&1
if errorlevel 1 (echo   ^(no Run value present^) ) else (echo   removed Run value)

set "LNK=%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\parsec-hooks.lnk"
if exist "%LNK%" (
    del /f /q "%LNK%"
    echo   removed legacy Startup shortcut
)

echo.
echo Uninstalled. Config and log were left in place:
echo   %~dp0bin\parsec-hooks.ini
echo   %%LOCALAPPDATA%%\parsec-hooks
exit /b 0
