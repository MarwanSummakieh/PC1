<#
.SYNOPSIS
    Disables Microsoft Defender antivirus and SmartScreen as far as this machine
    allows, for the MarwanOS "no Microsoft surface" goal.

.DESCRIPTION
    Step in the PC1 de-Microsofting sequence. It removes Defender's active
    protection and its UI presence; it does NOT "uninstall" Defender, because on
    Windows 11 the platform (WinDefend / the Security Center) is a protected OS
    component that cannot be removed and self-heals. What CAN be done, and what
    this does, is turn every part of it off.

    HARD PREREQUISITE, AND WHY THIS SCRIPT REFUSES WITHOUT IT.
    Tamper Protection. While it is ON, Windows rejects every programmatic disable
    of real-time protection -- Set-MpPreference silently no-ops, and the policy
    keys below are ignored -- so a run with Tamper Protection on would report
    success and change nothing. Tamper Protection CANNOT be turned off from a
    script (that is the entire point of it); it is a manual toggle:
        Settings > Privacy & security > Windows Security > Virus & threat
        protection > Manage settings > Tamper Protection = Off
    On a machine joined to a Microsoft account or managed by Intune it may be
    locked on and can only be cleared there. This script DETECTS the state and
    stops with instructions rather than pretending.

    WHAT IT CHANGES (each guarded, each logged, each undone by 90-restore-defender.ps1):
      1. Real-time monitoring off        Set-MpPreference -DisableRealtimeMonitoring $true
                                         plus the behaviour/IOAV/script/email switches.
      2. Cloud + sample submission off   -MAPSReporting Disabled -SubmitSamplesConsent NeverSend
      3. Policy: antivirus disabled      HKLM\SOFTWARE\Policies\Microsoft\Windows Defender
                                         DisableAntiSpyware=1 and the Real-Time Protection subkey.
      4. SmartScreen off                 Explorer + Edge SmartScreen policy keys.
      5. Defender scheduled tasks off    the four \Microsoft\Windows\Windows Defender tasks.
      6. Service start = Disabled        WinDefend / WdNisSvc via the registry Start value
                                         (Set-Service is blocked on these; the reg write is
                                         attempted and its success reported honestly).

    It does NOT touch UAC, the firewall, or any account. It does NOT install a
    replacement. AFTER THIS RUNS THE MACHINE HAS NO ANTIVIRUS until MarwanOS ships
    its own protection -- that is a deliberate, approved choice, recorded here so
    it is never a surprise.

.CONTEXT
    Approved by the user 2026-08-16 ("Yes, script a full disable"), with the
    tradeoffs stated and accepted: no AV until we build our own, Tamper Protection
    must be cleared by hand first, and Vanguard / other anti-cheat may react to a
    machine with Defender off. NOT YET APPLIED. Run elevated, on the marwanshell test
    account's machine state first, never blind on the daily driver.

.UNDO
    provisioning\90-restore-defender.ps1  -- reverses every step below and re-enables
    real-time protection. Re-enabling Tamper Protection afterwards is a manual toggle.

.NOTES
    Run in an ELEVATED PowerShell. Exits non-zero and changes nothing if not
    elevated, or if Tamper Protection is on.
#>

[CmdletBinding()]
param(
    # Set to actually apply. Without it the script is a dry run that only reports
    # what it WOULD do and what the current state is -- safe to run first.
    [switch] $Apply,
    # Log of exactly what changed, for MACHINE-CHANGES.md and for the undo.
    [string] $LogPath = "C:\MarwanOS\defender-disable.log"
)

$ErrorActionPreference = "Stop"

function Line($m) {
    $stamp = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
    Write-Host $m
    try { Add-Content -LiteralPath $LogPath -Value ("{0}  {1}" -f $stamp, $m) -ErrorAction SilentlyContinue } catch {}
}

# ---- Elevation ------------------------------------------------------------
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$pr = New-Object Security.Principal.WindowsPrincipal($id)
if (-not $pr.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
    Write-Host "ERROR: run this in an ELEVATED PowerShell (Administrator). Nothing was changed." -ForegroundColor Red
    exit 2
}

try { New-Item -ItemType Directory -Force (Split-Path $LogPath) -ErrorAction SilentlyContinue | Out-Null } catch {}
Line "=== 20-disable-defender.ps1  (Apply=$Apply) ==="

