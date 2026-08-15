@echo off
REM ExtProbe - build with the inbox .NET Framework compiler (no SDK required).
setlocal
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set HERE=%~dp0
set OUTDIR=%HERE%bin
set SDK=%HERE%..\..\vendor\webview2

if not exist "%CSC%" ( echo ERROR: inbox csc.exe not found at %CSC% & exit /b 1 )
if not exist "%OUTDIR%" mkdir "%OUTDIR%"

REM /target:exe (console subsystem) so stdout redirects cleanly over SSH.
"%CSC%" /nologo /target:exe /platform:x64 /optimize+ /warn:4 ^
  /out:"%OUTDIR%\ExtProbe.exe" ^
  /reference:System.dll ^
  /reference:System.Core.dll ^
  /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll ^
  /reference:"%SDK%\lib\net462\Microsoft.Web.WebView2.Core.dll" ^
  /reference:"%SDK%\lib\net462\Microsoft.Web.WebView2.WinForms.dll" ^
  "%HERE%ExtProbe.cs"

if errorlevel 1 ( echo BUILD FAILED & exit /b 1 )

copy /y "%SDK%\lib\net462\Microsoft.Web.WebView2.Core.dll"     "%OUTDIR%\" >nul
copy /y "%SDK%\lib\net462\Microsoft.Web.WebView2.WinForms.dll" "%OUTDIR%\" >nul
copy /y "%SDK%\runtimes\win-x64\native\WebView2Loader.dll"     "%OUTDIR%\" >nul

echo BUILD OK -^> %OUTDIR%\ExtProbe.exe
endlocal
