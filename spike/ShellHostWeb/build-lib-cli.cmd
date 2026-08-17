@echo off
REM MarwanOS - build the LibraryApi console harness with the inbox .NET Framework compiler.
REM
REM   build-lib-cli.cmd               -> bin\libraryapi-cli.exe
REM   build-lib-cli.cmd myname.exe    -> bin\myname.exe
REM
REM Reference list: LibraryApi.cs needs System.dll / System.Core.dll like FileApi.cs, PLUS
REM System.Drawing.dll. Drawing is used by lib.icon only - HICON -> 32bpp bitmap -> PNG - and
REM build.cmd already references it because ShellHostWeb.cs is a WinForms host, so adding
REM LibraryApi.cs there costs one source line and nothing else.
REM Registry access is Microsoft.Win32.Registry, which lives in mscorlib. No extra reference,
REM no winmd, no NuGet.
REM
REM LibraryWatch.cs and MetaApi.cs compile in too, and not optionally: LibraryApi's ScanMod asks
REM WCfg for its scan roots and its disabled-source list, and Dispatch routes lib.config.* /
REM meta.* to them, so the harness does not link without them. Same three sources as
REM build-watch-cli.cmd, different Main.

setlocal
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set HERE=%~dp0
set OUTDIR=%HERE%bin
set OUTNAME=%~1
if "%OUTNAME%"=="" set OUTNAME=libraryapi-cli.exe

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
  "%HERE%libraryapi-cli.cs"

if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)

echo BUILD OK -^> %OUTDIR%\%OUTNAME%
endlocal
