<#
.SYNOPSIS
    Removes Microsoft Edge as a browser on this machine: de-registers it, hides it,
    stops it launching, and optionally uninstalls it -- without touching the WebView2
    runtime the MarwanOS shell is built on.

.DESCRIPTION
    THE ONE THING TO UNDERSTAND BEFORE RUNNING THIS.
    MarwanOS renders in WebView2, and the WebView2 runtime IS Edge's engine. It is a
    SEPARATE installation from the Edge browser --
        Edge browser :  %ProgramFiles(x86)%\Microsoft\Edge\Application\<ver>\msedge.exe
        WebView2     :  %ProgramFiles(x86)%\Microsoft\EdgeWebView\Application\<ver>\msedgewebview2.exe
    -- and everything here targets the first path only. If the second one goes, the
    console has no shell at all. Every step below is keyed to the literal string
    "msedge.exe" or to Edge's own install directory, never to a wildcard that would
    also match msedgewebview2.exe, and the script REFUSES to uninstall anything if it
    cannot first confirm the WebView2 runtime is installed independently.

    WHAT IT DOES BY DEFAULT (no -Uninstall):
      1. Backs up the registry keys it is about to delete, to C:\MarwanOS\backup\.
      2. Deletes Edge's browser registration:
           HKLM\SOFTWARE\RegisteredApplications\Microsoft Edge
           HKLM\SOFTWARE\Clients\StartMenuInternet\Microsoft Edge
         After this Edge no longer claims http/https and no longer appears in Windows'
         "Default apps" list as a browser.
      3. Deletes the machine-wide shortcuts (public desktop, common Start Menu).
      4. Blocks msedge.exe from starting, via an Image File Execution Options "Debugger"
         entry -- the documented mechanism by which Windows starts a named program
         under another program. Pointed at systray.exe, which exits immediately, so a
         launch attempt ends silently instead of opening a browser. Keyed to msedge.exe
         alone; msedgewebview2.exe has no such key and is unaffected.
      5. Tells the Edge updater not to reinstall the browser:
           HKLM\SOFTWARE\Policies\Microsoft\EdgeUpdate\InstallDefault = 0
           HKLM\SOFTWARE\Policies\Microsoft\EdgeUpdate\Install{56EB18F8-...} = 0   (Edge Stable)
         The WebView2 client GUID is deliberately NOT given an Install=0 policy, so the
         runtime the shell depends on can still be serviced. The script prints every
         client GUID the updater actually knows about rather than trusting this comment.

    WITH -Uninstall it additionally runs Edge's own uninstaller:
        <EdgeDir>\Installer\setup.exe --uninstall --system-level --verbose-logging --force-uninstall
    This is Microsoft's own binary doing its own removal. Be honest about what it is
    worth: on some Windows 11 builds the inbox Edge refuses this and returns without
    removing anything, and a cumulative update can put Edge back afterwards. The script
    reports what actually happened by checking whether msedge.exe is still on disk --
    the exit code is treated as a claim, not as evidence, the same rule the install
    broker uses. Steps 2-5 are what make the outcome stick either way, which is why
    they run first and are not skipped when -Uninstall is given.

    WHAT IT DOES NOT DO:
      * It does not touch msedgewebview2.exe, the EdgeWebView directory, or the
        WebView2 client's update policy.
      * It does not disable the MicrosoftEdgeUpdate service or its scheduled tasks.
        Doing so would freeze the WebView2 runtime at its current version too, which is
        a security decision about the shell's own engine and belongs in its own change.
      * It does not remove per-user taskbar or Start pins. Those live in each profile
        and are removed by the profile's owner.

.CONTEXT
    Asked for by the user 2026-08-16: "remove edge as a browser and make the one we
    made the default browser". The other half is 21-set-default-browser.ps1, which
    should be applied FIRST -- a machine with no registered browser at all is a machine
    where a clicked link goes nowhere. NOT YET APPLIED.

.UNDO
    provisioning\88-restore-edge-browser.ps1  (a full -Uninstall can only be undone by
    reinstalling Edge from Microsoft; everything else is restored from the backup.)

