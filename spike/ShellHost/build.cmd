@echo off
REM ARC OS ShellHost spike - build with the inbox .NET Framework compiler.
REM No SDK install required; csc.exe ships with Windows.

REM Optional first argument = output file name, e.g.  build.cmd ArcShellHost-v2.exe
REM Deploying under a NEW name avoids overwriting the binary a running shell holds open.

setlocal
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set HERE=%~dp0
set OUTDIR=%HERE%bin
set OUTNAME=%~1
if "%OUTNAME%"=="" set OUTNAME=ArcShellHost.exe

if not exist "%CSC%" (
  echo ERROR: inbox csc.exe not found at %CSC%
  exit /b 1
)
if not exist "%OUTDIR%" mkdir "%OUTDIR%"

"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /warn:4 ^
  /out:"%OUTDIR%\%OUTNAME%" ^
  /reference:System.dll ^
  /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll ^
  "%HERE%ShellHost.cs"

if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)
echo BUILD OK -^> %OUTDIR%\%OUTNAME%
endlocal
