@echo off
REM ARC OS - build the SystemApi console harness with the inbox .NET Framework compiler.
REM
REM   build-cli.cmd                -> bin\systemapi-cli.exe
REM   build-cli.cmd myname.exe     -> bin\myname.exe
REM
REM Note the reference list: SystemApi.cs needs NOTHING beyond System.dll / System.Core.dll.
REM No WebView2, no winmd, no NuGet. The same two source lines drop straight into build.cmd:
REM   add  "%HERE%SystemApi.cs"  to the csc invocation there and the shell host gets the API.

setlocal
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set HERE=%~dp0
set OUTDIR=%HERE%bin
set OUTNAME=%~1
if "%OUTNAME%"=="" set OUTNAME=systemapi-cli.exe

if not exist "%CSC%" (
  echo ERROR: inbox csc.exe not found at %CSC%
  exit /b 1
)
if not exist "%OUTDIR%" mkdir "%OUTDIR%"

"%CSC%" /nologo /target:exe /platform:x64 /optimize+ /warn:4 ^
  /out:"%OUTDIR%\%OUTNAME%" ^
  /reference:System.dll ^
  /reference:System.Core.dll ^
  "%HERE%SystemApi.cs" ^
  "%HERE%systemapi-cli.cs"

if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)

echo BUILD OK -^> %OUTDIR%\%OUTNAME%
endlocal
