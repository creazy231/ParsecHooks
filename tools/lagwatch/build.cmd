@echo off
rem Builds LagWatch.exe with the in-box C# compiler, same as the main build.cmd.
rem Console app, so run it from a terminal and watch the timeline live.
setlocal

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo ERROR: could not find the in-box C# compiler ^(csc.exe^).
    exit /b 1
)

"%CSC%" /nologo /optimize+ /warn:4 /target:exe /platform:x64 ^
    /out:"%~dp0LagWatch.exe" ^
    /reference:System.dll ^
    "%~dp0LagWatch.cs"

if errorlevel 1 (
    echo.
    echo BUILD FAILED
    exit /b 1
)

echo BUILD OK  -^>  %~dp0LagWatch.exe
exit /b 0
