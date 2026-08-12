@echo off
rem Builds MonitorPower.exe with the in-box C# compiler, same as the main build.cmd.
setlocal

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo ERROR: could not find the in-box C# compiler ^(csc.exe^).
    exit /b 1
)

"%CSC%" /nologo /optimize+ /warn:4 /target:exe /platform:x64 ^
    /out:"%~dp0MonitorPower.exe" ^
    /reference:System.dll ^
    "%~dp0MonitorPower.cs"

if errorlevel 1 (
    echo.
    echo BUILD FAILED
    exit /b 1
)

echo BUILD OK  -^>  %~dp0MonitorPower.exe
exit /b 0
