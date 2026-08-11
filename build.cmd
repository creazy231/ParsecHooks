@echo off
rem ---------------------------------------------------------------------------
rem Builds ParsecHooks.exe with the C# compiler that ships inside Windows.
rem No .NET SDK, no NuGet, no downloads. Because it is the .NET Framework
rem compiler, the source must stay within C# 5 (no string interpolation etc).
rem ---------------------------------------------------------------------------
setlocal

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo ERROR: could not find the in-box C# compiler ^(csc.exe^).
    echo Looked in %%WINDIR%%\Microsoft.NET\Framework64\v4.0.30319 and Framework\v4.0.30319.
    exit /b 1
)

if not exist "%~dp0bin" mkdir "%~dp0bin"

echo Compiling with %CSC%
rem /win32manifest gives us DPI awareness (so the settings dialog is sharp, not stretched)
rem and a supportedOS block (so Environment.OSVersion stops reporting Windows 8).
"%CSC%" /nologo /optimize+ /warn:4 /target:winexe /platform:anycpu ^
    /out:"%~dp0bin\ParsecHooks.exe" ^
    /win32manifest:"%~dp0app.manifest" ^
    /reference:System.dll ^
    /reference:System.Drawing.dll ^
    /reference:System.Windows.Forms.dll ^
    "%~dp0src\*.cs"

if errorlevel 1 (
    echo.
    echo BUILD FAILED
    exit /b 1
)

echo.
echo BUILD OK  -^>  %~dp0bin\ParsecHooks.exe
echo.
echo Next:  install.cmd   ^(adds a Startup shortcut and launches it^)
exit /b 0
