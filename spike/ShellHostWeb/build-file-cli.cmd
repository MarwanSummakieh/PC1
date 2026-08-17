@echo off
REM MarwanOS - build the FileApi console harness with the inbox .NET Framework compiler.
REM
REM   build-file-cli.cmd               -> bin\fileapi-cli.exe
REM   build-file-cli.cmd myname.exe    -> bin\myname.exe
REM
REM Note the reference list: FileApi.cs needs NOTHING beyond System.dll / System.Core.dll.
REM No WebView2, no winmd, no NuGet. The same source line drops straight into build.cmd.

setlocal
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set HERE=%~dp0
set OUTDIR=%HERE%bin
set OUTNAME=%~1
if "%OUTNAME%"=="" set OUTNAME=fileapi-cli.exe

if not exist "%CSC%" (
  echo ERROR: inbox csc.exe not found at %CSC%
  exit /b 1
)
if not exist "%OUTDIR%" mkdir "%OUTDIR%"

"%CSC%" /nologo /target:exe /platform:x64 /optimize+ /warn:4 ^
  /out:"%OUTDIR%\%OUTNAME%" ^
  /reference:System.dll ^
  /reference:System.Core.dll ^
  "%HERE%FileApi.cs" ^
  "%HERE%fileapi-cli.cs"

if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)

echo BUILD OK -^> %OUTDIR%\%OUTNAME%
endlocal