# ---- Read current state ---------------------------------------------------
$status = $null
try { $status = Get-MpComputerStatus } catch { Line "Get-MpComputerStatus unavailable: $($_.Exception.Message)" }
if ($status) {
    Line ("state: RealTimeProtection={0} Tamper={1} AntivirusEnabled={2} RunningMode={3}" -f `
        $status.RealTimeProtectionEnabled, $status.IsTamperProtected, $status.AntivirusEnabled, $status.AMRunningMode)
}

# ---- The blocker ----------------------------------------------------------
if ($status -and $status.IsTamperProtected) {
    Write-Host ""
    Write-Host "STOP: Tamper Protection is ON. Every disable below would be silently ignored." -ForegroundColor Yellow
    Write-Host "Turn it off by hand first:" -ForegroundColor Yellow
    Write-Host "  Settings > Privacy & security > Windows Security > Virus & threat protection"
    Write-Host "  > Manage settings > Tamper Protection = Off"
    Write-Host "Then re-run this script. (If the toggle is greyed out, this machine's Tamper"
    Write-Host "Protection is managed by a Microsoft account or Intune and must be cleared there.)"
    Line "REFUSED: Tamper Protection on; no changes made."
    exit 3
}

if (-not $Apply) {
    Write-Host ""
    Write-Host "DRY RUN. Re-run with -Apply to make the changes above. Nothing was changed." -ForegroundColor Cyan
    Line "dry run only; no changes made."
    exit 0
}

# ---- 1..2  MpPreference ---------------------------------------------------
function TrySet($desc, [scriptblock] $do) {
    try { & $do; Line "OK  $desc" }
    catch { Line "SKIP $desc  --  $($_.Exception.Message)" }
}

TrySet "real-time monitoring off"        { Set-MpPreference -DisableRealtimeMonitoring $true -ErrorAction Stop }
TrySet "behaviour monitoring off"        { Set-MpPreference -DisableBehaviorMonitoring $true -ErrorAction Stop }
TrySet "on-access (IOAV) off"            { Set-MpPreference -DisableIOAVProtection $true -ErrorAction Stop }
TrySet "script scanning off"             { Set-MpPreference -DisableScriptScanning $true -ErrorAction Stop }
TrySet "archive scanning off"            { Set-MpPreference -DisableArchiveScanning $true -ErrorAction Stop }
TrySet "cloud reporting (MAPS) off"      { Set-MpPreference -MAPSReporting Disabled -ErrorAction Stop }
TrySet "sample submission off"           { Set-MpPreference -SubmitSamplesConsent NeverSend -ErrorAction Stop }

# ---- 3  Policy keys -------------------------------------------------------
function SetReg($path, $name, $value, $kind = "DWord") {
    try {
        if (-not (Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
        New-ItemProperty -Path $path -Name $name -Value $value -PropertyType $kind -Force | Out-Null
        Line "OK  reg $path\$name = $value"
    } catch { Line "SKIP reg $path\$name  --  $($_.Exception.Message)" }
}

$def = "HKLM:\SOFTWARE\Policies\Microsoft\Windows Defender"
SetReg $def "DisableAntiSpyware" 1
SetReg $def "DisableAntiVirus"  1
SetReg "$def\Real-Time Protection" "DisableRealtimeMonitoring" 1
SetReg "$def\Real-Time Protection" "DisableBehaviorMonitoring" 1
SetReg "$def\Real-Time Protection" "DisableScanOnRealtimeEnable" 1

# ---- 4  SmartScreen -------------------------------------------------------
SetReg "HKLM:\SOFTWARE\Policies\Microsoft\Windows\System" "EnableSmartScreen" 0
SetReg "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer" "SmartScreenEnabled" "Off" "String"

# ---- 5  Scheduled tasks ---------------------------------------------------
$tasks = @(
    "\Microsoft\Windows\Windows Defender\Windows Defender Cache Maintenance",
    "\Microsoft\Windows\Windows Defender\Windows Defender Cleanup",
    "\Microsoft\Windows\Windows Defender\Windows Defender Scheduled Scan",
    "\Microsoft\Windows\Windows Defender\Windows Defender Verification"
)
foreach ($t in $tasks) {
    try { Disable-ScheduledTask -TaskName (Split-Path $t -Leaf) -TaskPath ((Split-Path $t) + "\") -ErrorAction Stop | Out-Null
          Line "OK  task disabled $t" }
    catch { Line "SKIP task $t  --  $($_.Exception.Message)" }
}

# ---- 6  Service start values ---------------------------------------------
# Set-Service / sc.exe are blocked on WinDefend by SYSTEM ACLs; the Start value in
# the registry is the reachable lever, and even that may be protected. Attempted
# and reported honestly -- a SKIP here is expected on a locked-down build.
foreach ($svc in @("WinDefend", "WdNisSvc", "Sense")) {
    SetReg "HKLM:\SYSTEM\CurrentControlSet\Services\$svc" "Start" 4
}

Line "=== done.  Reboot for the service/start changes to take effect. ==="
Write-Host ""
Write-Host "Applied. Reboot to settle the service changes." -ForegroundColor Green
Write-Host "Verify after reboot:  Get-MpComputerStatus | Select AMRunningMode,RealTimeProtectionEnabled" -ForegroundColor Green
Write-Host "Undo any time:         provisioning\90-restore-defender.ps1 -Apply" -ForegroundColor Green