.NOTES
    Run in an ELEVATED PowerShell. Dry run by default; -Apply to write.
#>

[CmdletBinding()]
param(
    [switch] $Apply,

    # Also run Edge's own uninstaller. Refused unless the WebView2 runtime is confirmed
    # installed in its own directory.
    [switch] $Uninstall,

    [string] $BackupDir = "C:\MarwanOS\backup",
    [string] $LogPath   = "C:\MarwanOS\remove-edge.log"
)

$ErrorActionPreference = "Stop"

# Edge Stable's app GUID in the Edge updater. Printed against the machine's actual
# client list below rather than assumed -- if it is not there, the script says so.
$EdgeStableGuid = "{56EB18F8-B008-4CBD-B6D2-8C97FE7E9062}"

function Line($m) {
    $stamp = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
    Write-Host $m
    try { Add-Content -LiteralPath $LogPath -Value ("{0}  {1}" -f $stamp, $m) -ErrorAction SilentlyContinue } catch {}
}

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$pr = New-Object Security.Principal.WindowsPrincipal($id)
if (-not $pr.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
    Write-Host "ERROR: run this in an ELEVATED PowerShell (Administrator). Nothing was changed." -ForegroundColor Red
    exit 2
}

try { New-Item -ItemType Directory -Force (Split-Path $LogPath) -ErrorAction SilentlyContinue | Out-Null } catch {}
Line "=== 22-remove-edge-browser.ps1  (Apply=$Apply Uninstall=$Uninstall) ==="

# ---- State ----------------------------------------------------------------
$pf86 = ${env:ProgramFiles(x86)}
if (-not $pf86) { $pf86 = "C:\Program Files (x86)" }

$edgeRoot   = Join-Path $pf86 "Microsoft\Edge\Application"
$webviewDir = Join-Path $pf86 "Microsoft\EdgeWebView\Application"

$edgeExe = Join-Path $edgeRoot "msedge.exe"
$edgeVersionDir = $null
if (Test-Path -LiteralPath $edgeRoot) {
    $edgeVersionDir = Get-ChildItem -LiteralPath $edgeRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d+\.' } | Sort-Object Name -Descending | Select-Object -First 1
}
$webviewExe = $null
if (Test-Path -LiteralPath $webviewDir) {
    $webviewExe = Get-ChildItem -LiteralPath $webviewDir -Recurse -Filter "msedgewebview2.exe" -ErrorAction SilentlyContinue |
        Select-Object -First 1
}

Line ("Edge browser    : " + $(if (Test-Path -LiteralPath $edgeExe) { "$edgeExe  (version dir: $($edgeVersionDir.Name))" } else { "not found at $edgeExe" }))
Line ("WebView2 runtime: " + $(if ($webviewExe) { $webviewExe.FullName } else { "NOT FOUND under $webviewDir" }))

# The whole safety argument in one check.
if (-not $webviewExe) {
    Write-Host ""
    Write-Host "STOP: the WebView2 runtime was not found in its own directory." -ForegroundColor Yellow
    Write-Host "The MarwanOS shell renders in WebView2. Removing or blocking Edge on a machine where"
    Write-Host "the runtime cannot be confirmed separately risks taking the shell down with it."
    Write-Host "Confirm the runtime is installed, then re-run."
    Line "REFUSED: WebView2 runtime not confirmed; no changes made."
    exit 3
}

# What the updater actually manages, printed rather than assumed.
foreach ($hive in @("HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients",
                    "HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients")) {
    if (Test-Path $hive) {
        foreach ($c in Get-ChildItem $hive -ErrorAction SilentlyContinue) {
            $n = (Get-ItemProperty $c.PSPath -ErrorAction SilentlyContinue).name
            Line ("EdgeUpdate client: {0}  {1}" -f $c.PSChildName, $n)
        }
    }
}

$regTargets = @(
    "HKLM:\SOFTWARE\Clients\StartMenuInternet\Microsoft Edge",
    "HKLM:\SOFTWARE\WOW6432Node\Clients\StartMenuInternet\Microsoft Edge"
)
foreach ($t in $regTargets) { Line ("present: {0}  {1}" -f $(if (Test-Path $t) { "YES" } else { "no " }), $t) }

