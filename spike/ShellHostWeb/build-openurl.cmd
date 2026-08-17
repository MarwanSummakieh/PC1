@echo off
REM MarwanOS OpenUrl - the registered http/https handler.
REM
REM Built with the inbox csc.exe like everything else here: no SDK, no NuGet, no MSBuild.
REM /target:winexe on purpose - a console subsystem binary would flash a black window on
REM every clicked link, and this program has nothing to say on stdout.
REM
REM It references nothing but System.dll. It must NOT link the WebView2 assemblies: this
REM binary is registered machine-wide and gets started by anything that opens a link, so it
REM stays small enough to be obviously auditable.
REM
REM Optional first argument = output name, e.g.  build-openurl.cmd MarwanOpenUrl-v16.exe

setlocal
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set HERE=%~dp0
set OUTDIR=%HERE%bin
set OUTNAME=%~1
if "%OUTNAME%"=="" set OUTNAME=MarwanOpenUrl.exe

if not exist "%CSC%" (
  echo ERROR: inbox csc.exe not found at %CSC%
  exit /b 1
)
if not exist "%OUTDIR%" mkdir "%OUTDIR%"

"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /warn:4 ^
  /out:"%OUTDIR%\%OUTNAME%" ^
  /reference:System.dll ^
  "%HERE%OpenUrl.cs"

if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)

echo BUILD OK -^> %OUTDIR%\%OUTNAME%
endlocal
