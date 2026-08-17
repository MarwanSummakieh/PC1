<#
.SYNOPSIS
    Undo of 22-remove-edge-browser.ps1. Puts Edge back as a registered, launchable
    browser.

.DESCRIPTION
    Reverses the four reversible steps:
      1. Removes the IFEO "Debugger" entry on msedge.exe, so it starts again.
      2. Removes the EdgeUpdate InstallDefault / Install{Edge Stable} policy values.
      3. Re-imports the StartMenuInternet registration from the newest backup in
         C:\MarwanOS\backup, and restores the RegisteredApplications entry from the
         recorded value.
      4. Moves the machine-wide shortcuts back.

    WHAT IT CANNOT REVERSE: a run of 22 with -Uninstall. That ran Microsoft's own
    uninstaller and deleted Edge's files; no backup here brings them back. If msedge.exe
    is gone, reinstall Edge from Microsoft -- and note that with step 2 undone, the Edge
    updater is permitted to do that on its own again.

.NOTES
    Run in an ELEVATED PowerShell. Dry run by default; -Apply to write.
#>

[CmdletBinding()]
param(
    [switch] $Apply,
    [string] $BackupDir = "C:\MarwanOS\backup",
    [string] $LogPath   = "C:\MarwanOS\remove-edge.log"
)

$ErrorActionPreference = "Stop"
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
Line "=== 88-restore-edge-browser.ps1  (Apply=$Apply) ==="

$regBackups = @(Get-ChildItem -LiteralPath $BackupDir -Filter "edge-startmenuinternet-*.reg" -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending)
$raBackup   = Get-ChildItem -LiteralPath $BackupDir -Filter "edge-registeredapplications-*.txt" -ErrorAction SilentlyContinue |
              Sort-Object LastWriteTime -Descending | Select-Object -First 1
$lnkBackups = @(Get-ChildItem -LiteralPath $BackupDir -Filter "edge-shortcut-*.lnk.bak" -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending)

Line ("registration backups found: " + $regBackups.Count)
Line ("RegisteredApplications value recorded: " + $(if ($raBackup) { $raBackup.Name } else { "none" }))
Line ("shortcut backups found: " + $lnkBackups.Count)

if (-not $Apply) {
    Write-Host ""
    Write-Host "DRY RUN. Re-run with -Apply. Nothing was changed." -ForegroundColor Cyan
    exit 0
}

# 1. Unblock.
$ifeo = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\msedge.exe"
try {
    if (Test-Path $ifeo) { Remove-Item -Path $ifeo -Recurse -Force; Line "OK  removed the IFEO key for msedge.exe" }
    else                 { Line "--  no IFEO key for msedge.exe" }
} catch { Line "FAIL IFEO  --  $($_.Exception.Message)" }

# 2. Updater policy.
try {
    $eu = "HKLM:\SOFTWARE\Policies\Microsoft\EdgeUpdate"
    if (Test-Path $eu) {
        Remove-ItemProperty -Path $eu -Name "InstallDefault" -ErrorAction SilentlyContinue
        Remove-ItemProperty -Path $eu -Name ("Install" + $EdgeStableGuid) -ErrorAction SilentlyContinue
        Line "OK  removed the EdgeUpdate install policy values"
    }
} catch { Line "FAIL EdgeUpdate policy  --  $($_.Exception.Message)" }

# 3. Registration.
# One import per backed-up key; the newest file for each distinct key wins, which is
# what sorting newest-first and skipping keys already restored gives us.
$restored = @{}
foreach ($f in $regBackups) {
    $keyTag = $f.Name -replace '^edge-startmenuinternet-', '' -replace '-\d{8}-\d{6}\.reg$', ''
    if ($restored.ContainsKey($keyTag)) { continue }
    $restored[$keyTag] = $true
    & reg.exe import $f.FullName 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) { Line "OK  imported $($f.Name)" } else { Line "FAIL reg import $($f.Name) (exit $LASTEXITCODE)" }
}
if ($raBackup) {
    try {
        $val = (Get-Content -LiteralPath $raBackup.FullName -Raw).Trim()
        New-ItemProperty -Path "HKLM:\SOFTWARE\RegisteredApplications" -Name "Microsoft Edge" -Value $val -PropertyType String -Force | Out-Null
        Line "OK  restored RegisteredApplications\Microsoft Edge = $val"
    } catch { Line "FAIL RegisteredApplications  --  $($_.Exception.Message)" }
}

# 4. Shortcuts.
# Newest first, one restore per location: re-running 22 leaves several stamped copies.
$done = @{}
foreach ($b in $lnkBackups) {
    $tag = if ($b.Name -like "edge-shortcut-desktop-*") { "desktop" } else { "startmenu" }
    if ($done.ContainsKey($tag)) { continue }
    $done[$tag] = $true
    $dest = if ($tag -eq "desktop") { Join-Path $env:PUBLIC "Desktop\Microsoft Edge.lnk" }
            else { Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\Microsoft Edge.lnk" }
    try { Copy-Item -LiteralPath $b.FullName -Destination $dest -Force; Line "OK  restored $dest" }
    catch { Line "FAIL restoring $dest  --  $($_.Exception.Message)" }
}

# The one that cannot be undone here.
$pf86 = ${env:ProgramFiles(x86)}; if (-not $pf86) { $pf86 = "C:\Program Files (x86)" }
$edgeRoot = Join-Path $pf86 "Microsoft\Edge\Application"
$exe = Get-ChildItem -LiteralPath $edgeRoot -Recurse -Filter "msedge.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($exe) { Line "msedge.exe is on disk at $($exe.FullName) -- Edge is usable again." }
else      { Line "msedge.exe is NOT on disk. 22 was run with -Uninstall; reinstall Edge from Microsoft to finish this undo." }

Line "=== done ==="
exit 0