# Tagged, because both shortcuts are called "Microsoft Edge.lnk" and moving them into
# one backup folder under their own names would have the second overwrite the first.
$shortcuts = @(
    [pscustomobject]@{ Tag = "desktop";   Path = (Join-Path $env:PUBLIC "Desktop\Microsoft Edge.lnk") },
    [pscustomobject]@{ Tag = "startmenu"; Path = (Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\Microsoft Edge.lnk") }
)
foreach ($s in $shortcuts) { Line ("present: {0}  {1}" -f $(if (Test-Path -LiteralPath $s.Path) { "YES" } else { "no " }), $s.Path) }

if (-not $Apply) {
    Write-Host ""
    Write-Host "DRY RUN. With -Apply this would:" -ForegroundColor Cyan
    Write-Host "    back up + delete Edge's StartMenuInternet registration and RegisteredApplications entry"
    Write-Host "    delete the machine-wide Edge shortcuts listed above"
    Write-Host "    set IFEO Debugger on msedge.exe (msedgewebview2.exe untouched)"
    Write-Host "    set EdgeUpdate InstallDefault=0 and Install$EdgeStableGuid=0"
    if ($Uninstall) {
    Write-Host "    then run Edge's own setup.exe --uninstall --system-level --force-uninstall" -ForegroundColor Yellow
    }
    Write-Host "Re-run with -Apply. Nothing was changed." -ForegroundColor Cyan
    Line "dry run only; no changes made."
    exit 0
}

# ---- 1  Backup ------------------------------------------------------------
New-Item -ItemType Directory -Force $BackupDir | Out-Null
$stamp = (Get-Date).ToString("yyyyMMdd-HHmmss")
foreach ($t in $regTargets) {
    if (-not (Test-Path $t)) { continue }
    $native = $t -replace '^HKLM:\\', 'HKLM\'
    $file = Join-Path $BackupDir ("edge-startmenuinternet-{0}-{1}.reg" -f ($t -replace '[\\: ]', '_'), $stamp)
    & reg.exe export "$native" "$file" /y | Out-Null
    if ($LASTEXITCODE -eq 0) { Line "OK  backed up $t -> $file" } else { Line "WARN reg export failed for $t (exit $LASTEXITCODE)" }
}
try {
    $ra = Get-ItemProperty -Path "HKLM:\SOFTWARE\RegisteredApplications" -Name "Microsoft Edge" -ErrorAction Stop
    Set-Content -LiteralPath (Join-Path $BackupDir "edge-registeredapplications-$stamp.txt") -Value $ra."Microsoft Edge"
    Line "OK  recorded RegisteredApplications\Microsoft Edge = $($ra.'Microsoft Edge')"
} catch { Line "--  no RegisteredApplications\Microsoft Edge entry to record" }

# ---- 2  De-register -------------------------------------------------------
try {
    Remove-ItemProperty -Path "HKLM:\SOFTWARE\RegisteredApplications" -Name "Microsoft Edge" -ErrorAction SilentlyContinue
    Line "OK  removed RegisteredApplications\Microsoft Edge"
} catch { Line "FAIL RegisteredApplications  --  $($_.Exception.Message)" }

foreach ($t in $regTargets) {
    try {
        if (Test-Path $t) { Remove-Item -Path $t -Recurse -Force; Line "OK  removed $t" }
        else              { Line "--  not present: $t" }
    } catch { Line "FAIL removing $t  --  $($_.Exception.Message)" }
}

# ---- 3  Shortcuts ---------------------------------------------------------
foreach ($s in $shortcuts) {
    try {
        if (Test-Path -LiteralPath $s.Path) {
            $dest = Join-Path $BackupDir ("edge-shortcut-{0}-{1}.lnk.bak" -f $s.Tag, $stamp)
            Move-Item -LiteralPath $s.Path -Destination $dest -Force
            Line "OK  moved aside $($s.Path) -> $dest"
        }
    } catch { Line "FAIL moving $($s.Path)  --  $($_.Exception.Message)" }
}

# ---- 4  Block msedge.exe --------------------------------------------------
# Image File Execution Options: Windows starts <Debugger> with the original command line
# appended. systray.exe is a stub that exits immediately, so the launch ends there.
# The key is the FILE NAME ONLY -- "msedge.exe". "msedgewebview2.exe" is a different name
# and gets no key, which is what keeps the shell alive.
$ifeo = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\msedge.exe"
try {
    if (-not (Test-Path $ifeo)) { New-Item -Path $ifeo -Force | Out-Null }
    New-ItemProperty -Path $ifeo -Name "Debugger" -Value "%SystemRoot%\System32\systray.exe" -PropertyType String -Force | Out-Null
    Line "OK  IFEO Debugger set on msedge.exe"
} catch { Line "FAIL IFEO  --  $($_.Exception.Message)" }

if (Test-Path "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\msedgewebview2.exe") {
    Line "WARN an IFEO key exists for msedgewebview2.exe. This script did not create it. Check it -- it would break the shell."
}

# ---- 5  Stop the updater reinstalling the browser -------------------------
try {
    $eu = "HKLM:\SOFTWARE\Policies\Microsoft\EdgeUpdate"
    if (-not (Test-Path $eu)) { New-Item -Path $eu -Force | Out-Null }
    New-ItemProperty -Path $eu -Name "InstallDefault" -Value 0 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -Path $eu -Name ("Install" + $EdgeStableGuid) -Value 0 -PropertyType DWord -Force | Out-Null
    Line "OK  EdgeUpdate InstallDefault=0, Install$EdgeStableGuid=0 (WebView2's client left alone)"
} catch { Line "FAIL EdgeUpdate policy  --  $($_.Exception.Message)" }

# ---- 6  Optional real uninstall -------------------------------------------
if ($Uninstall) {
    if (-not $edgeVersionDir) {
        Line "SKIP uninstall: no versioned Edge directory under $edgeRoot"
    } else {
        $setup = Join-Path $edgeVersionDir.FullName "Installer\setup.exe"
        if (-not (Test-Path -LiteralPath $setup)) {
            Line "SKIP uninstall: $setup not found"
        } else {
            Line "running $setup --uninstall --system-level --verbose-logging --force-uninstall"
            $p = Start-Process -FilePath $setup `
                -ArgumentList "--uninstall", "--system-level", "--verbose-logging", "--force-uninstall" `
                -Wait -PassThru
            Line "uninstaller exit code = $($p.ExitCode)  (a claim, not evidence -- checking disk)"

            Start-Sleep -Seconds 3
            $still = Get-ChildItem -LiteralPath $edgeRoot -Recurse -Filter "msedge.exe" -ErrorAction SilentlyContinue |
                     Select-Object -First 1
            if ($still) {
                Line "RESULT: msedge.exe is STILL on disk at $($still.FullName)."
                Line "        The inbox Edge on this build refused removal. Steps 2-5 still stand: it is"
                Line "        de-registered, hidden and blocked from starting."
            } else {
                Line "RESULT: msedge.exe is gone from $edgeRoot."
            }
        }
    }
    # The runtime, re-checked after the uninstaller ran. This is the line to read.
    $wv = Get-ChildItem -LiteralPath $webviewDir -Recurse -Filter "msedgewebview2.exe" -ErrorAction SilentlyContinue |
          Select-Object -First 1
    if ($wv) { Line "WebView2 runtime after uninstall: STILL PRESENT at $($wv.FullName)  <- the shell is fine" }
    else     { Line "WebView2 runtime after uninstall: MISSING. THE SHELL WILL NOT START. Reinstall the WebView2 Evergreen runtime before signing into the console account." }
}

Line "=== done ==="
Write-Host ""
Write-Host "Edge is no longer registered as a browser on this machine." -ForegroundColor Green
Write-Host "Run 21-set-default-browser.ps1 if you have not already -- otherwise nothing claims http."
Write-Host "A cumulative update can restore Edge's files; the IFEO block and the EdgeUpdate policy"
Write-Host "survive that and keep it from coming back as a browser."
exit 0
