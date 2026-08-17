<#
.SYNOPSIS
    Reverses 20-disable-defender.ps1 and turns Microsoft Defender back on.

.DESCRIPTION
    Undo for the Defender disable. It clears the policy keys, re-enables real-time
    protection and the other MpPreference switches, restores SmartScreen, re-enables
    the scheduled tasks, and sets the services back to their normal start values.

    It CANNOT re-enable Tamper Protection -- that is a manual toggle, and turning it
    back on by hand (same path as before) is the last step to a fully-restored state.

    Idempotent and safe to run even if only some of the disable steps had applied.

.NOTES
    Run ELEVATED. Use -Apply to make changes; without it, dry run.
#>

[CmdletBinding()]
param(
    [switch] $Apply,
    [string] $LogPath = "C:\MarwanOS\defender-disable.log"
)

$ErrorActionPreference = "Stop"

function Line($m) {
    $stamp = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
    Write-Host $m
    try { Add-Content -LiteralPath $LogPath -Value ("{0}  {1}" -f $stamp, $m) -ErrorAction SilentlyContinue } catch {}
}

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$pr = New-Object Security.Principal.WindowsPrincipal($id)
if (-not $pr.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
    Write-Host "ERROR: run this in an ELEVATED PowerShell (Administrator)." -ForegroundColor Red
    exit 2
}

Line "=== 90-restore-defender.ps1  (Apply=$Apply) ==="

if (-not $Apply) {
    Write-Host "DRY RUN. Re-run with -Apply to restore Defender. Nothing was changed." -ForegroundColor Cyan
    exit 0
}

function TrySet($desc, [scriptblock] $do) {
    try { & $do; Line "OK  $desc" } catch { Line "SKIP $desc  --  $($_.Exception.Message)" }
}
function DelReg($path, $name) {
    try {
        if (Test-Path $path) { Remove-ItemProperty -Path $path -Name $name -ErrorAction SilentlyContinue }
        Line "OK  reg cleared $path\$name"
    } catch { Line "SKIP reg $path\$name  --  $($_.Exception.Message)" }
}

# 3  Policy keys back
$def = "HKLM:\SOFTWARE\Policies\Microsoft\Windows Defender"
DelReg $def "DisableAntiSpyware"
DelReg $def "DisableAntiVirus"
DelReg "$def\Real-Time Protection" "DisableRealtimeMonitoring"
DelReg "$def\Real-Time Protection" "DisableBehaviorMonitoring"
DelReg "$def\Real-Time Protection" "DisableScanOnRealtimeEnable"

# 4  SmartScreen back
TrySet "SmartScreen (system policy) back to default" {
    Remove-ItemProperty -Path "HKLM:\SOFTWARE\Policies\Microsoft\Windows\System" -Name "EnableSmartScreen" -ErrorAction SilentlyContinue
}
function SetReg($path, $name, $value, $kind = "DWord") {
    if (-not (Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
    New-ItemProperty -Path $path -Name $name -Value $value -PropertyType $kind -Force | Out-Null
}
TrySet "Explorer SmartScreen on" { SetReg "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer" "SmartScreenEnabled" "RequireAdmin" "String" }

# 6  Services back to default start (WinDefend=2 auto, WdNisSvc=3 manual, Sense=3 manual)
foreach ($pair in @(@("WinDefend",2), @("WdNisSvc",3), @("Sense",3))) {
    TrySet "service $($pair[0]) start=$($pair[1])" { SetReg "HKLM:\SYSTEM\CurrentControlSet\Services\$($pair[0])" "Start" $pair[1] }
}

# 5  Scheduled tasks back
foreach ($t in @("Windows Defender Cache Maintenance","Windows Defender Cleanup","Windows Defender Scheduled Scan","Windows Defender Verification")) {
    TrySet "task enabled $t" { Enable-ScheduledTask -TaskName $t -TaskPath "\Microsoft\Windows\Windows Defender\" -ErrorAction Stop | Out-Null }
}

# 1..2  MpPreference back on
TrySet "real-time monitoring on"   { Set-MpPreference -DisableRealtimeMonitoring $false -ErrorAction Stop }
TrySet "behaviour monitoring on"   { Set-MpPreference -DisableBehaviorMonitoring $false -ErrorAction Stop }
TrySet "on-access (IOAV) on"       { Set-MpPreference -DisableIOAVProtection $false -ErrorAction Stop }
TrySet "script scanning on"        { Set-MpPreference -DisableScriptScanning $false -ErrorAction Stop }
TrySet "archive scanning on"       { Set-MpPreference -DisableArchiveScanning $false -ErrorAction Stop }
TrySet "cloud reporting on"        { Set-MpPreference -MAPSReporting Advanced -ErrorAction Stop }

Line "=== done.  Reboot, then re-enable Tamper Protection by hand in Windows Security. ==="
Write-Host ""
Write-Host "Restored. Reboot, then turn Tamper Protection back ON in Windows Security." -ForegroundColor Green
