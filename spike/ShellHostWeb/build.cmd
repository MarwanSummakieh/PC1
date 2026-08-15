@echo off
REM ARC OS ShellHostWeb - build with the inbox .NET Framework compiler.
REM No SDK install required; csc.exe ships with Windows.
REM Output is staged into bin\ together with every DLL the exe needs at runtime.

REM Optional first argument = output file name, e.g.  build.cmd ArcShellHostWeb-v2.exe
REM Deploying under a NEW name avoids overwriting the binary a running shell holds open.
REM
REM SystemApi.cs (the native system-control layer behind the Settings screen) compiles into the
REM same binary and needs no extra /reference. Call it as ArcOs.Sys.SystemApi.Handle(json).
REM Build the standalone test harness for it with build-cli.cmd.
REM
REM FileApi.cs (the native file-operations layer behind the file explorer, ui/files.js) is the
REM same deal: no extra /reference, call it as ArcOs.Files.FileApi.Handle(json). Its namespace
REM and helper type names are distinct from SystemApi's, so the two coexist in one binary.
REM Build its standalone harness with build-file-cli.cmd.
REM
REM LibraryApi.cs (the installed-software scan behind the home rail, ui/library.js) is the third:
REM call it as ArcOs.Library.LibraryApi.Handle(json). Namespace ArcOs.Library, helper types
REM prefixed L*, so all three coexist. It is the only one that needs System.Drawing.dll - already
REM referenced below because ShellHostWeb.cs is a WinForms host - and it needs it only for
REM lib.icon, which turns an HICON or an on-disk logo into a PNG data URI.
REM Build its standalone harness with build-lib-cli.cmd.

setlocal
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set HERE=%~dp0
set OUTDIR=%HERE%bin
set SDK=%HERE%..\..\vendor\webview2
set REPO=%HERE%..\..
set OUTNAME=%~1
if "%OUTNAME%"=="" set OUTNAME=ArcShellHostWeb.exe

if not exist "%CSC%" (
  echo ERROR: inbox csc.exe not found at %CSC%
  exit /b 1
)
if not exist "%SDK%\lib\net462\Microsoft.Web.WebView2.Core.dll" (
  echo ERROR: WebView2 SDK not found under %SDK%
  exit /b 1
)
if not exist "%OUTDIR%" mkdir "%OUTDIR%"

"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /warn:4 ^
  /out:"%OUTDIR%\%OUTNAME%" ^
  /reference:System.dll ^
  /reference:System.Core.dll ^
  /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll ^
  /reference:System.IO.Compression.dll ^
  /reference:System.IO.Compression.FileSystem.dll ^
  /reference:"%SDK%\lib\net462\Microsoft.Web.WebView2.Core.dll" ^
  /reference:"%SDK%\lib\net462\Microsoft.Web.WebView2.WinForms.dll" ^
  "%HERE%ShellHostWeb.cs" ^
  "%HERE%SystemApi.cs" ^
  "%HERE%FileApi.cs" ^
  "%HERE%LibraryApi.cs"

if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)

REM Runtime layout: the loader and both managed wrappers must sit next to the exe.
copy /y "%SDK%\lib\net462\Microsoft.Web.WebView2.Core.dll"      "%OUTDIR%\" >nul
copy /y "%SDK%\lib\net462\Microsoft.Web.WebView2.WinForms.dll"  "%OUTDIR%\" >nul
copy /y "%SDK%\runtimes\win-x64\native\WebView2Loader.dll"      "%OUTDIR%\" >nul

REM The UI itself. Served from the exe directory unless --assets says otherwise.
REM ui\*.css and ui\*.js are flattened alongside index.html because that is how the
REM page links them (href="osk.css", src="files.js") and how they are deployed.
REM arcnav.js is a special case: it is never loaded by index.html at all. The host
REM reads it off this folder and injects it into every page the CONTENT WebView
REM loads, so it has to be here even though nothing links to it.
copy /y "%REPO%\index.html" "%OUTDIR%\" >nul
copy /y "%REPO%\boot.html"  "%OUTDIR%\" >nul
copy /y "%REPO%\ui\*.css"   "%OUTDIR%\" >nul
copy /y "%REPO%\ui\*.js"    "%OUTDIR%\" >nul

echo BUILD OK -^> %OUTDIR%\%OUTNAME%
endlocal
