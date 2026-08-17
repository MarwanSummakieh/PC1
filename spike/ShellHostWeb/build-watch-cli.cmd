@echo off
REM MarwanOS - build the LibraryWatch + MetaApi console harness with the inbox compiler.
REM
REM   build-watch-cli.cmd               -> bin\libwatch-cli.exe
REM   build-watch-cli.cmd myname.exe    -> bin\myname.exe
REM
REM LibraryWatch.cs and MetaApi.cs both build on MarwanOs.Library's LJ / LApi / LKv / LJobs /
REM LibEntry / Sources / ScanMod, so LibraryApi.cs compiles in alongside them. System.Drawing is
REM referenced only because LibraryApi.cs needs it for lib.icon; neither new file uses it.
REM MetaApi's HTTP is System.Net.HttpWebRequest, which lives in System.dll - no extra reference,
REM no NuGet, no SDK.

setlocal
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set HERE=%~dp0
set OUTDIR=%HERE%bin
set OUTNAME=%~1
if "%OUTNAME%"=="" set OUTNAME=libwatch-cli.exe

if not exist "%CSC%" (
  echo ERROR: inbox csc.exe not found at %CSC%
  exit /b 1
)
if not exist "%OUTDIR%" mkdir "%OUTDIR%"

"%CSC%" /nologo /target:exe /platform:x64 /optimize+ /warn:4 ^
  /out:"%OUTDIR%\%OUTNAME%" ^
  /reference:System.dll ^
  /reference:System.Core.dll ^
  /reference:System.Drawing.dll ^
  "%HERE%LibraryApi.cs" ^
  "%HERE%LibraryWatch.cs" ^
  "%HERE%MetaApi.cs" ^
  "%HERE%libwatch-cli.cs"

if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)

echo BUILD OK -^> %OUTDIR%\%OUTNAME%
endlocal
