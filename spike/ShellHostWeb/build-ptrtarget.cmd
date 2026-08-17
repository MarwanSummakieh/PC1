@echo off
REM ptrtarget.exe - the bench target for the host's pointer mode. See ptrtarget.cs.
REM Nothing in the shell references it; it is built and deployed only for a verification run.
setlocal
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set HERE=%~dp0
set OUTDIR=%HERE%bin
if not exist "%OUTDIR%" mkdir "%OUTDIR%"

"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /warn:4 ^
  /out:"%OUTDIR%\ptrtarget.exe" ^
  /reference:System.dll ^
  /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll ^
  "%HERE%ptrtarget.cs"

if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)
echo BUILD OK -^> %OUTDIR%\ptrtarget.exe
endlocal
